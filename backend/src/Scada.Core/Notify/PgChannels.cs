using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Scada.Core.Model;

namespace Scada.Core.Notify;

/// <summary>Каналы LISTEN/NOTIFY. Контракт описан в db/001_schema.sql.</summary>
public static class PgChannels
{
    public const string Values = "scada_values";
    public const string Alarm = "scada_alarm";
    public const string AlarmAck = "scada_alarm_ack";
    public const string Command = "scada_command";
    public const string CommandDone = "scada_command_done";
    /// <summary>Опубликована новая версия конфигурации (payload — номер версии).</summary>
    public const string Config = "scada_config";
    /// <summary>Служба сбора применила версию: {"version":N,"ms":..}.</summary>
    public const string ConfigApplied = "scada_config_applied";
    /// <summary>Изменён черновик (имя таблицы) — только для обновления интерфейса инженеров.</summary>
    public const string Draft = "scada_draft";
    public const string Device = "scada_device";
    public const string Heartbeat = "scada_heartbeat";

    /// <summary>Лимит полезной нагрузки NOTIFY — 8000 байт; оставляем запас.</summary>
    public const int MaxPayloadBytes = 7500;
}

public static class JsonOpts
{
    public static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
}

/// <summary>Запрос квитирования/снятия с контроля, который веб шлёт службе сбора.</summary>
public sealed record AlarmAckRequest(
    int[] Ids, string User, string? Comment, string Action = "ack", int? ShelveMinutes = null);

public sealed record HeartbeatPayload(long Ts, int Tags, int Devices, int HistorianQueue, long SpoolBytes, int ConfigVersion = 0);

/// <summary>
/// Компактная упаковка значений тегов: {"v":[[id,num,str,q,tsMs],...]}.
/// Значения режутся на несколько сообщений, чтобы каждое влезло в лимит NOTIFY.
/// </summary>
public static class ValuesPayload
{
    public static IEnumerable<string> Pack(IReadOnlyList<TagValue> values, int maxBytes = PgChannels.MaxPayloadBytes)
    {
        var sb = new StringBuilder(maxBytes + 256);
        var item = new StringBuilder(128);
        sb.Append("{\"v\":[");
        var count = 0;
        var bytes = 6;
        foreach (var v in values)
        {
            item.Clear();
            item.Append('[').Append(v.TagId.ToString(CultureInfo.InvariantCulture)).Append(',');
            item.Append(v.Num is { } n ? n.ToString("R", CultureInfo.InvariantCulture) : "null").Append(',');
            // JsonSerializer экранирует не-ASCII как \uXXXX, поэтому длина в символах = длина в байтах
            item.Append(v.Str is null ? "null" : JsonSerializer.Serialize(Truncate(v.Str, 1000))).Append(',');
            item.Append(((short)v.Quality).ToString(CultureInfo.InvariantCulture)).Append(',');
            item.Append(new DateTimeOffset(v.TsSource, TimeSpan.Zero).ToUnixTimeMilliseconds()).Append(']');

            if (count > 0 && bytes + item.Length + 3 > maxBytes)
            {
                sb.Append("]}");
                yield return sb.ToString();
                sb.Clear().Append("{\"v\":[");
                count = 0;
                bytes = 6;
            }
            if (count > 0) { sb.Append(','); bytes++; }
            sb.Append(item);
            bytes += item.Length;
            count++;
        }
        if (count > 0)
        {
            sb.Append("]}");
            yield return sb.ToString();
        }
    }

    public static List<TagValue> Unpack(string payload, DateTime nowUtc)
    {
        var list = new List<TagValue>();
        using var doc = JsonDocument.Parse(payload);
        foreach (var e in doc.RootElement.GetProperty("v").EnumerateArray())
        {
            var id = e[0].GetInt32();
            double? num = e[1].ValueKind == JsonValueKind.Null ? null : e[1].GetDouble();
            var str = e[2].ValueKind == JsonValueKind.Null ? null : e[2].GetString();
            var q = (Quality)e[3].GetInt16();
            var ts = DateTimeOffset.FromUnixTimeMilliseconds(e[4].GetInt64()).UtcDateTime;
            list.Add(new TagValue(id, num, str, q, ts, nowUtc));
        }
        return list;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
