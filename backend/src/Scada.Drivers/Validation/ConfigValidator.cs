using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Scada.Core.Config;
using Scada.Core.Model;
using Scada.Drivers.Modbus;

namespace Scada.Drivers.Validation;

public sealed record ValidationIssue(string Code, string Level, string Message, string ObjectType, int? ObjectId)
{
    public const string Error = "error";
    public const string Warning = "warning";
}

public sealed class ValidationOptions
{
    /// <summary>Порог оценки нагрузки архива, записей в сутки.</summary>
    public long ArchiveRowsPerDayLimit { get; set; } = 100_000_000;
    /// <summary>Сколько однотипных сообщений выводить, остальные сворачиваются в одно.</summary>
    public int MaxPerCode { get; set; } = 30;
}

/// <summary>
/// Проверки черновика перед публикацией (раздел «Проверки» документа по публикации).
/// Ошибка блокирует публикацию, предупреждение требует отметки «Ознакомлен».
/// </summary>
public static partial class ConfigValidator
{
    private static readonly string[] TagKeys = ["tag", "stateTag", "faultTag", "cmdTag", "visTag"];
    private static readonly HashSet<AlarmKind> Analog = [AlarmKind.HiHi, AlarmKind.Hi, AlarmKind.Lo, AlarmKind.LoLo];

    [GeneratedRegex(@"^(ns=\d+;)?(i=\d+|s=.+|g=[0-9a-fA-F-]{36}|b=[A-Za-z0-9+/=]+)$")]
    private static partial Regex OpcNodeId();

    [GeneratedRegex(@"^(sine|ramp|square|random|memory|follow)(:.*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex SimAddress();

    public static List<ValidationIssue> Validate(
        ConfigSnapshot next, ConfigSnapshot? prev, IReadOnlyDictionary<int, JsonElement> mnemoDocs, ValidationOptions? options = null)
    {
        var opt = options ?? new ValidationOptions();
        var issues = new List<ValidationIssue>();
        void Add(string code, string level, string msg, string type, int? id) => issues.Add(new(code, level, msg, type, id));

        var devices = next.Devices.ToDictionary(d => d.Id);
        var tagsByName = next.Tags.ToDictionary(t => t.Name, StringComparer.Ordinal);
        var tagsById = next.Tags.ToDictionary(t => t.Id);
        var prevTags = prev?.Tags.ToDictionary(t => t.Id) ?? new Dictionary<int, TagDefinition>();
        var mnemoIds = next.Mnemos.Select(m => m.Id).ToHashSet();

        // ---- теги
        foreach (var t in next.Tags)
        {
            if (!devices.TryGetValue(t.DeviceId, out var dev))
            {
                Add("tag-device-missing", ValidationIssue.Error, $"Тег {t.Name}: устройство удалено или не существует", "tag", t.Id);
                continue;
            }
            if (AddressError(dev.Protocol, t) is { } err)
                Add("address-invalid", ValidationIssue.Error, $"Тег {t.Name}: адрес «{t.Address}» — {err}", "tag", t.Id);

            var scaling = new[] { t.RawLow, t.RawHigh, t.EngLow, t.EngHigh };
            var filled = scaling.Count(x => x is not null);
            if (filled is > 0 and < 4)
                Add("scaling-partial", ValidationIssue.Error, $"Тег {t.Name}: масштабирование заполнено не полностью ({filled} из 4 полей)", "tag", t.Id);
            else if (filled == 4 && (t.RawLow == t.RawHigh || t.EngLow == t.EngHigh))
                Add("scaling-equal", ValidationIssue.Error, $"Тег {t.Name}: границы масштаба совпадают", "tag", t.Id);

            if (t.Writable && t.DataType is not (TagDataType.Bool or TagDataType.String) && t.WriteMin is null && t.WriteMax is null)
                Add("write-no-range", ValidationIssue.Warning, $"Тег {t.Name}: запись разрешена без диапазона — принимается любое значение", "tag", t.Id);

            prevTags.TryGetValue(t.Id, out var old);
            if (t.Writable && (old is null || !old.Writable))
                Add("became-writable", ValidationIssue.Warning, $"Тег {t.Name}: разрешена запись из SCADA", "tag", t.Id);
            if (t.Writable && !t.WriteConfirm && (old is null || old.WriteConfirm || !old.Writable))
                Add("confirm-removed", ValidationIssue.Warning, $"Тег {t.Name}: команды выполняются без подтверждения оператора", "tag", t.Id);
        }

        // ---- пересечение адресов Modbus
        foreach (var dev in next.Devices.Where(d => d.Protocol == Protocol.ModbusTcp))
            CheckModbusOverlaps(dev, next.Tags.Where(t => t.DeviceId == dev.Id && t.Enabled).ToList(), Add);

        // ---- устройства: «технические» предупреждения — только по новым и изменённым устройствам,
        // иначе одно и то же замечание повторяется при каждой публикации и его перестают читать
        var prevDevices = prev?.Devices.ToDictionary(d => d.Id) ?? new Dictionary<int, DeviceDefinition>();
        foreach (var d in next.Devices)
        {
            prevDevices.TryGetValue(d.Id, out var oldDev);
            var touched = oldDev is null || oldDev.Enabled != d.Enabled || oldDev.PollMs != d.PollMs || oldDev.TimeoutMs != d.TimeoutMs;
            if (d.Enabled && !next.Tags.Any(t => t.DeviceId == d.Id))
                Add("device-no-tags", ValidationIssue.Warning, $"Устройство «{d.Name}»: нет тегов", "device", d.Id);
            if (touched && !d.Enabled && next.Tags.Any(t => t.DeviceId == d.Id && t.Enabled))
                Add("device-disabled", ValidationIssue.Warning, $"Устройство «{d.Name}» отключено, его теги не опрашиваются", "device", d.Id);
            if (touched && d.Enabled && d.Protocol != Protocol.Simulator && d.PollMs < d.TimeoutMs)
                Add("poll-lt-timeout", ValidationIssue.Warning,
                    $"Устройство «{d.Name}»: опрос {d.PollMs} мс при тайм-ауте {d.TimeoutMs} мс — при обрыве связи опрос будет отставать", "device", d.Id);
        }

        // ---- тревоги
        foreach (var a in next.Alarms)
        {
            if (!tagsById.TryGetValue(a.TagId, out var tag))
            {
                Add("alarm-tag-missing", ValidationIssue.Error, $"Тревога «{a.Message}»: тег удалён", "alarm", a.Id);
                continue;
            }
            if (Analog.Contains(a.Kind) && a.Limit is null)
                Add("alarm-no-limit", ValidationIssue.Error, $"Тревога {KindName(a.Kind)} на {tag.Name}: не задана уставка", "alarm", a.Id);
            if (string.IsNullOrWhiteSpace(a.Message))
                Add("alarm-no-message", ValidationIssue.Error, $"Тревога {KindName(a.Kind)} на {tag.Name}: нет сообщения для оператора", "alarm", a.Id);
            if (Analog.Contains(a.Kind) && a.Limit is { } lim && tag.HasScaling)
            {
                var lo = Math.Min(tag.EngLow!.Value, tag.EngHigh!.Value);
                var hi = Math.Max(tag.EngLow!.Value, tag.EngHigh!.Value);
                if (lim < lo || lim > hi)
                    Add("alarm-out-of-range", ValidationIssue.Warning,
                        $"Тревога {KindName(a.Kind)} на {tag.Name}: уставка {Fmt(lim)} вне шкалы {Fmt(lo)}…{Fmt(hi)} {tag.Unit} — может не сработать", "alarm", a.Id);
            }
        }
        foreach (var g in next.Alarms.Where(a => a.Enabled && Analog.Contains(a.Kind) && a.Limit is not null).GroupBy(a => a.TagId))
        {
            double? L(AlarmKind k) => g.Where(a => a.Kind == k).Select(a => a.Limit).FirstOrDefault();
            var order = new[] { (AlarmKind.LoLo, L(AlarmKind.LoLo)), (AlarmKind.Lo, L(AlarmKind.Lo)), (AlarmKind.Hi, L(AlarmKind.Hi)), (AlarmKind.HiHi, L(AlarmKind.HiHi)) }
                .Where(x => x.Item2 is not null).ToList();
            for (var i = 1; i < order.Count; i++)
            {
                if (order[i].Item2 <= order[i - 1].Item2)
                {
                    var tag = tagsById.GetValueOrDefault(g.Key);
                    Add("alarm-order", ValidationIssue.Error,
                        $"{tag?.Name}: уставка {KindName(order[i].Item1)} = {Fmt(order[i].Item2!.Value)} не больше {KindName(order[i - 1].Item1)} = {Fmt(order[i - 1].Item2!.Value)}",
                        "alarm", g.First(a => a.Kind == order[i].Item1).Id);
                }
            }
        }

        // ---- схемы
        foreach (var m in next.Mnemos)
        {
            if (m.Version == 0 || !mnemoDocs.TryGetValue(m.Id, out var doc))
            {
                Add("mnemo-empty", ValidationIssue.Warning, $"Схема «{m.Name}» ещё не сохранялась — операторы увидят пустую схему", "mnemo", m.Id);
                continue;
            }
            if (!doc.TryGetProperty("elements", out var els) || els.ValueKind != JsonValueKind.Array) continue;
            foreach (var e in els.EnumerateArray())
            {
                if (!e.TryGetProperty("props", out var props) || props.ValueKind != JsonValueKind.Object) continue;
                var type = e.TryGetProperty("type", out var tp) ? tp.GetString() : "?";
                var label = props.TryGetProperty("label", out var lb) && lb.ValueKind == JsonValueKind.String && lb.GetString() is { Length: > 0 } s ? $" «{s}»" : "";
                foreach (var key in TagKeys)
                {
                    if (!props.TryGetProperty(key, out var v) || v.ValueKind != JsonValueKind.String) continue;
                    var name = v.GetString();
                    if (string.IsNullOrEmpty(name) || tagsByName.ContainsKey(name)) continue;
                    Add("mnemo-tag-missing", ValidationIssue.Error, $"Схема «{m.Name}», элемент {type}{label}: тег {name} не найден", "mnemo", m.Id);
                }
                if (type == "link" && props.TryGetProperty("target", out var target) && target.ValueKind == JsonValueKind.Number
                    && !mnemoIds.Contains(target.GetInt32()))
                    Add("mnemo-link-missing", ValidationIssue.Warning, $"Схема «{m.Name}»: переход{label} ведёт на удалённую схему", "mnemo", m.Id);
            }
        }

        // ---- нагрузка архива (оценка сверху: значение меняется на каждом опросе)
        double perDay = 0;
        foreach (var t in next.Tags.Where(t => t.Enabled))
        {
            if (!devices.TryGetValue(t.DeviceId, out var d) || !d.Enabled) continue;
            perDay += t.ArchiveMode switch
            {
                ArchiveMode.Change => 86_400_000.0 / Math.Max(50, t.PollMs ?? d.PollMs),
                ArchiveMode.Interval => 86_400.0 / Math.Max(1, t.ArchiveIntervalS),
                _ => 0,
            };
        }
        if (perDay > opt.ArchiveRowsPerDayLimit)
            Add("archive-load", ValidationIssue.Warning,
                $"Оценка архива сверху — {perDay / 1e6:0.#} млн записей в сутки (порог {opt.ArchiveRowsPerDayLimit / 1e6:0.#} млн). Проверьте зоны нечувствительности", "config", null);

        return Collapse(issues, opt.MaxPerCode);
    }

    private static void CheckModbusOverlaps(DeviceDefinition dev, List<TagDefinition> tags, Action<string, string, string, string, int?> add)
    {
        var items = new List<(TagDefinition Tag, ModbusAddress Addr, int Start, int End)>();
        foreach (var t in tags)
        {
            try
            {
                var a = ModbusAddress.Parse(t.Address);
                items.Add((t, a, a.Offset, a.Offset + a.Length(t.DataType)));
            }
            catch (TagAddressException) { /* уже отмечено как ошибка адреса */ }
        }
        foreach (var area in items.GroupBy(i => i.Addr.Area))
        {
            var list = area.OrderBy(i => i.Start).ToList();
            for (var i = 0; i < list.Count; i++)
            for (var j = i + 1; j < list.Count && list[j].Start < list[i].End; j++)
            {
                var a = list[i];
                var b = list[j];
                // разные биты одного регистра не пересекаются
                if (a.Addr.Bit is { } ba && b.Addr.Bit is { } bb && a.Start == b.Start && ba != bb) continue;
                var writable = a.Tag.Writable || b.Tag.Writable;
                add(writable ? "address-overlap" : "address-overlap-ro", writable ? ValidationIssue.Error : ValidationIssue.Warning,
                    $"Устройство «{dev.Name}»: {a.Tag.Name} ({a.Tag.Address}) и {b.Tag.Name} ({b.Tag.Address}) занимают одни регистры" +
                    (writable ? ", а запись в один испортит другой" : ""), "tag", b.Tag.Id);
            }
        }
    }

    private static string? AddressError(Protocol p, TagDefinition t)
    {
        if (string.IsNullOrWhiteSpace(t.Address)) return "адрес не задан";
        switch (p)
        {
            case Protocol.ModbusTcp:
                try
                {
                    var a = ModbusAddress.Parse(t.Address);
                    if (a.IsBitArea && t.DataType is not TagDataType.Bool) return "в области coils/discrete inputs допустим только bool";
                    if (a.Bit is not null && t.DataType is not TagDataType.Bool) return "адрес бита допустим только для bool";
                    if (t.DataType == TagDataType.String && a.StringRegs == 0) return "для строки укажите длину в регистрах: hr:200#10";
                    if (t.Writable && a.Area is ModbusArea.InputRegister or ModbusArea.DiscreteInput) return "область только для чтения, а тег записываемый";
                    return null;
                }
                catch (TagAddressException ex) { return ex.Message.Split(": ").Last(); }
                catch (FormatException) { return "неверный формат числа"; }
            case Protocol.OpcUa:
                return OpcNodeId().IsMatch(t.Address.Trim()) ? null : "ожидается NodeId, например ns=2;s=Pump1.Speed";
            default:
                return SimAddress().IsMatch(t.Address.Trim()) ? null : "неизвестная функция симулятора";
        }
    }

    private static List<ValidationIssue> Collapse(List<ValidationIssue> issues, int max)
    {
        var result = new List<ValidationIssue>();
        foreach (var g in issues.GroupBy(i => i.Code))
        {
            result.AddRange(g.Take(max));
            var rest = g.Count() - max;
            if (rest > 0) result.Add(g.First() with { Message = $"…и ещё {rest} таких же", ObjectId = null });
        }
        return result.OrderBy(i => i.Level == ValidationIssue.Error ? 0 : 1).ToList();
    }

    private static string KindName(AlarmKind k) => k switch
    {
        AlarmKind.HiHi => "HH", AlarmKind.Hi => "H", AlarmKind.Lo => "L", AlarmKind.LoLo => "LL",
        AlarmKind.DiscreteOn => "«включено»", AlarmKind.DiscreteOff => "«отключено»", _ => "«нет связи»",
    };

    private static string Fmt(double v) => v.ToString("0.###", CultureInfo.GetCultureInfo("ru-RU"));
}
