using System.Globalization;
using System.Text;
using System.Text.Json;
using Npgsql;
using Scada.Core.Config;
using Scada.Core.Data;
using Scada.Web.Infrastructure;

namespace Scada.Web.Api;

public sealed record DeviceDto(
    int Id, string Name, string Protocol, string Endpoint, JsonElement Settings, bool Enabled,
    int PollMs, int TimeoutMs, string Description,
    string? State = null, DateTime? LastOkAt = null, string? LastError = null, long? ErrorCount = null,
    double? AvgLatencyMs = null, int RowVersion = 0);

public sealed record TagDto(
    int Id, string Name, string Description, int DeviceId, string Address, string DataType, string Unit,
    double? RawLow, double? RawHigh, double? EngLow, double? EngHigh, int? PollMs, int? StaleAfterMs,
    double DeadbandUi, double DeadbandArchive, string ArchiveMode, int ArchiveIntervalS,
    bool Writable, double? WriteMin, double? WriteMax, bool WriteConfirm, bool Enabled, int RowVersion = 0);

public sealed record AlarmDefDto(
    int Id, int TagId, string Kind, double? LimitValue, double Hysteresis, int DelayMs, short Priority,
    string Message, bool Enabled, int RowVersion = 0);

/// <summary>
/// Конфигурация: устройства, теги, тревоги, импорт/экспорт CSV.
/// Все изменения идут в ЧЕРНОВИК; в рантайм они попадают только через публикацию (PublishApi).
/// Удаление мягкое, одновременная правка одного объекта ловится по row_version.
/// </summary>
public static class ConfigApi
{
    private const string TagSelect =
        "SELECT id, name, description, device_id, address, data_type, unit, raw_low, raw_high, eng_low, eng_high, " +
        "poll_ms, stale_after_ms, deadband_ui, deadband_archive, archive_mode, archive_interval_s, " +
        "writable, write_min, write_max, write_confirm, enabled, row_version FROM tags";

    private static TagDto ReadTag(System.Data.Common.DbDataReader r) => new(
        r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetInt32(3), r.GetString(4), r.GetString(5), r.GetString(6),
        r.GetNullableDouble(7), r.GetNullableDouble(8), r.GetNullableDouble(9), r.GetNullableDouble(10),
        r.GetNullableInt(11), r.GetNullableInt(12), r.GetDouble(13), r.GetDouble(14), r.GetString(15), r.GetInt32(16),
        r.GetBoolean(17), r.GetNullableDouble(18), r.GetNullableDouble(19), r.GetBoolean(20), r.GetBoolean(21), r.GetInt32(22));

    public static void Map(RouteGroupBuilder api)
    {
        // ------------------------------------------------------------ устройства
        api.MapGet("/devices", async (NpgsqlDataSource ds, CancellationToken ct) =>
            await ds.QueryAsync(
                """
                SELECT d.id, d.name, d.protocol, d.endpoint, d.settings::text, d.enabled, d.poll_ms, d.timeout_ms, d.description,
                       s.state, s.last_ok_at, s.last_error, s.error_count, s.avg_latency_ms, d.row_version
                FROM devices d LEFT JOIN device_status s ON s.device_id = d.id WHERE NOT d.deleted ORDER BY d.name
                """,
                r => new DeviceDto(r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetString(3),
                    JsonSerializer.Deserialize<JsonElement>(r.GetString(4)), r.GetBoolean(5), r.GetInt32(6), r.GetInt32(7),
                    r.GetString(8), r.GetNullableString(9), r.GetNullableDateTime(10), r.GetNullableString(11),
                    r.IsDBNull(12) ? null : r.GetInt64(12), r.GetNullableDouble(13), r.GetInt32(14)), ct))
            .RequireAuthorization(Roles.CanView);

        api.MapPost("/devices", (DeviceDto d, HttpContext http, NpgsqlDataSource ds, Audit audit, CancellationToken ct) =>
            Guard(async () =>
            {
                var id = Convert.ToInt32(await ds.ScalarAsync(
                    "INSERT INTO devices (name, protocol, endpoint, settings, enabled, poll_ms, timeout_ms, description) " +
                    "VALUES ($1,$2,$3,$4::jsonb,$5,$6,$7,$8) RETURNING id", ct,
                    d.Name.Trim(), d.Protocol, d.Endpoint.Trim(), SettingsText(d.Settings), d.Enabled, d.PollMs, d.TimeoutMs,
                    d.Description));
                await DraftLog.WriteAsync(ds, http, "device", id, d.Name.Trim(), "create", ct);
                await audit.WriteAsync(http, "config_change", $"device:{id}", new { op = "create", draft = true, device = d });
                return Results.Ok(new { id });
            })).RequireAuthorization(Roles.CanEngineer);

        api.MapPut("/devices/{id:int}", (int id, DeviceDto d, HttpContext http, NpgsqlDataSource ds, Audit audit,
            CancellationToken ct) => Guard(async () =>
            {
                var n = await ds.ExecAsync(
                    "UPDATE devices SET name=$2, protocol=$3, endpoint=$4, settings=$5::jsonb, enabled=$6, poll_ms=$7, " +
                    "timeout_ms=$8, description=$9, row_version=row_version+1, updated_at=now() " +
                    "WHERE id=$1 AND NOT deleted AND ($10 = 0 OR row_version = $10)", ct,
                    id, d.Name.Trim(), d.Protocol, d.Endpoint.Trim(), SettingsText(d.Settings), d.Enabled, d.PollMs,
                    d.TimeoutMs, d.Description, d.RowVersion);
                if (n == 0) return await NotFoundOrConflict(ds, "devices", id, ct);
                await DraftLog.WriteAsync(ds, http, "device", id, d.Name.Trim(), "update", ct);
                await audit.WriteAsync(http, "config_change", $"device:{id}", new { op = "update", draft = true, device = d });
                return Results.NoContent();
            })).RequireAuthorization(Roles.CanEngineer);

        api.MapDelete("/devices/{id:int}", (int id, HttpContext http, NpgsqlDataSource ds, Audit audit, CancellationToken ct) =>
            Guard(async () =>
            {
                var name = (string?)await ds.ScalarAsync("SELECT name FROM devices WHERE id=$1 AND NOT deleted", ct, id);
                if (name is null) return Results.NotFound();
                var tags = Convert.ToInt64(await ds.ScalarAsync("SELECT count(*) FROM tags WHERE device_id=$1 AND NOT deleted", ct, id));
                if (tags > 0) return Results.Problem($"У устройства {tags} тегов. Сначала удалите или перенесите их.", statusCode: 409);
                await SoftDelete(ds, "devices", id, ct);
                await DraftLog.WriteAsync(ds, http, "device", id, name, "delete", ct);
                await audit.WriteAsync(http, "config_change", $"device:{id}", new { op = "delete", draft = true });
                return Results.NoContent();
            })).RequireAuthorization(Roles.CanEngineer);

        // ------------------------------------------------------------ теги
        api.MapGet("/tags", async (int? deviceId, NpgsqlDataSource ds, CancellationToken ct) =>
            await ds.QueryAsync(TagSelect + " WHERE NOT deleted AND ($1::int IS NULL OR device_id = $1) ORDER BY name", ReadTag, ct, deviceId))
            .RequireAuthorization(Roles.CanView);

        api.MapGet("/tags/{id:int}", async (int id, NpgsqlDataSource ds, CancellationToken ct) =>
            await ds.QuerySingleAsync(TagSelect + " WHERE id = $1 AND NOT deleted", ReadTag, ct, id) is { } t ? Results.Ok(t) : Results.NotFound())
            .RequireAuthorization(Roles.CanView);

        api.MapPost("/tags", (TagDto t, HttpContext http, NpgsqlDataSource ds, Audit audit, CancellationToken ct) =>
            Guard(async () =>
            {
                var id = Convert.ToInt32(await ds.ScalarAsync(
                    "INSERT INTO tags (name, description, device_id, address, data_type, unit, raw_low, raw_high, eng_low, " +
                    "eng_high, poll_ms, stale_after_ms, deadband_ui, deadband_archive, archive_mode, archive_interval_s, " +
                    "writable, write_min, write_max, write_confirm, enabled) VALUES " +
                    "($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15,$16,$17,$18,$19,$20,$21) RETURNING id",
                    ct, TagArgs(t)));
                await DraftLog.WriteAsync(ds, http, "tag", id, t.Name.Trim(), "create", ct);
                await audit.WriteAsync(http, "config_change", $"tag:{id}", new { op = "create", draft = true, tag = t });
                return Results.Ok(new { id });
            })).RequireAuthorization(Roles.CanEngineer);

        api.MapPut("/tags/{id:int}", (int id, TagDto t, HttpContext http, NpgsqlDataSource ds, Audit audit,
            CancellationToken ct) => Guard(async () =>
            {
                var args = new List<object?>(TagArgs(t)) { id, t.RowVersion };
                var n = await ds.ExecAsync(
                    "UPDATE tags SET name=$1, description=$2, device_id=$3, address=$4, data_type=$5, unit=$6, raw_low=$7, " +
                    "raw_high=$8, eng_low=$9, eng_high=$10, poll_ms=$11, stale_after_ms=$12, deadband_ui=$13, " +
                    "deadband_archive=$14, archive_mode=$15, archive_interval_s=$16, writable=$17, write_min=$18, " +
                    "write_max=$19, write_confirm=$20, enabled=$21, row_version=row_version+1, updated_at=now() " +
                    "WHERE id=$22 AND NOT deleted AND ($23 = 0 OR row_version = $23)",
                    ct, args.ToArray());
                if (n == 0) return await NotFoundOrConflict(ds, "tags", id, ct);
                await DraftLog.WriteAsync(ds, http, "tag", id, t.Name.Trim(), "update", ct);
                await audit.WriteAsync(http, "config_change", $"tag:{id}", new { op = "update", draft = true, tag = t });
                return Results.NoContent();
            })).RequireAuthorization(Roles.CanEngineer);

        api.MapDelete("/tags/{id:int}", (int id, HttpContext http, NpgsqlDataSource ds, Audit audit, CancellationToken ct) =>
            Guard(async () =>
            {
                var name = (string?)await ds.ScalarAsync("SELECT name FROM tags WHERE id=$1 AND NOT deleted", ct, id);
                if (name is null) return Results.NotFound();
                var alarms = Convert.ToInt64(await ds.ScalarAsync("SELECT count(*) FROM alarm_defs WHERE tag_id=$1 AND NOT deleted", ct, id));
                if (alarms > 0) return Results.Problem($"На теге настроено тревог: {alarms}. Сначала удалите их.", statusCode: 409);
                await SoftDelete(ds, "tags", id, ct);
                await DraftLog.WriteAsync(ds, http, "tag", id, name, "delete", ct);
                await audit.WriteAsync(http, "config_change", $"tag:{id}", new { op = "delete", draft = true });
                return Results.NoContent();
            })).RequireAuthorization(Roles.CanEngineer);

        api.MapGet("/tags/export", async (NpgsqlDataSource ds, HttpContext http, Audit audit, CancellationToken ct) =>
        {
            var csv = await TagCsv.ExportAsync(ds, ct);
            await audit.WriteAsync(http, "export", "tags", null);
            // BOM — чтобы Excel корректно открыл кириллицу
            return Results.File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv)).ToArray(),
                "text/csv; charset=utf-8", "tags.csv");
        }).RequireAuthorization(Roles.CanEngineer);

        api.MapPost("/tags/import", async (HttpRequest req, HttpContext http, NpgsqlDataSource ds, Audit audit,
            CancellationToken ct) =>
        {
            using var reader = new StreamReader(req.Body, Encoding.UTF8);
            var text = await reader.ReadToEndAsync(ct);
            var report = await TagCsv.ImportAsync(ds, text, http.User.Login(), ct);
            await audit.WriteAsync(http, "import", "tags", new { report.Inserted, report.Updated, errors = report.Errors.Count, draft = true });
            return report.Errors.Count > 0 ? Results.BadRequest(report) : Results.Ok(report);
        }).RequireAuthorization(Roles.CanEngineer);

        // ------------------------------------------------------------ определения тревог
        api.MapGet("/alarm-defs", async (int? tagId, NpgsqlDataSource ds, CancellationToken ct) =>
            await ds.QueryAsync(
                "SELECT id, tag_id, kind, limit_value, hysteresis, delay_ms, priority, message, enabled, row_version FROM alarm_defs " +
                "WHERE NOT deleted AND ($1::int IS NULL OR tag_id = $1) ORDER BY tag_id, priority",
                r => new AlarmDefDto(r.GetInt32(0), r.GetInt32(1), r.GetString(2), r.GetNullableDouble(3), r.GetDouble(4),
                    r.GetInt32(5), r.GetInt16(6), r.GetString(7), r.GetBoolean(8), r.GetInt32(9)), ct, tagId))
            .RequireAuthorization(Roles.CanView);

        api.MapPost("/alarm-defs", (AlarmDefDto a, HttpContext http, NpgsqlDataSource ds, Audit audit, CancellationToken ct) =>
            Guard(async () =>
            {
                var id = Convert.ToInt32(await ds.ScalarAsync(
                    "INSERT INTO alarm_defs (tag_id, kind, limit_value, hysteresis, delay_ms, priority, message, enabled) " +
                    "VALUES ($1,$2,$3,$4,$5,$6,$7,$8) RETURNING id", ct,
                    a.TagId, a.Kind, a.LimitValue, a.Hysteresis, a.DelayMs, a.Priority, a.Message, a.Enabled));
                await DraftLog.WriteAsync(ds, http, "alarm", id, a.Message, "create", ct);
                await audit.WriteAsync(http, "config_change", $"alarm_def:{id}", new { op = "create", draft = true, alarm = a });
                return Results.Ok(new { id });
            })).RequireAuthorization(Roles.CanEngineer);

        api.MapPut("/alarm-defs/{id:int}", (int id, AlarmDefDto a, HttpContext http, NpgsqlDataSource ds, Audit audit,
            CancellationToken ct) => Guard(async () =>
            {
                var n = await ds.ExecAsync(
                    "UPDATE alarm_defs SET tag_id=$2, kind=$3, limit_value=$4, hysteresis=$5, delay_ms=$6, priority=$7, " +
                    "message=$8, enabled=$9, row_version=row_version+1, updated_at=now() " +
                    "WHERE id=$1 AND NOT deleted AND ($10 = 0 OR row_version = $10)", ct,
                    id, a.TagId, a.Kind, a.LimitValue, a.Hysteresis, a.DelayMs, a.Priority, a.Message, a.Enabled, a.RowVersion);
                if (n == 0) return await NotFoundOrConflict(ds, "alarm_defs", id, ct);
                await DraftLog.WriteAsync(ds, http, "alarm", id, a.Message, "update", ct);
                await audit.WriteAsync(http, "config_change", $"alarm_def:{id}", new { op = "update", draft = true, alarm = a });
                return Results.NoContent();
            })).RequireAuthorization(Roles.CanEngineer);

        api.MapDelete("/alarm-defs/{id:int}", (int id, HttpContext http, NpgsqlDataSource ds, Audit audit,
            CancellationToken ct) => Guard(async () =>
            {
                var msg = (string?)await ds.ScalarAsync("SELECT message FROM alarm_defs WHERE id=$1 AND NOT deleted", ct, id);
                if (msg is null) return Results.NotFound();
                await SoftDelete(ds, "alarm_defs", id, ct);
                await DraftLog.WriteAsync(ds, http, "alarm", id, msg, "delete", ct);
                await audit.WriteAsync(http, "config_change", $"alarm_def:{id}", new { op = "delete", draft = true });
                return Results.NoContent();
            })).RequireAuthorization(Roles.CanEngineer);
    }

    public static async Task SoftDelete(NpgsqlDataSource ds, string table, int id, CancellationToken ct)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        await DraftStore.SoftDeleteAsync(conn, null, table, id, ct);
    }

    /// <summary>0 обновлённых строк: объекта нет — 404, его успели изменить — 409.</summary>
    public static async Task<IResult> NotFoundOrConflict(NpgsqlDataSource ds, string table, int id, CancellationToken ct)
    {
        var exists = await ds.ScalarAsync($"SELECT 1 FROM {table} WHERE id = $1 AND NOT deleted", ct, id);
        return exists is null
            ? Results.NotFound()
            : Results.Problem("Объект уже изменил другой пользователь. Закройте форму, обновите список и повторите правку.", statusCode: 409);
    }

    private static string SettingsText(JsonElement e) =>
        e.ValueKind == JsonValueKind.Object ? e.GetRawText() : "{}";

    private static object?[] TagArgs(TagDto t) =>
    [
        t.Name.Trim(), t.Description, t.DeviceId, t.Address.Trim(), t.DataType, t.Unit, t.RawLow, t.RawHigh,
        t.EngLow, t.EngHigh, t.PollMs, t.StaleAfterMs, t.DeadbandUi, t.DeadbandArchive, t.ArchiveMode,
        t.ArchiveIntervalS, t.Writable, t.WriteMin, t.WriteMax, t.WriteConfirm, t.Enabled,
    ];

    /// <summary>Нарушения ограничений БД → 400/409 с понятным текстом.</summary>
    public static async Task<IResult> Guard(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (Exception ex) when (ex.IsUniqueViolation())
        { return Results.Problem("Объект с таким именем уже существует", statusCode: 409); }
        catch (Exception ex) when (ex.IsForeignKeyViolation())
        { return Results.Problem("Есть связанные объекты (или указана несуществующая ссылка)", statusCode: 409); }
        catch (PostgresException ex) when (ex.IsCheckViolation())
        { return Results.Problem($"Недопустимое значение: {ex.MessageText}", statusCode: 400); }
    }
}

/// <summary>Импорт/экспорт тегов. Разделитель «;», первая строка — заголовок. Импорт — всё или ничего.</summary>
public static class TagCsv
{
    public static readonly string[] Columns =
    [
        "name", "description", "device", "address", "data_type", "unit", "raw_low", "raw_high", "eng_low", "eng_high",
        "poll_ms", "stale_after_ms", "deadband_ui", "deadband_archive", "archive_mode", "archive_interval_s",
        "writable", "write_min", "write_max", "write_confirm", "enabled",
    ];

    public sealed record ImportReport(int Inserted, int Updated, List<string> Errors);

    public static async Task<string> ExportAsync(NpgsqlDataSource ds, CancellationToken ct)
    {
        var rows = await ds.QueryAsync(
            "SELECT t.name, t.description, d.name, t.address, t.data_type, t.unit, t.raw_low, t.raw_high, t.eng_low, " +
            "t.eng_high, t.poll_ms, t.stale_after_ms, t.deadband_ui, t.deadband_archive, t.archive_mode, " +
            "t.archive_interval_s, t.writable, t.write_min, t.write_max, t.write_confirm, t.enabled " +
            "FROM tags t JOIN devices d ON d.id = t.device_id WHERE NOT t.deleted ORDER BY t.name",
            r =>
            {
                var cells = new string[r.FieldCount];
                for (var i = 0; i < r.FieldCount; i++)
                    cells[i] = r.IsDBNull(i) ? "" : Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture)!.ToLowerInvariantIfBool();
                return string.Join(';', cells.Select(Escape));
            }, ct);
        return string.Join(';', Columns) + "\n" + string.Join("\n", rows) + "\n";
    }

    private static string ToLowerInvariantIfBool(this string s) => s is "True" or "False" ? s.ToLowerInvariant() : s;

    private static string Escape(string s) =>
        s.IndexOfAny([';', '"', '\n', '\r']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    public static List<string[]> Parse(string text)
    {
        // разделитель определяется по заголовку: «;» (Excel RU) или «,»
        var nl = text.IndexOf('\n');
        var delim = (nl < 0 ? text : text[..nl]).Contains(';') ? ';' : ',';

        var rows = new List<string[]>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else cell.Append(c);
                continue;
            }
            if (c == '"') quoted = true;
            else if (c == delim) { row.Add(cell.ToString()); cell.Clear(); }
            else if (c == '\n')
            {
                row.Add(cell.ToString()); cell.Clear();
                if (row.Any(x => x.Length > 0)) rows.Add(row.ToArray());
                row.Clear();
            }
            else if (c != '\r') cell.Append(c);
        }
        row.Add(cell.ToString());
        if (row.Any(x => x.Length > 0)) rows.Add(row.ToArray());
        return rows;
    }

    public static async Task<ImportReport> ImportAsync(NpgsqlDataSource ds, string text, string user, CancellationToken ct)
    {
        var errors = new List<string>();
        var rows = Parse(text.TrimStart('﻿'));
        if (rows.Count == 0) return new ImportReport(0, 0, ["Файл пуст"]);

        var header = rows[0].Select(h => h.Trim().ToLowerInvariant()).ToArray();
        var idx = Columns.ToDictionary(c => c, c => Array.IndexOf(header, c));
        foreach (var req in new[] { "name", "device", "address", "data_type" })
            if (idx[req] < 0) errors.Add($"Нет обязательной колонки «{req}»");
        if (errors.Count > 0) return new ImportReport(0, 0, errors);

        var devices = await ds.QueryAsync("SELECT name, id FROM devices WHERE NOT deleted", r => (r.GetString(0), r.GetInt32(1)), ct);
        var devByName = devices.ToDictionary(d => d.Item1, d => d.Item2, StringComparer.OrdinalIgnoreCase);

        await using var conn = await ds.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        int inserted = 0, updated = 0;

        for (var line = 1; line < rows.Count; line++)
        {
            var row = rows[line];
            string Get(string col) => idx[col] >= 0 && idx[col] < row.Length ? row[idx[col]].Trim() : "";
            double? D(string col)
            {
                var s = Get(col).Replace(',', '.');
                return s.Length == 0 ? null : double.Parse(s, CultureInfo.InvariantCulture);
            }
            int? I(string col) => Get(col) is { Length: > 0 } s ? int.Parse(s, CultureInfo.InvariantCulture) : null;
            bool B(string col, bool def) => Get(col).ToLowerInvariant() switch
            {
                "" => def, "1" or "true" or "да" or "yes" => true, "0" or "false" or "нет" or "no" => false,
                var x => throw new FormatException($"«{x}» не логическое значение"),
            };

            try
            {
                if (!devByName.TryGetValue(Get("device"), out var devId))
                    throw new FormatException($"устройство «{Get("device")}» не найдено");

                await using var cmd = new NpgsqlCommand(
                    """
                    INSERT INTO tags (name, description, device_id, address, data_type, unit, raw_low, raw_high, eng_low,
                        eng_high, poll_ms, stale_after_ms, deadband_ui, deadband_archive, archive_mode, archive_interval_s,
                        writable, write_min, write_max, write_confirm, enabled)
                    VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15,$16,$17,$18,$19,$20,$21)
                    ON CONFLICT (name) DO UPDATE SET description=EXCLUDED.description, device_id=EXCLUDED.device_id,
                        address=EXCLUDED.address, data_type=EXCLUDED.data_type, unit=EXCLUDED.unit,
                        raw_low=EXCLUDED.raw_low, raw_high=EXCLUDED.raw_high, eng_low=EXCLUDED.eng_low,
                        eng_high=EXCLUDED.eng_high, poll_ms=EXCLUDED.poll_ms, stale_after_ms=EXCLUDED.stale_after_ms,
                        deadband_ui=EXCLUDED.deadband_ui, deadband_archive=EXCLUDED.deadband_archive,
                        archive_mode=EXCLUDED.archive_mode, archive_interval_s=EXCLUDED.archive_interval_s,
                        writable=EXCLUDED.writable, write_min=EXCLUDED.write_min, write_max=EXCLUDED.write_max,
                        write_confirm=EXCLUDED.write_confirm, enabled=EXCLUDED.enabled,
                        row_version=tags.row_version+1, updated_at=now()
                    RETURNING (xmax = 0), id
                    """, conn, tx);
                object?[] args =
                [
                    Get("name"), Get("description"), devId, Get("address"), Get("data_type"), Get("unit"),
                    D("raw_low"), D("raw_high"), D("eng_low"), D("eng_high"), I("poll_ms"), I("stale_after_ms"),
                    D("deadband_ui") ?? 0, D("deadband_archive") ?? 0,
                    Get("archive_mode") is { Length: > 0 } m ? m : "change", I("archive_interval_s") ?? 60,
                    B("writable", false), D("write_min"), D("write_max"), B("write_confirm", true), B("enabled", true),
                ];
                foreach (var a in args) cmd.Parameters.Add(Db.P(a));

                // точка сохранения: ошибка строки не ломает транзакцию, собираем все ошибки сразу
                await tx.SaveAsync("row", ct);
                bool isInsert;
                int tagId;
                await using (var rr = await cmd.ExecuteReaderAsync(ct))
                {
                    await rr.ReadAsync(ct);
                    isInsert = rr.GetBoolean(0);
                    tagId = rr.GetInt32(1);
                }
                await using (var log = new NpgsqlCommand(
                    "INSERT INTO draft_changes (user_login, object_type, object_id, object_name, op) VALUES ($1,'tag',$2,$3,$4)", conn, tx))
                {
                    log.Parameters.Add(Db.P(user));
                    log.Parameters.Add(Db.P(tagId));
                    log.Parameters.Add(Db.P(Get("name")));
                    log.Parameters.Add(Db.P(isInsert ? "create" : "update"));
                    await log.ExecuteNonQueryAsync(ct);
                }
                await tx.ReleaseAsync("row", ct);
                if (isInsert) inserted++; else updated++;
            }
            catch (PostgresException ex)
            {
                await tx.RollbackAsync("row", ct);
                errors.Add($"Строка {line + 1}: {ex.MessageText}");
            }
            catch (Exception ex) when (ex is FormatException or OverflowException)
            {
                errors.Add($"Строка {line + 1}: {ex.Message}");
            }
        }

        if (errors.Count > 0)
        {
            await tx.RollbackAsync(ct);
            return new ImportReport(0, 0, errors);
        }
        await tx.CommitAsync(ct);
        return new ImportReport(inserted, updated, errors);
    }
}
