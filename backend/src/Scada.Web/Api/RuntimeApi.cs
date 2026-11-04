using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Npgsql;
using Scada.Core.Data;
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
}

public sealed record CommandRequest(int TagId, double? Value, string? ValueStr, bool Confirmed);
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
    DateTime ExpiresAt, string Status, string? Result, double? ReadbackNum, DateTime? FinishedAt);

public static class RuntimeApi
{
    public static void Map(RouteGroupBuilder api)
    {
        // ------------------------------------------------------------ текущие значения
        api.MapGet("/values", (string? ids, RealtimeService rt) =>
        {
            var list = ParseIds(ids);
            return Results.Ok(rt.Snapshot(list));
        }).RequireAuthorization(Roles.CanView);

        // ------------------------------------------------------------ архив / тренды
        api.MapGet("/history", async (string tagIds, DateTimeOffset from, DateTimeOffset to, int? points,
            NpgsqlDataSource ds, CancellationToken ct) =>
        {
            var ids = ParseIds(tagIds);
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
        }).RequireAuthorization(Roles.CanView);

        api.MapGet("/history/export", async (int tagId, DateTimeOffset from, DateTimeOffset to, NpgsqlDataSource ds,
            HttpContext http, Audit audit, CancellationToken ct) =>
        {
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
        }).RequireAuthorization(Roles.CanView);

        // ------------------------------------------------------------ тревоги
        api.MapGet("/alarms/active", async (NpgsqlDataSource ds, CancellationToken ct) =>
            await ds.QueryAsync(
                """
                SELECT a.alarm_def_id, d.tag_id, t.name, t.description, t.unit, d.kind, d.priority, d.message,
                       a.active, a.acked, a.value, a.raised_at, a.cleared_at, a.acked_at, a.acked_by, a.shelved_until, a.comment
                FROM alarm_active a
                JOIN alarm_defs d ON d.id = a.alarm_def_id
                JOIN tags t ON t.id = d.tag_id
                WHERE a.active OR NOT a.acked OR a.shelved_until > now()
                ORDER BY (a.active AND NOT a.acked) DESC, d.priority, a.raised_at DESC
                """,
                r => new ActiveAlarmDto(r.GetInt32(0), r.GetInt32(1), r.GetString(2), r.GetString(3), r.GetString(4),
                    r.GetString(5), r.GetInt16(6), r.GetString(7), r.GetBoolean(8), r.GetBoolean(9),
                    r.GetNullableDouble(10), r.GetNullableDateTime(11), r.GetNullableDateTime(12),
                    r.GetNullableDateTime(13), r.GetNullableString(14), r.GetNullableDateTime(15),
                    r.GetNullableString(16)), ct))
            .RequireAuthorization(Roles.CanView);

        api.MapGet("/alarms/events", async (DateTimeOffset? from, DateTimeOffset? to, int? limit,
            NpgsqlDataSource ds, CancellationToken ct) =>
            await ds.QueryAsync(
                """
                SELECT e.id, e.ts, e.alarm_def_id, e.event, e.value, e.user_login, e.comment,
                       coalesce(t.name, '?'), coalesce(d.message, ''), coalesce(d.priority, 4)
                FROM alarm_events e
                LEFT JOIN alarm_defs d ON d.id = e.alarm_def_id
                LEFT JOIN tags t ON t.id = d.tag_id
                WHERE e.ts >= $1 AND e.ts <= $2
                ORDER BY e.ts DESC LIMIT $3
                """,
                r => new AlarmEventDto(r.GetInt64(0), r.GetUtc(1), r.GetInt32(2), r.GetString(3), r.GetNullableDouble(4),
                    r.GetNullableString(5), r.GetNullableString(6), r.GetString(7), r.GetString(8), r.GetInt16(9)), ct,
                (from ?? DateTimeOffset.UtcNow.AddDays(-1)).UtcDateTime, (to ?? DateTimeOffset.UtcNow).UtcDateTime,
                Math.Clamp(limit ?? 500, 1, 5000)))
            .RequireAuthorization(Roles.CanView);

        api.MapPost("/alarms/ack", (AlarmActionRequest req, HttpContext http, NpgsqlDataSource ds, RealtimeService rt,
            IOptions<CommandOptions> opt, Audit audit, CancellationToken ct) =>
            SendAlarmAction(req, "ack", http, ds, rt, opt.Value, audit, ct))
            .RequireAuthorization(Roles.CanOperate);

        api.MapPost("/alarms/shelve", (AlarmActionRequest req, HttpContext http, NpgsqlDataSource ds, RealtimeService rt,
            IOptions<CommandOptions> opt, Audit audit, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Comment))
                return Task.FromResult(Results.Problem("Для снятия с контроля обязателен комментарий", statusCode: 400));
            return SendAlarmAction(req, "shelve", http, ds, rt, opt.Value, audit, ct);
        }).RequireAuthorization(Roles.CanOperate);

        api.MapPost("/alarms/unshelve", (AlarmActionRequest req, HttpContext http, NpgsqlDataSource ds, RealtimeService rt,
            IOptions<CommandOptions> opt, Audit audit, CancellationToken ct) =>
            SendAlarmAction(req, "unshelve", http, ds, rt, opt.Value, audit, ct))
            .RequireAuthorization(Roles.CanOperate);

        // ------------------------------------------------------------ команды
        api.MapPost("/commands", async (CommandRequest req, HttpContext http, NpgsqlDataSource ds, RealtimeService rt,
            CommandWaiter waiter, IOptions<CommandOptions> opt, Audit audit, CancellationToken ct) =>
        {
            var o = opt.Value;
            var tag = await ds.QuerySingleAsync(
                "SELECT name, data_type, writable, write_min, write_max, write_confirm, enabled FROM tags WHERE id = $1",
                r => (Name: r.GetString(0), Type: r.GetString(1), Writable: r.GetBoolean(2), Min: r.GetNullableDouble(3),
                      Max: r.GetNullableDouble(4), Confirm: r.GetBoolean(5), Enabled: r.GetBoolean(6)), ct, req.TagId);

            // предварительные проверки (окончательные — в службе сбора)
            if (tag.Name is null) return Results.NotFound();
            if (!tag.Enabled || !tag.Writable) return Results.Problem("Тег недоступен для записи", statusCode: 403);
            if (tag.Confirm && !req.Confirmed) return Results.Problem("Требуется подтверждение оператора", statusCode: 428);
            if (tag.Type == "string" ? req.ValueStr is null : req.Value is null)
                return Results.Problem("Не задано значение", statusCode: 400);
            if (req.Value is { } v && (v < tag.Min || v > tag.Max))
                return Results.Problem($"Значение вне диапазона [{tag.Min}; {tag.Max}]", statusCode: 400);
            if (!AcquisitionAlive(rt, o))
                return Results.Problem("Служба сбора недоступна — команда не отправлена", statusCode: 503);

            var id = Convert.ToInt64(await ds.ScalarAsync(
                "INSERT INTO commands (tag_id, value_num, value_str, requested_by, expires_at, client_ip) " +
                "VALUES ($1, $2, $3, $4, now() + make_interval(secs => $5), $6) RETURNING id", ct,
                req.TagId, tag.Type == "string" ? null : req.Value, tag.Type == "string" ? req.ValueStr : null,
                http.User.Login(), (double)o.TtlSeconds, http.Ip()));

            var wait = waiter.Register(id);
            // уведомление могло прийти раньше регистрации — проверяем таблицу
            var (status, result) = await CommandStatusAsync(ds, id, ct);
            if (status is "pending" or "executing")
            {
                var done = await Task.WhenAny(wait, Task.Delay(TimeSpan.FromSeconds(o.WaitSeconds), ct));
                (status, result) = done == wait ? wait.Result : await CommandStatusAsync(ds, id, ct);
            }
            waiter.Forget(id);

            await audit.WriteAsync(http, "command", $"tag:{req.TagId}", new
            {
                command = id, tag = tag.Name, value = req.Value, valueStr = req.ValueStr, status, result,
            });
            return Results.Ok(new { id, status, result });
        }).RequireAuthorization(Roles.CanOperate);

        api.MapGet("/commands", async (int? limit, NpgsqlDataSource ds, CancellationToken ct) =>
            await ds.QueryAsync(
                """
                SELECT c.id, c.tag_id, t.name, c.value_num, c.value_str, c.requested_by, c.requested_at, c.expires_at,
                       c.status, c.result, c.readback_num, c.finished_at
                FROM commands c JOIN tags t ON t.id = c.tag_id ORDER BY c.id DESC LIMIT $1
                """,
                r => new CommandDto(r.GetInt64(0), r.GetInt32(1), r.GetString(2), r.GetNullableDouble(3),
                    r.GetNullableString(4), r.GetString(5), r.GetUtc(6), r.GetUtc(7), r.GetString(8),
                    r.GetNullableString(9), r.GetNullableDouble(10), r.GetNullableDateTime(11)), ct,
                Math.Clamp(limit ?? 100, 1, 1000)))
            .RequireAuthorization(Roles.CanView);
    }

    private static async Task<IResult> SendAlarmAction(AlarmActionRequest req, string action, HttpContext http,
        NpgsqlDataSource ds, RealtimeService rt, CommandOptions o, Audit audit, CancellationToken ct)
    {
        if (req.Ids is not { Length: > 0 }) return Results.Problem("Не выбраны тревоги", statusCode: 400);
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
