using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Npgsql;
using Scada.Core.Access;
using Scada.Core.Data;
using Scada.Core.Model;
using Scada.Core.Notify;
using Scada.Web.Infrastructure;
using Scada.Web.Realtime;

namespace Scada.Web.Api;

public sealed class CommandOptions
{
    /// <summary>Срок жизни команды. Не исполненная за это время команда не будет исполнена никогда.</summary>
    public int TtlSeconds { get; set; } = 15;
    /// <summary>Сколько веб ждёт результата для ответа клиенту.</summary>
    public int WaitSeconds { get; set; } = 20;
    /// <summary>Служба сбора считается недоступной, если нет heartbeat дольше этого.</summary>
    public int AcquisitionStaleSeconds { get; set; } = 20;
    /// <summary>Сколько критичная команда ждёт второго подтверждающего.</summary>
    public int ConfirmSeconds { get; set; } = 60;
    /// <summary>
    /// second_user — второй пользователь с правом «Критичные команды» с другого сеанса;
    /// permit — объект с одним дежурным: повторный пароль и номер наряда (решение заказчика).
    /// </summary>
    public string CriticalConfirmMode { get; set; } = "second_user";
}

public sealed record CommandRequest(int TagId, double? Value, string? ValueStr, bool Confirmed,
    string? Password = null, string? Comment = null, string? PermitCode = null);
public sealed record CommandConfirmRequest(string? Password, bool Approve = true, string? Comment = null);
public sealed record AwaitingCommandDto(long Id, int TagId, string TagName, string TagDescription, string Unit, double? ValueNum,
    string? ValueStr, double? CurrentValue, string RequestedBy, DateTime RequestedAt, DateTime ExpiresAt, string? Comment, string AreaPath);
public sealed record AlarmActionRequest(int[] Ids, string? Comment, int? Minutes);

public sealed record ActiveAlarmDto(
    int Id, int TagId, string TagName, string TagDescription, string Unit, string Kind, short Priority, string Message,
    bool Active, bool Acked, double? Value, DateTime? RaisedAt, DateTime? ClearedAt, DateTime? AckedAt, string? AckedBy,
    DateTime? ShelvedUntil, string? Comment);

public sealed record AlarmEventDto(
    long Id, DateTime Ts, int AlarmId, string Event, double? Value, string? User, string? Comment,
    string TagName, string Message, short Priority);

public sealed record CommandDto(
    long Id, int TagId, string TagName, double? ValueNum, string? ValueStr, string RequestedBy, DateTime RequestedAt,
    DateTime ExpiresAt, string Status, string? Result, double? ReadbackNum, DateTime? FinishedAt,
    string CommandClass = "normal", string? Comment = null, string? ConfirmedBy = null);

public static class RuntimeApi
{
    public static void Map(RouteGroupBuilder api)
    {
        // ------------------------------------------------------------ текущие значения
        api.MapGet("/values", async (string? ids, HttpContext http, RealtimeService rt, AccessService access, CancellationToken ct) =>
        {
            var runtime = await access.RuntimeAsync(ct);
            var perms = await http.PermsAsync();
            var list = ids is null ? runtime.Tags.Keys : ParseIds(ids);
            return Results.Ok(rt.Snapshot(runtime.Allowed(perms, list)));
        }).RequireAuthorization(View);

        // ------------------------------------------------------------ архив / тренды
        api.MapGet("/history", async (string tagIds, DateTimeOffset from, DateTimeOffset to, int? points,
            HttpContext http, NpgsqlDataSource ds, AccessService access, CancellationToken ct) =>
        {
            var ids = (await access.RuntimeAsync(ct)).Allowed(await http.PermsAsync(), ParseIds(tagIds));
            if (ids.Length == 0 || ids.Length > 16) return Results.Problem("Укажите от 1 до 16 тегов", statusCode: 400);
            if (to <= from) return Results.Problem("Неверный интервал", statusCode: 400);
            var span = to - from;
            if (span > TimeSpan.FromDays(400)) return Results.Problem("Интервал не более 400 суток", statusCode: 400);

            var pts = Math.Clamp(points ?? 1000, 50, 5000);
            var bucket = Math.Max(1, (int)Math.Ceiling(span.TotalSeconds / pts));
            var result = ids.ToDictionary(id => id, _ => new List<object?[]>());

            if (bucket <= 1)
            {
                // сырые значения
                var rows = await ds.QueryAsync(
                    "SELECT tag_id, ts, num, quality FROM tag_history WHERE tag_id = ANY($1) AND ts >= $2 AND ts <= $3 " +
                    "ORDER BY tag_id, ts LIMIT 200000",
                    r => (r.GetInt32(0), r.GetUtc(1), r.GetNullableDouble(2), r.GetInt16(3)), ct,
                    ids, from.UtcDateTime, to.UtcDateTime);
                foreach (var (id, ts, num, q) in rows)
                    result[id].Add([Ms(ts), num, num, num, q]);
                return Results.Ok(new { mode = "raw", bucketSeconds = 0, series = result });
            }

            // агрегаты по корзинам: среднее, минимум, максимум, худшее качество
            var agg = await ds.QueryAsync(
                """
                SELECT tag_id, date_bin(make_interval(secs => $4), ts, $2) AS b,
                       avg(num), min(num), max(num), max(quality)
                FROM tag_history
                WHERE tag_id = ANY($1) AND ts >= $2 AND ts <= $3
                GROUP BY 1, 2 ORDER BY 1, 2
                """,
                r => (r.GetInt32(0), r.GetUtc(1), r.GetNullableDouble(2), r.GetNullableDouble(3),
                      r.GetNullableDouble(4), r.GetInt16(5)), ct,
                ids, from.UtcDateTime, to.UtcDateTime, (double)bucket);
            foreach (var (id, ts, avg, min, max, q) in agg)
                result[id].Add([Ms(ts), avg, min, max, q]);
            return Results.Ok(new { mode = "aggregate", bucketSeconds = bucket, series = result });
        }).RequireAuthorization(View);

        api.MapGet("/history/export", async (int tagId, DateTimeOffset from, DateTimeOffset to, NpgsqlDataSource ds,
            HttpContext http, AccessService access, Audit audit, CancellationToken ct) =>
        {
            if (await http.DenyUnlessAsync(Perm.View, (await access.RuntimeAsync(ct)).TagArea(tagId), $"history:{tagId}") is { } denied) return denied;
            var rows = await ds.QueryAsync(
                "SELECT ts, num, str, quality FROM tag_history WHERE tag_id = $1 AND ts >= $2 AND ts <= $3 ORDER BY ts LIMIT 1000000",
                r => string.Join(';',
                    r.GetUtc(0).ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
                    r.IsDBNull(1) ? "" : r.GetDouble(1).ToString("R", CultureInfo.InvariantCulture),
                    r.IsDBNull(2) ? "" : r.GetString(2).Replace(';', ','),
                    r.GetInt16(3).ToString(CultureInfo.InvariantCulture)), ct,
                tagId, from.UtcDateTime, to.UtcDateTime);
            await audit.WriteAsync(http, "export", $"history:{tagId}", new { from, to, rows = rows.Count });
            var csv = "ts_utc;num;str;quality\n" + string.Join("\n", rows) + "\n";
            return Results.File(System.Text.Encoding.UTF8.GetBytes(csv), "text/csv; charset=utf-8", $"history_{tagId}.csv");
        }).RequireAuthorization(View);

        // ------------------------------------------------------------ тревоги
        api.MapGet("/alarms/active", async (HttpContext http, NpgsqlDataSource ds, CancellationToken ct) =>
            await ds.QueryAsync(
                """
                SELECT a.alarm_def_id, d.tag_id, t.name, t.description, t.unit, d.kind, d.priority, d.message,
                       a.active, a.acked, a.value, a.raised_at, a.cleared_at, a.acked_at, a.acked_by, a.shelved_until, a.comment
                FROM alarm_active a
                JOIN alarm_defs d ON d.id = a.alarm_def_id
                JOIN tags t ON t.id = d.tag_id
                JOIN devices dv ON dv.id = t.device_id
                WHERE (a.active OR NOT a.acked OR a.shelved_until > now())
                  AND ($1::int[] IS NULL OR coalesce(t.area_id, dv.area_id) = ANY($1))
                ORDER BY (a.active AND NOT a.acked) DESC, d.priority, a.raised_at DESC
                """,
                r => new ActiveAlarmDto(r.GetInt32(0), r.GetInt32(1), r.GetString(2), r.GetString(3), r.GetString(4),
                    r.GetString(5), r.GetInt16(6), r.GetString(7), r.GetBoolean(8), r.GetBoolean(9),
                    r.GetNullableDouble(10), r.GetNullableDateTime(11), r.GetNullableDateTime(12),
                    r.GetNullableDateTime(13), r.GetNullableString(14), r.GetNullableDateTime(15),
                    r.GetNullableString(16)), ct, (await http.PermsAsync()).AreasOrAll(Perm.View)))
            .RequireAuthorization(View);

        api.MapGet("/alarms/events", async (DateTimeOffset? from, DateTimeOffset? to, int? limit,
            HttpContext http, NpgsqlDataSource ds, CancellationToken ct) =>
            await ds.QueryAsync(
                """
                SELECT e.id, e.ts, e.alarm_def_id, e.event, e.value, e.user_login, e.comment,
                       coalesce(t.name, '?'), coalesce(d.message, ''), coalesce(d.priority, 4)
                FROM alarm_events e
                LEFT JOIN alarm_defs d ON d.id = e.alarm_def_id
                LEFT JOIN tags t ON t.id = d.tag_id
                LEFT JOIN devices dv ON dv.id = t.device_id
                WHERE e.ts >= $1 AND e.ts <= $2 AND ($4::int[] IS NULL OR coalesce(t.area_id, dv.area_id) = ANY($4))
                ORDER BY e.ts DESC LIMIT $3
                """,
                r => new AlarmEventDto(r.GetInt64(0), r.GetUtc(1), r.GetInt32(2), r.GetString(3), r.GetNullableDouble(4),
                    r.GetNullableString(5), r.GetNullableString(6), r.GetString(7), r.GetString(8), r.GetInt16(9)), ct,
                (from ?? DateTimeOffset.UtcNow.AddDays(-1)).UtcDateTime, (to ?? DateTimeOffset.UtcNow).UtcDateTime,
                Math.Clamp(limit ?? 500, 1, 5000), (await http.PermsAsync()).AreasOrAll(Perm.View)))
            .RequireAuthorization(View);

        api.MapPost("/alarms/ack", (AlarmActionRequest req, HttpContext http, NpgsqlDataSource ds, RealtimeService rt,
            IOptions<CommandOptions> opt, Audit audit, CancellationToken ct) =>
            SendAlarmAction(req, "ack", Perm.AlarmAck, http, ds, rt, opt.Value, audit, ct))
            .RequireAuthorization(Policies.Of(Perm.AlarmAck));

        api.MapPost("/alarms/shelve", (AlarmActionRequest req, HttpContext http, NpgsqlDataSource ds, RealtimeService rt,
            IOptions<CommandOptions> opt, Audit audit, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Comment))
                return Task.FromResult(Results.Problem("Для снятия с контроля обязателен комментарий", statusCode: 400));
            return SendAlarmAction(req, "shelve", Perm.AlarmShelve, http, ds, rt, opt.Value, audit, ct);
        }).RequireAuthorization(Policies.Of(Perm.AlarmShelve));

        api.MapPost("/alarms/unshelve", (AlarmActionRequest req, HttpContext http, NpgsqlDataSource ds, RealtimeService rt,
            IOptions<CommandOptions> opt, Audit audit, CancellationToken ct) =>
            SendAlarmAction(req, "unshelve", Perm.AlarmShelve, http, ds, rt, opt.Value, audit, ct))
            .RequireAuthorization(Policies.Of(Perm.AlarmShelve));

        // ------------------------------------------------------------ команды
        // Класс критичности тега задаёт право и способ подтверждения:
        //   обычный — диалог; важный — + пароль и комментарий; критичный — + второй пользователь за ConfirmSeconds.
        api.MapPost("/commands", async (CommandRequest req, HttpContext http, NpgsqlDataSource ds, RealtimeService rt,
            CommandWaiter waiter, AccessService access, Credentials credentials, IDirectory directory,
            IOptions<CommandOptions> opt, Audit audit, CancellationToken ct) =>
        {
            var o = opt.Value;
            var runtime = await access.RuntimeAsync(ct);
            // свойства тега — из опубликованной версии, по которой работает служба сбора
            if (!runtime.Tags.TryGetValue(req.TagId, out var tag)) return Results.NotFound();
            var area = runtime.TagArea(tag.Id);
            var cls = tag.CommandClass;
            var perm = AccessCalculator.CommandPermission(cls, tag.DataType == TagDataType.Bool);
            if (!tag.Enabled || !tag.Writable) return Results.Problem("Тег недоступен для записи", statusCode: 403);

            var perms = await http.PermsAsync();
            if (cls != CommandClass.Normal && http.User.IsDomain())
            {
                // перед важной и критичной командой группы AD проверяются заново
                var fresh = await directory.LookupAsync(http.User.Login(), ct);
                if (fresh.Status == DirectoryStatus.Ok) perms = await access.ForAsync(http.User.Login(), fresh.User!.Groups, ct);
                else if (fresh.Status != DirectoryStatus.Unavailable)
                    return Results.Problem("Учётная запись отключена или удалена в домене", statusCode: 403);
            }
            var model = await access.ModelAsync(ct);
            if (!perms.Has(perm, area))
            {
                await audit.WriteAsync(http, "access_denied", $"tag:{tag.Id}", new { perm, area = model.Tree.Path(area), tag = tag.Name, commandClass = cls.ToCode() });
                return Results.Problem($"Нет права «{Perm.Names[perm]}» на область «{model.Tree.Path(area)}»", statusCode: 403);
            }

            if ((cls == CommandClass.Normal && tag.WriteConfirm || cls != CommandClass.Normal) && !req.Confirmed)
                return Results.Problem("Требуется подтверждение оператора", statusCode: 428);
            if (cls != CommandClass.Normal)
            {
                if (string.IsNullOrWhiteSpace(req.Comment)) return Results.Problem("Для этой команды обязателен комментарий", statusCode: 400);
                if (!await credentials.VerifyAsync(http.User.Login(), req.Password, ct))
                {
                    await audit.WriteAsync(http, "command_denied", $"tag:{tag.Id}", new { reason = "password", tag = tag.Name });
                    return Results.Problem("Неверный пароль", statusCode: 403);
                }
            }
            var permit = cls == CommandClass.Critical && o.CriticalConfirmMode == "permit";
            if (permit && string.IsNullOrWhiteSpace(req.PermitCode))
                return Results.Problem("Укажите номер наряда-допуска", statusCode: 400);
            if (tag.DataType == TagDataType.String ? req.ValueStr is null : req.Value is null)
                return Results.Problem("Не задано значение", statusCode: 400);
            if (req.Value is { } v && (v < tag.WriteMin || v > tag.WriteMax))
                return Results.Problem($"Значение вне диапазона [{tag.WriteMin}; {tag.WriteMax}]", statusCode: 400);
            if (!AcquisitionAlive(rt, o))
                return Results.Problem("Служба сбора недоступна — команда не отправлена", statusCode: 503);

            var awaiting = cls == CommandClass.Critical && !permit;
            var id = Convert.ToInt64(await ds.ScalarAsync(
                "INSERT INTO commands (tag_id, value_num, value_str, requested_by, expires_at, client_ip, status, command_class, comment, " +
                "requested_session, confirm_mode, permit_code) " +
                "VALUES ($1, $2, $3, $4, now() + make_interval(secs => $5), $6, $7, $8, $9, $10, $11, $12) RETURNING id", ct,
                req.TagId, tag.DataType == TagDataType.String ? null : req.Value, tag.DataType == TagDataType.String ? req.ValueStr : null,
                http.User.Login(), (double)(awaiting ? o.ConfirmSeconds : o.TtlSeconds), http.Ip(),
                awaiting ? "awaiting_confirm" : "pending", cls.ToCode(), req.Comment?.Trim(), http.User.SessionId(),
                cls == CommandClass.Critical ? (permit ? "permit" : "second_user") : null, permit ? req.PermitCode!.Trim() : null));

            var details = new
            {
                command = id, tag = tag.Name, value = req.Value, valueStr = req.ValueStr, commandClass = cls.ToCode(), comment = req.Comment,
                confirmation = cls switch
                {
                    CommandClass.Normal => tag.WriteConfirm ? "dialog" : "none",
                    CommandClass.Important => "password+comment",
                    _ => permit ? "password+comment+permit" : "password+comment+second_user",
                },
                permitCode = permit ? req.PermitCode : null, via = perms.Via(perm, area, model.Tree),
            };
            if (awaiting)
            {
                await audit.WriteAsync(http, "command_requested", $"tag:{req.TagId}", details);
                return Results.Json(new { id, status = "awaiting_confirm", confirmSeconds = o.ConfirmSeconds }, statusCode: 202);
            }

            var (status, result) = await WaitAsync(ds, waiter, id, o, ct);
            await audit.WriteAsync(http, "command", $"tag:{req.TagId}", new { details, status, result });
            return Results.Ok(new { id, status, result });
        }).RequireAuthorization(Policies.Of(Perm.CmdControl, Perm.CmdSetpoint, Perm.CmdCritical));

        api.MapGet("/commands/settings", (IOptions<CommandOptions> opt) => new
        {
            ttlSeconds = opt.Value.TtlSeconds, confirmSeconds = opt.Value.ConfirmSeconds, criticalConfirmMode = opt.Value.CriticalConfirmMode,
        }).RequireAuthorization();

        // запросы на второе подтверждение, которые может подтвердить текущий пользователь
        api.MapGet("/commands/awaiting", async (HttpContext http, NpgsqlDataSource ds, AccessService access, RealtimeService rt,
            CancellationToken ct) =>
        {
            var perms = await http.PermsAsync();
            var runtime = await access.RuntimeAsync(ct);
            var tree = (await access.ModelAsync(ct)).Tree;
            var rows = await ds.QueryAsync(
                "SELECT id, tag_id, value_num, value_str, requested_by, requested_at, expires_at, comment FROM commands " +
                "WHERE status = 'awaiting_confirm' AND expires_at > now() AND lower(requested_by) <> lower($1) ORDER BY id",
                r => (Id: r.GetInt64(0), Tag: r.GetInt32(1), Num: r.GetNullableDouble(2), Str: r.GetNullableString(3), By: r.GetString(4),
                      At: r.GetUtc(5), Until: r.GetUtc(6), Comment: r.GetNullableString(7)), ct, http.User.Login());
            return rows.Where(x => perms.Has(Perm.CmdCritical, runtime.TagArea(x.Tag)) && runtime.Tags.ContainsKey(x.Tag))
                .Select(x =>
                {
                    var t = runtime.Tags[x.Tag];
                    return new AwaitingCommandDto(x.Id, x.Tag, t.Name, t.Description, t.Unit, x.Num, x.Str,
                        rt.TryGet(x.Tag, out var cur) ? cur.Num : null, x.By, x.At, x.Until, x.Comment, tree.Path(runtime.TagArea(x.Tag)));
                });
        }).RequireAuthorization(Policies.Of(Perm.CmdCritical));

        api.MapPost("/commands/{id:long}/confirm", async (long id, CommandConfirmRequest req, HttpContext http, NpgsqlDataSource ds,
            AccessService access, Credentials credentials, IOptions<CommandOptions> opt, Audit audit, CancellationToken ct) =>
        {
            var c = await ds.QuerySingleAsync(
                "SELECT tag_id, requested_by, requested_session, status, expires_at > now(), value_num, value_str FROM commands WHERE id = $1",
                r => (Tag: r.GetInt32(0), By: r.GetString(1), Session: r.GetNullableString(2), Status: r.GetString(3), Alive: r.GetBoolean(4),
                      Num: r.GetNullableDouble(5), Str: r.GetNullableString(6)), ct, id);
            if (c.By is null) return Results.NotFound();
            if (c.Status != "awaiting_confirm" || !c.Alive) return Results.Problem("Команда уже не ожидает подтверждения (истёк срок или её подтвердили)", statusCode: 409);
            if (string.Equals(c.By, http.User.Login(), StringComparison.OrdinalIgnoreCase) || c.Session == http.User.SessionId())
                return Results.Problem("Подтверждать должен другой пользователь со своего рабочего места", statusCode: 403);
            var runtime = await access.RuntimeAsync(ct);
            if (await http.DenyUnlessAsync(Perm.CmdCritical, runtime.TagArea(c.Tag), $"command:{id}") is { } denied) return denied;
            if (!await credentials.VerifyAsync(http.User.Login(), req.Password, ct))
                return Results.Problem("Неверный пароль", statusCode: 403);

            if (!req.Approve)
            {
                var n = await ds.ExecAsync(
                    "UPDATE commands SET status='rejected', result=$2, finished_at=now() WHERE id=$1 AND status='awaiting_confirm'", ct,
                    id, $"отклонена вторым пользователем {http.User.Login()}" + (string.IsNullOrWhiteSpace(req.Comment) ? "" : $": {req.Comment.Trim()}"));
                await audit.WriteAsync(http, "command_confirm", $"command:{id}", new { approve = false, author = c.By, req.Comment });
                return n == 0 ? Results.Conflict() : Results.NoContent();
            }

            // подтверждение и запуск — одной транзакцией; триггер БД ещё раз проверяет «другой пользователь, другой сеанс»
            await using var conn = await ds.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            await using (var ins = new NpgsqlCommand(
                "INSERT INTO command_confirmations (command_id, user_login, session_id, client_ip) VALUES ($1,$2,$3,$4)", conn, tx))
            {
                ins.Parameters.Add(Db.P(id)); ins.Parameters.Add(Db.P(http.User.Login()));
                ins.Parameters.Add(Db.P(http.User.SessionId())); ins.Parameters.Add(Db.P(http.Ip()));
                await ins.ExecuteNonQueryAsync(ct);
            }
            // срок жизни команды отсчитывается от второго подтверждения
            await using (var upd = new NpgsqlCommand(
                "UPDATE commands SET status='pending', confirmed_by=$2, expires_at = now() + make_interval(secs => $3) " +
                "WHERE id=$1 AND status='awaiting_confirm' AND expires_at > now()", conn, tx))
            {
                upd.Parameters.Add(Db.P(id)); upd.Parameters.Add(Db.P(http.User.Login())); upd.Parameters.Add(Db.P((double)opt.Value.TtlSeconds));
                if (await upd.ExecuteNonQueryAsync(ct) == 0) return Results.Problem("Срок подтверждения истёк", statusCode: 409);
            }
            await tx.CommitAsync(ct);
            await audit.WriteAsync(http, "command_confirm", $"command:{id}", new { approve = true, author = c.By, tag = c.Tag, value = c.Num, valueStr = c.Str });
            return Results.NoContent();
        }).RequireAuthorization(Policies.Of(Perm.CmdCritical));

        api.MapGet("/commands", async (int? limit, HttpContext http, NpgsqlDataSource ds, CancellationToken ct) =>
            await ds.QueryAsync(
                """
                SELECT c.id, c.tag_id, t.name, c.value_num, c.value_str, c.requested_by, c.requested_at, c.expires_at,
                       c.status, c.result, c.readback_num, c.finished_at, c.command_class, c.comment, c.confirmed_by
                FROM commands c JOIN tags t ON t.id = c.tag_id JOIN devices d ON d.id = t.device_id
                WHERE ($2::int[] IS NULL OR coalesce(t.area_id, d.area_id) = ANY($2))
                ORDER BY c.id DESC LIMIT $1
                """,
                r => new CommandDto(r.GetInt64(0), r.GetInt32(1), r.GetString(2), r.GetNullableDouble(3),
                    r.GetNullableString(4), r.GetString(5), r.GetUtc(6), r.GetUtc(7), r.GetString(8),
                    r.GetNullableString(9), r.GetNullableDouble(10), r.GetNullableDateTime(11), r.GetString(12),
                    r.GetNullableString(13), r.GetNullableString(14)), ct,
                Math.Clamp(limit ?? 100, 1, 1000), (await http.PermsAsync()).AreasOrAll(Perm.View)))
            .RequireAuthorization(View);
    }

    private static readonly string View = Policies.Of(Perm.View);

    private static async Task<(string Status, string? Result)> WaitAsync(NpgsqlDataSource ds, CommandWaiter waiter, long id,
        CommandOptions o, CancellationToken ct)
    {
        var wait = waiter.Register(id);
        // уведомление могло прийти раньше регистрации — проверяем таблицу
        var (status, result) = await CommandStatusAsync(ds, id, ct);
        if (status is "pending" or "executing")
        {
            var done = await Task.WhenAny(wait, Task.Delay(TimeSpan.FromSeconds(o.WaitSeconds), ct));
            (status, result) = done == wait ? wait.Result : await CommandStatusAsync(ds, id, ct);
        }
        waiter.Forget(id);
        return (status, result);
    }

    private static async Task<IResult> SendAlarmAction(AlarmActionRequest req, string action, string perm, HttpContext http,
        NpgsqlDataSource ds, RealtimeService rt, CommandOptions o, Audit audit, CancellationToken ct)
    {
        if (req.Ids is not { Length: > 0 }) return Results.Problem("Не выбраны тревоги", statusCode: 400);
        // квитирование и снятие с контроля — по области тревоги
        var access = http.RequestServices.GetRequiredService<AccessService>();
        var runtime = await access.RuntimeAsync(ct);
        var perms = await http.PermsAsync();
        var foreign = req.Ids.Where(id => !perms.Has(perm, runtime.AlarmArea(id))).ToArray();
        if (foreign.Length > 0)
        {
            await audit.WriteAsync(http, "access_denied", "alarms:" + string.Join(',', foreign.Take(50)), new { perm, action });
            return Results.Problem($"Нет права «{Perm.Names[perm]}» на часть выбранных тревог ({foreign.Length})", statusCode: 403);
        }
        if (!AcquisitionAlive(rt, o)) return Results.Problem("Служба сбора недоступна", statusCode: 503);
        var payload = JsonSerializer.Serialize(
            new AlarmAckRequest(req.Ids.Take(500).ToArray(), http.User.Login(), req.Comment, action, req.Minutes),
            JsonOpts.Web);
        await PgNotify.SendAsync(ds, PgChannels.AlarmAck, payload, ct);
        await audit.WriteAsync(http, "alarm_" + action, "alarms:" + string.Join(',', req.Ids.Take(50)),
            new { req.Comment, req.Minutes });
        return Results.Accepted();
    }

    private static bool AcquisitionAlive(RealtimeService rt, CommandOptions o) =>
        rt.LastHeartbeatAt is { } t && (DateTime.UtcNow - t).TotalSeconds < o.AcquisitionStaleSeconds;

    private static async Task<(string Status, string? Result)> CommandStatusAsync(NpgsqlDataSource ds, long id,
        CancellationToken ct) =>
        await ds.QuerySingleAsync("SELECT status, result FROM commands WHERE id = $1",
            r => (r.GetString(0), r.GetNullableString(1)), ct, id);

    public static int[] ParseIds(string? s) =>
        (s ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => int.TryParse(x, NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : -1)
            .Where(v => v > 0).Distinct().ToArray();

    private static long Ms(DateTime utc) => new DateTimeOffset(utc, TimeSpan.Zero).ToUnixTimeMilliseconds();
}
