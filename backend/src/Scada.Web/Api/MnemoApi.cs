using System.Text.Json;
using Npgsql;
using Scada.Core.Access;
using Scada.Core.Data;
using Scada.Web.Infrastructure;

namespace Scada.Web.Api;

public sealed record MnemoDto(int Id, string Name, int? ParentId, int SortOrder, int CurrentVersion,
    DateTime UpdatedAt, string? UpdatedBy, int? PublishedVersion = null, int AreaId = 1);
public sealed record MnemoCreateRequest(string Name, int? ParentId, int SortOrder, int? AreaId = null);
public sealed record MnemoSaveRequest(JsonElement Doc, int BaseVersion, string? Comment);
public sealed record MnemoVersionDto(int Version, DateTime CreatedAt, string? CreatedBy);

/// <summary>
/// Мнемосхемы: JSON-документ с версиями. Сохранение — новая версия с оптимистической блокировкой
/// (baseVersion должна совпадать с текущей), чтобы два инженера не затёрли правки друг друга.
/// </summary>
public static class MnemoApi
{
    private const int MaxDocBytes = 5 * 1024 * 1024;

    private static readonly string View = Policies.Of(Perm.View);
    private static readonly string Edit = Policies.Of(Perm.MnemoEdit);

    public static void Map(RouteGroupBuilder api)
    {
        var g = api.MapGroup("/mnemos");

        // по умолчанию — опубликованное дерево (для операторов); ?draft=1 — черновик (редактор схем)
        // схемы чужих областей не показываются в дереве
        g.MapGet("", async (int? draft, HttpContext http, NpgsqlDataSource ds, CancellationToken ct) =>
        {
            var perms = await http.PermsAsync();
            var published = await PublishedMnemosAsync(ds, ct);
            if (draft == 1)
            {
                if (!perms.HasAnywhere(Perm.MnemoEdit)) return Results.Forbid();
                return Results.Ok(await ds.QueryAsync(
                    "SELECT id, name, parent_id, sort_order, current_version, updated_at, updated_by, area_id FROM mnemos WHERE NOT deleted " +
                    "AND ($1::int[] IS NULL OR area_id = ANY($1)) ORDER BY parent_id NULLS FIRST, sort_order, name",
                    r => new MnemoDto(r.GetInt32(0), r.GetString(1), r.GetNullableInt(2), r.GetInt32(3), r.GetInt32(4),
                        r.GetUtc(5), r.GetNullableString(6), published.GetValueOrDefault(r.GetInt32(0))?.Version, r.GetInt32(7)), ct,
                    perms.AreasOrAll(Perm.View)));
            }

            var meta = await ds.QueryAsync("SELECT id, updated_at, updated_by FROM mnemos",
                r => (Id: r.GetInt32(0), At: r.GetUtc(1), By: r.GetNullableString(2)), ct);
            var byId = meta.ToDictionary(m => m.Id);
            return Results.Ok(published.Values
                .Where(m => perms.Has(Perm.View, m.AreaId))
                .OrderBy(m => m.ParentId is null ? 0 : 1).ThenBy(m => m.SortOrder).ThenBy(m => m.Name)
                .Select(m => new MnemoDto(m.Id, m.Name, m.ParentId, m.SortOrder, m.Version,
                    byId.TryGetValue(m.Id, out var x) ? x.At : DateTime.UtcNow, byId.TryGetValue(m.Id, out var y) ? y.By : null, m.Version, m.AreaId)));
        }).RequireAuthorization(View);

        // документ: ?version=N — конкретная версия, ?draft=1 — текущая версия черновика, иначе опубликованная
        g.MapGet("/{id:int}", async (int id, int? version, int? draft, HttpContext http, NpgsqlDataSource ds, AccessService access,
            CancellationToken ct) =>
        {
            int? ver = version;
            var published = await PublishedMnemosAsync(ds, ct);
            var area = published.TryGetValue(id, out var pub) && draft != 1 ? pub.AreaId : await access.DraftMnemoAreaAsync(id, ct);
            if (!(await http.PermsAsync()).Has(Perm.View, area)) return Results.NotFound();
            if (ver is null && draft != 1)
            {
                if (pub is null) return Results.NotFound();
                ver = pub.Version;
            }
            var row = await ds.QuerySingleAsync(
                """
                SELECT m.id, m.name, v.version, v.doc::text, m.current_version
                FROM mnemos m
                LEFT JOIN mnemo_versions v ON v.mnemo_id = m.id AND v.version = coalesce($2::int, m.current_version)
                WHERE m.id = $1
                """,
                r => (Id: r.GetInt32(0), Name: r.GetString(1), Version: r.GetNullableInt(2), Doc: r.GetNullableString(3), Current: r.GetInt32(4)),
                ct, id, ver);
            if (row.Name is null) return Results.NotFound();
            var doc = row.Doc is null ? (JsonElement?)null : JsonSerializer.Deserialize<JsonElement>(row.Doc);
            // в редакторе base — версия черновика, от которой идёт правка
            return Results.Ok(new { id = row.Id, name = row.Name, version = draft == 1 ? row.Current : row.Version ?? 0, doc, areaId = area });
        }).RequireAuthorization(View);

        g.MapGet("/{id:int}/versions", async (int id, HttpContext http, NpgsqlDataSource ds, AccessService access, CancellationToken ct) =>
            (await http.PermsAsync()).Has(Perm.View, await access.DraftMnemoAreaAsync(id, ct))
                ? Results.Ok(await ds.QueryAsync(
                    "SELECT version, created_at, created_by FROM mnemo_versions WHERE mnemo_id = $1 ORDER BY version DESC LIMIT 200",
                    r => new MnemoVersionDto(r.GetInt32(0), r.GetUtc(1), r.GetNullableString(2)), ct, id))
                : Results.NotFound())
            .RequireAuthorization(View);

        g.MapPost("", (MnemoCreateRequest req, HttpContext http, NpgsqlDataSource ds, AccessService access, Audit audit, CancellationToken ct) =>
            ConfigApi.Guard(async () =>
            {
                if (string.IsNullOrWhiteSpace(req.Name)) return Results.Problem("Укажите название", statusCode: 400);
                // по умолчанию — область родительской схемы
                var area = req.AreaId ?? (req.ParentId is { } p ? await access.DraftMnemoAreaAsync(p, ct) : 1);
                if (await http.DenyUnlessAsync(Perm.MnemoEdit, area, "mnemo:new") is { } denied) return denied;
                var id = await ds.ScalarAsync(
                    "INSERT INTO mnemos (name, parent_id, sort_order, updated_by, area_id) VALUES ($1,$2,$3,$4,$5) RETURNING id",
                    ct, req.Name.Trim(), req.ParentId, req.SortOrder, http.User.Login(), area);
                await DraftLog.WriteAsync(ds, http, "mnemo", Convert.ToInt32(id), req.Name.Trim(), "create", ct);
                await audit.WriteAsync(http, "config_change", $"mnemo:{id}", new { op = "create", draft = true, req.Name });
                return Results.Ok(new { id });
            })).RequireAuthorization(Edit);

        g.MapPut("/{id:int}", (int id, MnemoCreateRequest req, HttpContext http, NpgsqlDataSource ds, AccessService access, Audit audit,
            CancellationToken ct) => ConfigApi.Guard(async () =>
            {
                if (req.ParentId == id) return Results.Problem("Схема не может быть родителем самой себя", statusCode: 400);
                var oldArea = await access.DraftMnemoAreaAsync(id, ct);
                if (await http.DenyUnlessAsync(Perm.MnemoEdit, oldArea, $"mnemo:{id}") is { } denied) return denied;
                var area = req.AreaId ?? oldArea;
                if (await http.DenyUnlessAsync(Perm.MnemoEdit, area, $"mnemo:{id}") is { } denied2) return denied2;
                var n = await ds.ExecAsync(
                    "UPDATE mnemos SET name=$2, parent_id=$3, sort_order=$4, updated_at=now(), updated_by=$5, area_id=$6, row_version=row_version+1 WHERE id=$1 AND NOT deleted",
                    ct, id, req.Name.Trim(), req.ParentId, req.SortOrder, http.User.Login(), area);
                if (n == 0) return Results.NotFound();
                await DraftLog.WriteAsync(ds, http, "mnemo", id, req.Name.Trim(), "update", ct);
                await audit.WriteAsync(http, "config_change", $"mnemo:{id}", new { op = "rename", draft = true, req.Name, req.ParentId });
                return Results.NoContent();
            })).RequireAuthorization(Edit);

        g.MapPut("/{id:int}/doc", async (int id, MnemoSaveRequest req, HttpContext http, NpgsqlDataSource ds, AccessService access,
            Audit audit, CancellationToken ct) =>
        {
            if (await http.DenyUnlessAsync(Perm.MnemoEdit, await access.DraftMnemoAreaAsync(id, ct), $"mnemo:{id}") is { } denied) return denied;
            if (req.Doc.ValueKind != JsonValueKind.Object) return Results.Problem("Документ должен быть объектом", statusCode: 400);
            var json = req.Doc.GetRawText();
            if (json.Length > MaxDocBytes) return Results.Problem("Схема слишком большая (более 5 МБ)", statusCode: 413);

            await using var conn = await ds.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            int current;
            string mnemoName;
            await using (var lockCmd = new NpgsqlCommand("SELECT current_version, name FROM mnemos WHERE id = $1 AND NOT deleted FOR UPDATE", conn, tx))
            {
                lockCmd.Parameters.Add(Db.P(id));
                await using var lr = await lockCmd.ExecuteReaderAsync(ct);
                if (!await lr.ReadAsync(ct)) return Results.NotFound();
                current = lr.GetInt32(0);
                mnemoName = lr.GetString(1);
            }
            if (current != req.BaseVersion)
                return Results.Problem($"Схему уже изменили (версия {current}). Обновите редактор и повторите.", statusCode: 409);

            // после отката current_version может быть меньше последней сохранённой — номер берём от максимума
            int next;
            await using (var maxCmd = new NpgsqlCommand("SELECT coalesce(max(version), 0) + 1 FROM mnemo_versions WHERE mnemo_id = $1", conn, tx))
            {
                maxCmd.Parameters.Add(Db.P(id));
                next = Convert.ToInt32(await maxCmd.ExecuteScalarAsync(ct));
            }
            await using (var ins = new NpgsqlCommand(
                "INSERT INTO mnemo_versions (mnemo_id, version, doc, created_by) VALUES ($1,$2,$3::jsonb,$4)", conn, tx))
            {
                ins.Parameters.Add(Db.P(id));
                ins.Parameters.Add(Db.P(next));
                ins.Parameters.Add(Db.P(json));
                ins.Parameters.Add(Db.P(http.User.Login()));
                await ins.ExecuteNonQueryAsync(ct);
            }
            await using (var upd = new NpgsqlCommand(
                "UPDATE mnemos SET current_version=$2, updated_at=now(), updated_by=$3 WHERE id=$1", conn, tx))
            {
                upd.Parameters.Add(Db.P(id));
                upd.Parameters.Add(Db.P(next));
                upd.Parameters.Add(Db.P(http.User.Login()));
                await upd.ExecuteNonQueryAsync(ct);
            }
            await using (var log = new NpgsqlCommand(
                "INSERT INTO draft_changes (user_login, object_type, object_id, object_name, op) VALUES ($1,'mnemo',$2,$3,'update')", conn, tx))
            {
                log.Parameters.Add(Db.P(http.User.Login()));
                log.Parameters.Add(Db.P(id));
                log.Parameters.Add(Db.P(mnemoName));
                await log.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
            await audit.WriteAsync(http, "config_change", $"mnemo:{id}", new { op = "save", draft = true, version = next, req.Comment });
            return Results.Ok(new { version = next });
        }).RequireAuthorization(Edit);

        g.MapDelete("/{id:int}", (int id, HttpContext http, NpgsqlDataSource ds, AccessService access, Audit audit, CancellationToken ct) =>
            ConfigApi.Guard(async () =>
            {
                if (await http.DenyUnlessAsync(Perm.MnemoEdit, await access.DraftMnemoAreaAsync(id, ct), $"mnemo:{id}") is { } denied) return denied;
                var name = (string?)await ds.ScalarAsync("SELECT name FROM mnemos WHERE id=$1 AND NOT deleted", ct, id);
                if (name is null) return Results.NotFound();
                await ConfigApi.SoftDelete(ds, "mnemos", id, ct);
                await ds.ExecAsync("UPDATE mnemos SET parent_id = NULL WHERE parent_id = $1 AND NOT deleted", ct, id);
                await DraftLog.WriteAsync(ds, http, "mnemo", id, name, "delete", ct);
                await audit.WriteAsync(http, "config_change", $"mnemo:{id}", new { op = "delete", draft = true });
                return Results.NoContent();
            })).RequireAuthorization(Edit);
    }

    /// <summary>Схемы последней опубликованной версии (id → ссылка на версию).</summary>
    public static async Task<Dictionary<int, Scada.Core.Config.MnemoRef>> PublishedMnemosAsync(NpgsqlDataSource ds, CancellationToken ct)
    {
        var json = (string?)await ds.ScalarAsync(
            "SELECT (snapshot->'mnemos')::text FROM config_versions ORDER BY version DESC LIMIT 1", ct);
        if (json is null) return new();
        var list = JsonSerializer.Deserialize<List<Scada.Core.Config.MnemoRef>>(json, Scada.Core.Config.ConfigSnapshot.Json) ?? [];
        return list.ToDictionary(m => m.Id);
    }
}
