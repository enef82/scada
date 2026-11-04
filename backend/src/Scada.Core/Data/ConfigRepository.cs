using System.Data.Common;
using Npgsql;
using Scada.Core.Model;

namespace Scada.Core.Data;

public static class Db
{
    /// <summary>Источник соединений; search_path = scada.</summary>
    public static NpgsqlDataSource CreateDataSource(string connectionString, string appName)
    {
        var csb = new NpgsqlConnectionStringBuilder(connectionString)
        {
            ApplicationName = appName,
            SearchPath = "scada,public",
        };
        return NpgsqlDataSource.Create(csb.ConnectionString);
    }

    public static double? GetNullableDouble(this DbDataReader r, int i) => r.IsDBNull(i) ? null : r.GetDouble(i);
    public static int? GetNullableInt(this DbDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt32(i);
    public static string? GetNullableString(this DbDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    public static DateTime? GetNullableDateTime(this DbDataReader r, int i) =>
        r.IsDBNull(i) ? null : DateTime.SpecifyKind(r.GetDateTime(i), DateTimeKind.Utc);
    public static DateTime GetUtc(this DbDataReader r, int i) => DateTime.SpecifyKind(r.GetDateTime(i), DateTimeKind.Utc);

    public static NpgsqlParameter P(object? value) => new() { Value = value ?? DBNull.Value };
}

public static class ConfigRepository
{
    public const string DeviceColumns =
        "id, name, protocol, endpoint, settings::text, enabled, poll_ms, timeout_ms, description";

    public const string TagColumns =
        "id, name, description, device_id, address, data_type, unit, raw_low, raw_high, eng_low, eng_high, " +
        "poll_ms, stale_after_ms, deadband_ui, deadband_archive, archive_mode, archive_interval_s, " +
        "writable, write_min, write_max, write_confirm, enabled";

    public const string AlarmColumns =
        "id, tag_id, kind, limit_value, hysteresis, delay_ms, priority, message, enabled";

    public static async Task<ScadaConfig> LoadAsync(NpgsqlDataSource ds, CancellationToken ct)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        var devices = new List<DeviceDefinition>();
        await using (var cmd = new NpgsqlCommand($"SELECT {DeviceColumns} FROM devices ORDER BY id", conn))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) devices.Add(ReadDevice(r));

        var tags = new List<TagDefinition>();
        await using (var cmd = new NpgsqlCommand($"SELECT {TagColumns} FROM tags ORDER BY id", conn))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) tags.Add(ReadTag(r));

        var alarms = new List<AlarmDefinition>();
        await using (var cmd = new NpgsqlCommand($"SELECT {AlarmColumns} FROM alarm_defs ORDER BY id", conn))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) alarms.Add(ReadAlarm(r));

        return new ScadaConfig(devices, tags, alarms);
    }

    public static DeviceDefinition ReadDevice(DbDataReader r) => new()
    {
        Id = r.GetInt32(0),
        Name = r.GetString(1),
        Protocol = EnumCodes.ParseProtocol(r.GetString(2)),
        Endpoint = r.GetString(3),
        SettingsJson = r.GetString(4),
        Enabled = r.GetBoolean(5),
        PollMs = r.GetInt32(6),
        TimeoutMs = r.GetInt32(7),
        Description = r.GetString(8),
    };

    public static TagDefinition ReadTag(DbDataReader r) => new()
    {
        Id = r.GetInt32(0),
        Name = r.GetString(1),
        Description = r.GetString(2),
        DeviceId = r.GetInt32(3),
        Address = r.GetString(4),
        DataType = EnumCodes.ParseDataType(r.GetString(5)),
        Unit = r.GetString(6),
        RawLow = r.GetNullableDouble(7),
        RawHigh = r.GetNullableDouble(8),
        EngLow = r.GetNullableDouble(9),
        EngHigh = r.GetNullableDouble(10),
        PollMs = r.GetNullableInt(11),
        StaleAfterMs = r.GetNullableInt(12),
        DeadbandUi = r.GetDouble(13),
        DeadbandArchive = r.GetDouble(14),
        ArchiveMode = EnumCodes.ParseArchiveMode(r.GetString(15)),
        ArchiveIntervalS = r.GetInt32(16),
        Writable = r.GetBoolean(17),
        WriteMin = r.GetNullableDouble(18),
        WriteMax = r.GetNullableDouble(19),
        WriteConfirm = r.GetBoolean(20),
        Enabled = r.GetBoolean(21),
    };

    public static AlarmDefinition ReadAlarm(DbDataReader r) => new()
    {
        Id = r.GetInt32(0),
        TagId = r.GetInt32(1),
        Kind = AlarmDefinition.ParseKind(r.GetString(2)),
        Limit = r.GetNullableDouble(3),
        Hysteresis = r.GetDouble(4),
        DelayMs = r.GetInt32(5),
        Priority = r.GetInt16(6),
        Message = r.GetString(7),
        Enabled = r.GetBoolean(8),
    };
}
