using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Scada.Core.Model;

namespace Scada.Drivers.Modbus;

public sealed record ModbusSettings
{
    public byte UnitId { get; init; } = 1;
    public ByteOrder ByteOrder { get; init; } = ByteOrder.ABCD;
    /// <summary>Максимум регистров в одном запросе (стандарт — 125).</summary>
    public int MaxRegisters { get; init; } = 100;
    /// <summary>Допустимый «пропуск» между тегами при объединении в один запрос.</summary>
    public int MaxGap { get; init; } = 8;

    public static ModbusSettings Parse(string json)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        var r = doc.RootElement;
        var s = new ModbusSettings();
        if (r.TryGetProperty("unitId", out var u)) s = s with { UnitId = u.GetByte() };
        if (r.TryGetProperty("byteOrder", out var b)) s = s with { ByteOrder = Enum.Parse<ByteOrder>(b.GetString()!, true) };
        if (r.TryGetProperty("maxRegisters", out var m)) s = s with { MaxRegisters = Math.Clamp(m.GetInt32(), 1, 125) };
        if (r.TryGetProperty("maxGap", out var g)) s = s with { MaxGap = Math.Clamp(g.GetInt32(), 0, 100) };
        return s;
    }
}

public sealed class ModbusTcpDriverProvider : IDriverProvider
{
    public Protocol Protocol => Protocol.ModbusTcp;
    public IDeviceDriver Create(DeviceDefinition device, ILoggerFactory loggers) =>
        new ModbusTcpDriver(device, loggers.CreateLogger<ModbusTcpDriver>());
}

public sealed class ModbusTcpDriver : IDeviceDriver
{
    private readonly ModbusSettings _settings;
    private readonly ModbusTcpClient _client;
    private readonly ILogger _log;
    private readonly Dictionary<int, (ModbusAddress Addr, string Text)> _addrCache = new();

    public ModbusTcpDriver(DeviceDefinition device, ILogger log)
    {
        _log = log;
        _settings = ModbusSettings.Parse(device.SettingsJson);
        var (host, port) = ParseEndpoint(device.Endpoint);
        _client = new ModbusTcpClient(host, port, device.TimeoutMs);
    }

    public static (string Host, int Port) ParseEndpoint(string endpoint)
    {
        var e = endpoint.Trim();
        if (e.StartsWith("tcp://", StringComparison.OrdinalIgnoreCase)) e = e[6..];
        var idx = e.LastIndexOf(':');
        if (idx > 0 && int.TryParse(e[(idx + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var p))
            return (e[..idx], p);
        return (e, 502);
    }

    public bool IsConnected => _client.IsConnected;

    public Task ConnectAsync(CancellationToken ct) => _client.ConnectAsync(ct);

    public Task DisconnectAsync() => _client.CloseAsync();

    public ValueTask DisposeAsync() => _client.DisposeAsync();

    private ModbusAddress Addr(TagDefinition tag)
    {
        if (_addrCache.TryGetValue(tag.Id, out var c) && c.Text == tag.Address) return c.Addr;
        var a = ModbusAddress.Parse(tag.Address);
        _addrCache[tag.Id] = (a, tag.Address);
        return a;
    }

    public async Task<IReadOnlyList<RawValue>> ReadAsync(IReadOnlyList<TagDefinition> tags, CancellationToken ct)
    {
        var result = new List<RawValue>(tags.Count);
        var items = new List<(TagDefinition Tag, ModbusAddress Addr)>(tags.Count);
        foreach (var t in tags)
        {
            try { items.Add((t, Addr(t))); }
            catch (Exception ex) when (ex is TagAddressException or FormatException)
            {
                result.Add(new RawValue(t.Id, null, Quality.BadConfigError, DateTime.UtcNow));
            }
        }

        foreach (var group in items.GroupBy(i => i.Addr.Area))
        {
            var sorted = group.OrderBy(i => i.Addr.Offset).ToList();
            var isBit = group.Key is ModbusArea.Coil or ModbusArea.DiscreteInput;
            var maxLen = isBit ? 2000 : _settings.MaxRegisters;

            var i = 0;
            while (i < sorted.Count)
            {
                // собираем блок смежных адресов
                var start = sorted[i].Addr.Offset;
                var end = start + sorted[i].Addr.Length(sorted[i].Tag.DataType);
                var j = i + 1;
                while (j < sorted.Count)
                {
                    var a = sorted[j].Addr;
                    var aEnd = a.Offset + a.Length(sorted[j].Tag.DataType);
                    if (a.Offset - end > _settings.MaxGap || Math.Max(end, aEnd) - start > maxLen) break;
                    end = Math.Max(end, aEnd);
                    j++;
                }
                var block = sorted.GetRange(i, j - i);
                await ReadBlockAsync(group.Key, start, end - start, block, result, ct);
                i = j;
            }
        }
        return result;
    }

    private async Task ReadBlockAsync(
        ModbusArea area, int start, int count,
        List<(TagDefinition Tag, ModbusAddress Addr)> block, List<RawValue> result, CancellationToken ct)
    {
        try
        {
            var unit = _settings.UnitId;
            if (area is ModbusArea.Coil or ModbusArea.DiscreteInput)
            {
                var bits = await _client.ReadBitsAsync(unit, area == ModbusArea.Coil, start, count, ct);
                var ts = DateTime.UtcNow;
                foreach (var (tag, addr) in block)
                    result.Add(new RawValue(tag.Id, bits[addr.Offset - start], Quality.Good, ts));
            }
            else
            {
                var regs = await _client.ReadRegistersAsync(unit, area == ModbusArea.HoldingRegister, start, count, ct);
                var ts = DateTime.UtcNow;
                foreach (var (tag, addr) in block)
                {
                    try
                    {
                        var len = addr.Length(tag.DataType);
                        var span = regs.AsSpan(addr.Offset - start, len);
                        var value = ModbusCodec.Decode(span, tag.DataType, addr, addr.Order ?? _settings.ByteOrder);
                        result.Add(new RawValue(tag.Id, value, Quality.Good, ts));
                    }
                    catch (Exception ex) when (ex is TagAddressException or ArgumentException)
                    {
                        result.Add(new RawValue(tag.Id, null, Quality.BadConfigError, ts));
                    }
                }
            }
        }
        catch (ModbusException ex) when (ex.Code is 2 or 3)
        {
            // неверный адрес — ошибка конфигурации этих тегов, связь в порядке
            _log.LogWarning("Modbus {Area}:{Start}+{Count}: {Message}", area, start, count, ex.Message);
            var ts = DateTime.UtcNow;
            foreach (var (tag, _) in block)
                result.Add(new RawValue(tag.Id, null, Quality.BadConfigError, ts));
        }
        catch (ModbusException ex)
        {
            _log.LogWarning("Modbus {Area}:{Start}+{Count}: {Message}", area, start, count, ex.Message);
            var ts = DateTime.UtcNow;
            foreach (var (tag, _) in block)
                result.Add(new RawValue(tag.Id, null, Quality.BadDeviceFailure, ts));
        }
    }

    public async Task WriteAsync(TagDefinition tag, object rawValue, CancellationToken ct)
    {
        var addr = Addr(tag);
        switch (addr.Area)
        {
            case ModbusArea.Coil:
                await _client.WriteCoilAsync(_settings.UnitId, addr.Offset,
                    Convert.ToDouble(rawValue, CultureInfo.InvariantCulture) != 0, ct);
                break;
            case ModbusArea.HoldingRegister when addr.Bit is { } bit:
            {
                // запись бита: чтение-модификация-запись (неатомарно — предупреждение в документации)
                var cur = await _client.ReadRegistersAsync(_settings.UnitId, true, addr.Offset, 1, ct);
                var on = Convert.ToDouble(rawValue, CultureInfo.InvariantCulture) != 0;
                var v = on ? (ushort)(cur[0] | (1 << bit)) : (ushort)(cur[0] & ~(1 << bit));
                await _client.WriteRegistersAsync(_settings.UnitId, addr.Offset, [v], ct);
                break;
            }
            case ModbusArea.HoldingRegister:
                await _client.WriteRegistersAsync(_settings.UnitId, addr.Offset,
                    ModbusCodec.Encode(rawValue, tag.DataType, addr.Order ?? _settings.ByteOrder), ct);
                break;
            default:
                throw new TagAddressException($"Область {addr.Area} доступна только для чтения");
        }
    }
}
