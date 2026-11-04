using System.Text.Json;
using Npgsql;
using Scada.Core.Data;
using Scada.Core.Model;

namespace Scada.Core.Config;

/// <summary>
/// Черновик (рабочие таблицы) и опубликованные версии (config_versions).
/// Методы с conn/tx работают внутри транзакции вызывающего кода.
/// </summary>
public static class DraftStore
{
    public const string DeletedPrefix = "~";

    private static NpgsqlCommand Cmd(string sql, NpgsqlConnection conn, NpgsqlTransaction? tx, params object?[] args)
    {
        var cmd = new NpgsqlCommand(sql, conn, tx);
        foreach (var a in args) cmd.Parameters.Add(Db.P(a));
        return cmd;
    }

    /// <summary>Черновик: неудалённые строки рабочих таблиц.</summary>
    public static async Task<ConfigSnapshot> LoadDraftAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, CancellationToken ct)
    {
        var devices = new List<DeviceDefinition>();
        await using (var cmd = Cmd($"SELECT {ConfigRepository.DeviceColumns} FROM devices WHERE NOT deleted ORDER BY id", conn, tx))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) devices.Add(ConfigRepository.ReadDevice(r));

        var tags = new List<TagDefinition>();
        await using (var cmd = Cmd($"SELECT {ConfigRepository.TagColumns} FROM tags WHERE NOT deleted ORDER BY id", conn, tx))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) tags.Add(ConfigRepository.ReadTag(r));

        var alarms = new List<AlarmDefinition>();
        await using (var cmd = Cmd($"SELECT {ConfigRepository.AlarmColumns} FROM alarm_defs WHERE NOT deleted ORDER BY id", conn, tx))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) alarms.Add(ConfigRepository.ReadAlarm(r));

        var mnemos = new List<MnemoRef>();
        await using (var cmd = Cmd("SELECT id, name, parent_id, sort_order, current_version FROM mnemos WHERE NOT deleted ORDER BY id", conn, tx))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                mnemos.Add(new MnemoRef(r.GetInt32(0), r.GetString(1), r.GetNullableInt(2), r.GetInt32(3), r.GetInt32(4)));

        return new ConfigSnapshot(devices, tags, alarms, mnemos);
    }

    public static async Task<(int Version, ConfigSnapshot Snapshot)?> LoadPublishedAsync(
        NpgsqlConnection conn, NpgsqlTransaction? tx, CancellationToken ct, int? version = null)
    {
        await using var cmd = Cmd(
            "SELECT version, snapshot::text FROM config_versions WHERE ($1::int IS NULL OR version = $1) ORDER BY version DESC LIMIT 1",
            conn, tx, version);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return (r.GetInt32(0), ConfigSnapshot.Deserialize(r.GetString(1)));
    }

    public static async Task<(int Version, ConfigSnapshot Snapshot)?> LoadPublishedAsync(NpgsqlDataSource ds, CancellationToken ct)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        return await LoadPublishedAsync(conn, null, ct);
    }

    /// <summary>Документы схем указанных версий — для проверок (привязки тегов, переходы).</summary>
    public static async Task<Dictionary<int, JsonElement>> LoadMnemoDocsAsync(
        NpgsqlConnection conn, NpgsqlTransaction? tx, IEnumerable<MnemoRef> refs, CancellationToken ct)
    {
        var result = new Dictionary<int, JsonElement>();
        var list = refs.Where(m => m.Version > 0).ToList();
        if (list.Count == 0) return result;
        await using var cmd = Cmd(
            "SELECT v.mnemo_id, v.doc::text FROM mnemo_versions v JOIN unnest($1::int[], $2::int[]) AS u(id, ver) " +
            "ON v.mnemo_id = u.id AND v.version = u.ver", conn, tx,
            list.Select(m => m.Id).ToArray(), list.Select(m => m.Version).ToArray());
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) result[r.GetInt32(0)] = JsonSerializer.Deserialize<JsonElement>(r.GetString(1));
        return result;
    }

    /// <summary>Мягкое удаление; имя освобождается, чтобы его можно было занять заново.</summary>
    public static async Task SoftDeleteAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string table, int id, CancellationToken ct)
    {
        var rename = table is "devices" or "tags"
            ? $", name = CASE WHEN name LIKE '{DeletedPrefix}%' THEN name ELSE '{DeletedPrefix}' || id || '{DeletedPrefix}' || name END"
            : "";
        await using var cmd = Cmd(
            $"UPDATE {table} SET deleted = true, row_version = row_version + 1{rename} WHERE id = $1 AND NOT deleted", conn, tx, id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Приводит черновик к содержимому снимка (сброс черновика, откат):
    /// лишние объекты мягко удаляются, объекты снимка восстанавливаются с прежними id.
    /// </summary>
    public static async Task RestoreAsync(NpgsqlConnection conn, NpgsqlTransaction tx, ConfigSnapshot s, CancellationToken ct)
    {
        await SoftDeleteMissingAsync(conn, tx, "alarm_defs", s.Alarms.Select(a => a.Id), ct);
        await SoftDeleteMissingAsync(conn, tx, "tags", s.Tags.Select(t => t.Id), ct);
        await SoftDeleteMissingAsync(conn, tx, "devices", s.Devices.Select(d => d.Id), ct);
        await SoftDeleteMissingAsync(conn, tx, "mnemos", s.Mnemos.Select(m => m.Id), ct);

        foreach (var d in s.Devices)
        {
            object?[] args = [d.Id, d.Name, d.Protocol.ToCode(), d.Endpoint, d.SettingsJson, d.Enabled, d.PollMs, d.TimeoutMs, d.Description];
            await UpsertAsync(conn, tx,
                "UPDATE devices SET name=$2, protocol=$3, endpoint=$4, settings=$5::jsonb, enabled=$6, poll_ms=$7, timeout_ms=$8, " +
                "description=$9, deleted=false, row_version=row_version+1, updated_at=now() WHERE id=$1",
                "INSERT INTO devices (id, name, protocol, endpoint, settings, enabled, poll_ms, timeout_ms, description) " +
                "VALUES ($1,$2,$3,$4,$5::jsonb,$6,$7,$8,$9)", args, ct);
        }
        foreach (var t in s.Tags)
        {
            object?[] args =
            [
                t.Id, t.Name, t.Description, t.DeviceId, t.Address, t.DataType.ToCode(), t.Unit, t.RawLow, t.RawHigh, t.EngLow,
                t.EngHigh, t.PollMs, t.StaleAfterMs, t.DeadbandUi, t.DeadbandArchive, ArchiveCode(t.ArchiveMode),
                t.ArchiveIntervalS, t.Writable, t.WriteMin, t.WriteMax, t.WriteConfirm, t.Enabled,
            ];
            await UpsertAsync(conn, tx,
                "UPDATE tags SET name=$2, description=$3, device_id=$4, address=$5, data_type=$6, unit=$7, raw_low=$8, raw_high=$9, " +
                "eng_low=$10, eng_high=$11, poll_ms=$12, stale_after_ms=$13, deadband_ui=$14, deadband_archive=$15, archive_mode=$16, " +
                "archive_interval_s=$17, writable=$18, write_min=$19, write_max=$20, write_confirm=$21, enabled=$22, " +
                "deleted=false, row_version=row_version+1, updated_at=now() WHERE id=$1",
                "INSERT INTO tags (id, name, description, device_id, address, data_type, unit, raw_low, raw_high, eng_low, eng_high, " +
                "poll_ms, stale_after_ms, deadband_ui, deadband_archive, archive_mode, archive_interval_s, writable, write_min, " +
                "write_max, write_confirm, enabled) VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15,$16,$17,$18,$19,$20,$21,$22)",
                args, ct);
        }
        foreach (var a in s.Alarms)
        {
            object?[] args = [a.Id, a.TagId, KindCode(a.Kind), a.Limit, a.Hysteresis, a.DelayMs, a.Priority, a.Message, a.Enabled];
            await UpsertAsync(conn, tx,
                "UPDATE alarm_defs SET tag_id=$2, kind=$3, limit_value=$4, hysteresis=$5, delay_ms=$6, priority=$7, message=$8, " +
                "enabled=$9, deleted=false, row_version=row_version+1, updated_at=now() WHERE id=$1",
                "INSERT INTO alarm_defs (id, tag_id, kind, limit_value, hysteresis, delay_ms, priority, message, enabled) " +
                "VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9)", args, ct);
        }
        foreach (var m in s.Mnemos)
        {
            object?[] args = [m.Id, m.Name, m.ParentId, m.SortOrder, m.Version];
            await UpsertAsync(conn, tx,
                "UPDATE mnemos SET name=$2, parent_id=$3, sort_order=$4, current_version=$5, deleted=false, " +
                "row_version=row_version+1, updated_at=now() WHERE id=$1",
                "INSERT INTO mnemos (id, name, parent_id, sort_order, current_version) VALUES ($1,$2,$3,$4,$5)", args, ct);
        }
        await using (var clear = Cmd("DELETE FROM draft_changes", conn, tx)) await clear.ExecuteNonQueryAsync(ct);
    }

    private static async Task SoftDeleteMissingAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string table,
        IEnumerable<int> keep, CancellationToken ct)
    {
        var ids = new List<int>();
        await using (var cmd = Cmd($"SELECT id FROM {table} WHERE NOT deleted AND NOT (id = ANY($1))", conn, tx, keep.ToArray()))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) ids.Add(r.GetInt32(0));
        foreach (var id in ids) await SoftDeleteAsync(conn, tx, table, id, ct);
    }

    private static async Task UpsertAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string update, string insert,
        object?[] args, CancellationToken ct)
    {
        await using var up = Cmd(update, conn, tx, args);
        if (await up.ExecuteNonQueryAsync(ct) > 0) return;
        await using var ins = Cmd(insert, conn, tx, args);
        await ins.ExecuteNonQueryAsync(ct);
    }

    public static string ArchiveCode(ArchiveMode m) => m switch
    {
        ArchiveMode.None => "none", ArchiveMode.Interval => "interval", _ => "change",
    };

    public static string KindCode(AlarmKind k) => k switch
    {
        AlarmKind.HiHi => "hihi", AlarmKind.Hi => "hi", AlarmKind.Lo => "lo", AlarmKind.LoLo => "lolo",
        AlarmKind.DiscreteOn => "discrete_on", AlarmKind.DiscreteOff => "discrete_off", _ => "bad_quality",
    };

    /// <summary>Первый запуск после миграции: текущие таблицы становятся версией 1.</summary>
    public static async Task<int> EnsureInitialVersionAsync(NpgsqlDataSource ds, CancellationToken ct)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var lk = Cmd("SELECT pg_advisory_xact_lock(hashtext('scada.publish'))", conn, tx))
            await lk.ExecuteScalarAsync(ct);
        await using (var check = Cmd("SELECT max(version) FROM config_versions", conn, tx))
        {
            var v = await check.ExecuteScalarAsync(ct);
            if (v is int existing) { await tx.CommitAsync(ct); return existing; }
        }
        var snap = await LoadDraftAsync(conn, tx, ct);
        await using (var ins = Cmd(
            "INSERT INTO config_versions (version, created_by, comment, snapshot, changes) VALUES (1, 'system', $1, $2::jsonb, '[]')",
            conn, tx, "Исходная конфигурация", snap.Serialize()))
            await ins.ExecuteNonQueryAsync(ct);
        await using (var clear = Cmd("DELETE FROM draft_changes", conn, tx)) await clear.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
        return 1;
    }
}
