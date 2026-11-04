using System.Text.Json;
using Microsoft.Extensions.Options;
using Npgsql;
using Scada.Core.Access;
using Scada.Core.Config;
using Scada.Core.Data;
using Scada.Drivers.Validation;
using Scada.Web.Infrastructure;

namespace Scada.Web.Api;

public sealed class PublishOptions
{
    /// <summary>Принцип четырёх глаз: публикует не автор правок черновика (для объектов КИИ).</summary>
    public bool FourEyes { get; set; }
    public long ArchiveRowsPerDayLimit { get; set; } = 100_000_000;
}

public sealed record PublishRequest(int BaseVersion, string? Comment, string? Password, bool AcceptWarnings);
public sealed record RollbackRequest(string? Comment, string? Password, bool AcceptWarnings, bool DiscardDraft);
public sealed record DiscardRequest(string? Password);

public sealed record ChangeWithAuthors(ChangeItem Change, string[] Authors, DateTime? LastAt, string[] Areas, bool CanPublish);

/// <summary>
/// Черновик → проверка → применение → откат. Публикация и откат идут под одной блокировкой
/// (advisory lock 'scada.publish') и проверяют номер базовой версии — два инженера
/// не могут опубликовать «поверх» друг друга.
/// </summary>
public sealed class PublishService(NpgsqlDataSource ds, Credentials credentials, AccessService access, Audit audit, IOptions<PublishOptions> options)
{
    private readonly PublishOptions _opt = options.Value;

    private static async Task LockAsync(NpgsqlConnection conn, NpgsqlTransaction tx, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtext('scada.publish'))", conn, tx);
        await cmd.ExecuteScalarAsync(ct);
    }

    private async Task<(int Version, ConfigSnapshot Snapshot)> PublishedAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, CancellationToken ct)
    {
        var p = await DraftStore.LoadPublishedAsync(conn, tx, ct);
        if (p is not null) return p.Value;
        await DraftStore.EnsureInitialVersionAsync(ds, ct);
        return (await DraftStore.LoadPublishedAsync(conn, tx, ct))!.Value;
    }

    private async Task<List<ValidationIssue>> ValidateAsync(NpgsqlConnection conn, NpgsqlTransaction? tx,
        ConfigSnapshot draft, ConfigSnapshot published, CancellationToken ct)
    {
        var docs = await DraftStore.LoadMnemoDocsAsync(conn, tx, draft.Mnemos, ct);
        var areas = (await access.ModelAsync(ct)).Tree.Nodes.Select(n => n.Id).ToHashSet();
        return ConfigValidator.Validate(draft, published, docs,
            new ValidationOptions { ArchiveRowsPerDayLimit = _opt.ArchiveRowsPerDayLimit, KnownAreas = areas });
    }

    /// <summary>Публикация требует права «Публикация» на все области, затронутые черновиком.</summary>
    private async Task<IResult?> CheckAreasAsync(HttpContext http, List<ChangeItem> changes, ConfigSnapshot prev, ConfigSnapshot next,
        string action, CancellationToken ct)
    {
        var perms = await access.ForAsync(http.User, ct);
        var tree = (await access.ModelAsync(ct)).Tree;
        var foreign = changes
            .Select(c => (c, areas: ConfigDiff.ChangeAreas(c, prev, next).Where(a => !perms.Has(Perm.ConfigPublish, a)).ToArray()))
            .Where(x => x.areas.Length > 0)
            .Select(x => new { x.c.ObjectType, x.c.Id, x.c.Name, areas = x.areas.Select(tree.Path).ToArray() })
            .ToList();
        if (foreign.Count == 0) return null;
        await audit.WriteAsync(http, "publish_denied", "config", new { reason = "areas", action, foreign });
        return Results.Json(new
        {
            title = $"Нет права «Публикация» на области изменений ({foreign.Count}). Их должен опубликовать инженер с нужными правами.",
            foreign,
        }, statusCode: 403);
    }

    private static async Task<Dictionary<(string, int), (string[] Users, DateTime At)>> AuthorsAsync(
        NpgsqlConnection conn, NpgsqlTransaction? tx, CancellationToken ct)
    {
        var map = new Dictionary<(string, int), (string[] Users, DateTime At)>();
        await using var cmd = new NpgsqlCommand(
            "SELECT object_type, object_id, array_agg(DISTINCT user_login), max(ts) FROM draft_changes GROUP BY 1, 2", conn, tx);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            map[(r.GetString(0), r.GetInt32(1))] = ((string[])r.GetValue(2), r.GetUtc(3));
        return map;
    }

    public async Task<object> StatusAsync(CancellationToken ct)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        var (version, _) = await PublishedAsync(conn, null, ct);
        var head = await ds.QuerySingleAsync(
            "SELECT created_at, created_by, applied_at FROM config_versions WHERE version = $1",
            r => (At: r.GetUtc(0), By: r.GetString(1), Applied: r.GetNullableDateTime(2)), ct, version);
        var counts = await ds.QuerySingleAsync(
            "SELECT count(DISTINCT (object_type, object_id)), coalesce(array_agg(DISTINCT user_login), '{}') FROM draft_changes",
            r => (Count: r.GetInt64(0), Users: (string[])r.GetValue(1)), ct);
        return new
        {
            publishedVersion = version, publishedAt = head.At, publishedBy = head.By, appliedAt = head.Applied,
            draftChanges = counts.Count, draftAuthors = counts.Users,
        };
    }

    public async Task<object> DiffAsync(HttpContext http, CancellationToken ct)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        var (version, published) = await PublishedAsync(conn, null, ct);
        var draft = await DraftStore.LoadDraftAsync(conn, null, ct);
        var changes = ConfigDiff.Compute(published, draft);
        var authors = await AuthorsAsync(conn, null, ct);
        var issues = await ValidateAsync(conn, null, draft, published, ct);
        var perms = await access.ForAsync(http.User, ct);
        var tree = (await access.ModelAsync(ct)).Tree;
        return new
        {
            baseVersion = version,
            summary = ConfigDiff.Summary(changes),
            changes = changes.Select(c =>
            {
                var a = authors.GetValueOrDefault((c.ObjectType, c.Id));
                var areas = ConfigDiff.ChangeAreas(c, published, draft);
                return new ChangeWithAuthors(c, a.Users ?? [], a.Users is null ? null : a.At,
                    areas.Select(tree.Path).ToArray(), areas.All(x => perms.Has(Perm.ConfigPublish, x)));
            }),
            errors = issues.Where(i => i.Level == ValidationIssue.Error),
            warnings = issues.Where(i => i.Level == ValidationIssue.Warning),
        };
    }

    private async Task<IResult?> CheckPasswordAsync(HttpContext http, string? password, CancellationToken ct)
    {
        if (!await credentials.VerifyAsync(http.User.Login(), password, ct))
        {
            await audit.WriteAsync(http, "publish_denied", "config", new { reason = "password" });
            return Results.Problem("Неверный пароль. Для публикации повторно введите свой пароль.", statusCode: 403);
        }
        return null;
    }

    /// <summary>Общая часть публикации и отката: сравнение, проверки, запись версии. Транзакция открыта вызывающим.</summary>
    private async Task<IResult> CommitVersionAsync(NpgsqlConnection conn, NpgsqlTransaction tx, HttpContext http,
        int baseVersion, ConfigSnapshot published, string comment, bool acceptWarnings, int? rollbackOf, CancellationToken ct)
    {
        var draft = await DraftStore.LoadDraftAsync(conn, tx, ct);
        var changes = ConfigDiff.Compute(published, draft);
        if (changes.Count == 0)
            return Results.Problem("В черновике нет изменений относительно опубликованной версии.", statusCode: 400);

        if (await CheckAreasAsync(http, changes, published, draft, rollbackOf is null ? "publish" : "rollback", ct) is { } denied) return denied;

        var issues = await ValidateAsync(conn, tx, draft, published, ct);
        var errors = issues.Where(i => i.Level == ValidationIssue.Error).ToList();
        var warnings = issues.Where(i => i.Level == ValidationIssue.Warning).ToList();
        if (errors.Count > 0)
        {
            await audit.WriteAsync(http, "publish_denied", "config", new { reason = "errors", errors = errors.Count });
            return Results.Json(new { title = $"Публикация невозможна: ошибок {errors.Count}", errors, warnings }, statusCode: 422);
        }
        if (warnings.Count > 0 && !acceptWarnings)
            return Results.Json(new { title = "Подтвердите, что ознакомились с предупреждениями", errors, warnings }, statusCode: 428);

        var user = http.User.Login();
        string[] authors;
        await using (var a = new NpgsqlCommand("SELECT coalesce(array_agg(DISTINCT user_login), '{}') FROM draft_changes", conn, tx))
            authors = (string[])(await a.ExecuteScalarAsync(ct))!;
        if (_opt.FourEyes && rollbackOf is null && authors.Contains(user))
            return Results.Problem("Включён принцип четырёх глаз: публикует инженер, который не вносил эти правки.", statusCode: 403);

        var next = baseVersion + 1;
        await using (var ins = new NpgsqlCommand(
            "INSERT INTO config_versions (version, created_by, comment, snapshot, changes, warnings, rollback_of) " +
            "VALUES ($1, $2, $3, $4::jsonb, $5::jsonb, $6::jsonb, $7)", conn, tx))
        {
            ins.Parameters.Add(Db.P(next));
            ins.Parameters.Add(Db.P(user));
            ins.Parameters.Add(Db.P(comment));
            ins.Parameters.Add(Db.P(draft.Serialize()));
            ins.Parameters.Add(Db.P(JsonSerializer.Serialize(changes, ConfigSnapshot.Json)));
            ins.Parameters.Add(Db.P(JsonSerializer.Serialize(warnings, ConfigSnapshot.Json)));
            ins.Parameters.Add(Db.P(rollbackOf));
            await ins.ExecuteNonQueryAsync(ct);
        }
        await using (var clear = new NpgsqlCommand("DELETE FROM draft_changes", conn, tx)) await clear.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);

        var summary = ConfigDiff.Summary(changes);
        await audit.WriteAsync(http, rollbackOf is null ? "config_publish" : "config_rollback", $"config:{next}", new
        {
            version = next, baseVersion, rollbackOf, comment, summary, warningsAccepted = warnings.Select(w => w.Message), authors,
            changes = changes.Select(c => new { c.ObjectType, c.Id, c.Name, c.Op, fields = c.Fields.Select(f => f.Field) }),
        });
        return Results.Ok(new { version = next, summary, warnings = warnings.Count });
    }

    public async Task<IResult> PublishAsync(PublishRequest req, HttpContext http, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Comment)) return Results.Problem("Укажите причину изменения (номер заявки или наряда).", statusCode: 400);
        if (await CheckPasswordAsync(http, req.Password, ct) is { } denied) return denied;

        await using var conn = await ds.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await LockAsync(conn, tx, ct);
        var (version, published) = await PublishedAsync(conn, tx, ct);
        if (version != req.BaseVersion)
            return Results.Problem($"Пока вы проверяли изменения, опубликована версия {version}. Обновите страницу и проверьте список ещё раз.", statusCode: 409);
        return await CommitVersionAsync(conn, tx, http, version, published, req.Comment.Trim(), req.AcceptWarnings, null, ct);
    }

    public async Task<IResult> RollbackAsync(int target, RollbackRequest req, HttpContext http, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Comment)) return Results.Problem("Укажите причину отката.", statusCode: 400);
        if (await CheckPasswordAsync(http, req.Password, ct) is { } denied) return denied;

        await using var conn = await ds.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await LockAsync(conn, tx, ct);
        var (version, published) = await PublishedAsync(conn, tx, ct);
        if (target == version) return Results.Problem("Эта версия уже работает.", statusCode: 400);
        var old = await DraftStore.LoadPublishedAsync(conn, tx, ct, target);
        if (old is null) return Results.NotFound();

        var draft = await DraftStore.LoadDraftAsync(conn, tx, ct);
        if (ConfigDiff.Compute(published, draft).Count > 0 && !req.DiscardDraft)
            return Results.Problem("В черновике есть неопубликованные правки. Отметьте «Сбросить черновик» или сначала опубликуйте их.", statusCode: 409);

        await DraftStore.RestoreAsync(conn, tx, old.Value.Snapshot, ct);
        var comment = $"Откат к версии {target}: {req.Comment.Trim()}";
        return await CommitVersionAsync(conn, tx, http, version, published, comment, req.AcceptWarnings, target, ct);
    }

    public async Task<IResult> DiscardAsync(DiscardRequest req, HttpContext http, CancellationToken ct)
    {
        if (await CheckPasswordAsync(http, req.Password, ct) is { } denied) return denied;
        await using var conn = await ds.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await LockAsync(conn, tx, ct);
        var (version, published) = await PublishedAsync(conn, tx, ct);
        var draft = await DraftStore.LoadDraftAsync(conn, tx, ct);
        var changes = ConfigDiff.Compute(published, draft);
        if (await CheckAreasAsync(http, changes, published, draft, "discard", ct) is { } areaDenied) return areaDenied;
        string[] authors;
        await using (var a = new NpgsqlCommand("SELECT coalesce(array_agg(DISTINCT user_login), '{}') FROM draft_changes", conn, tx))
            authors = (string[])(await a.ExecuteScalarAsync(ct))!;
        await DraftStore.RestoreAsync(conn, tx, published, ct);
        await tx.CommitAsync(ct);
        await audit.WriteAsync(http, "draft_discard", $"config:{version}", new
        {
            version, authors, discarded = changes.Select(c => new { c.ObjectType, c.Id, c.Name, c.Op }),
        });
        return Results.Ok(new { discarded = changes.Count });
    }
}

public static class PublishApi
{
    public static void Map(RouteGroupBuilder api)
    {
        var engineer = Policies.Of(Perm.ConfigEdit, Perm.MnemoEdit, Perm.ConfigPublish);
        var publisher = Policies.Of(Perm.ConfigPublish);
        api.MapGet("/draft/status", (PublishService svc, CancellationToken ct) => svc.StatusAsync(ct))
            .RequireAuthorization(Policies.Of(Perm.View));
        api.MapGet("/draft/diff", (HttpContext http, PublishService svc, CancellationToken ct) => svc.DiffAsync(http, ct))
            .RequireAuthorization(engineer);
        // проверки без публикации (кнопка «Проверить»)
        api.MapPost("/draft/validate", (HttpContext http, PublishService svc, CancellationToken ct) => svc.DiffAsync(http, ct))
            .RequireAuthorization(engineer);
        api.MapPost("/draft/discard", (DiscardRequest req, HttpContext http, PublishService svc, CancellationToken ct) =>
            svc.DiscardAsync(req, http, ct)).RequireAuthorization(publisher);
        api.MapPost("/publish", (PublishRequest req, HttpContext http, PublishService svc, CancellationToken ct) =>
            svc.PublishAsync(req, http, ct)).RequireAuthorization(publisher);
        api.MapPost("/versions/{n:int}/rollback", (int n, RollbackRequest req, HttpContext http, PublishService svc, CancellationToken ct) =>
            svc.RollbackAsync(n, req, http, ct)).RequireAuthorization(publisher);

        api.MapGet("/versions", async (int? limit, NpgsqlDataSource ds, CancellationToken ct) =>
            await ds.QueryAsync(
                "SELECT version, created_at, created_by, comment, rollback_of, applied_at, applied_ms, changes::text, " +
                "jsonb_array_length(warnings) FROM config_versions ORDER BY version DESC LIMIT $1",
                r => new
                {
                    version = r.GetInt32(0), createdAt = r.GetUtc(1), createdBy = r.GetString(2), comment = r.GetString(3),
                    rollbackOf = r.GetNullableInt(4), appliedAt = r.GetNullableDateTime(5), appliedMs = r.GetNullableInt(6),
                    summary = ConfigDiff.Summary(JsonSerializer.Deserialize<List<ChangeItem>>(r.GetString(7), ConfigSnapshot.Json) ?? []),
                    warnings = r.GetInt32(8),
                }, ct, Math.Clamp(limit ?? 100, 1, 1000)))
            .RequireAuthorization(engineer);

        api.MapGet("/versions/{n:int}", async (int n, NpgsqlDataSource ds, CancellationToken ct) =>
        {
            var v = await ds.QuerySingleAsync(
                "SELECT version, created_at, created_by, comment, rollback_of, applied_at, applied_ms, changes::text, warnings::text " +
                "FROM config_versions WHERE version = $1",
                r => new
                {
                    version = r.GetInt32(0), createdAt = r.GetUtc(1), createdBy = r.GetString(2), comment = r.GetString(3),
                    rollbackOf = r.GetNullableInt(4), appliedAt = r.GetNullableDateTime(5), appliedMs = r.GetNullableInt(6),
                    changes = JsonSerializer.Deserialize<JsonElement>(r.GetString(7)),
                    warnings = JsonSerializer.Deserialize<JsonElement>(r.GetString(8)),
                }, ct, n);
            return v is null ? Results.NotFound() : Results.Ok(v);
        }).RequireAuthorization(engineer);
    }
}
