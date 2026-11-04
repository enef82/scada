using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Scada.Core.Model;

namespace Scada.Drivers.Simulator;

public sealed class SimulatorDriverProvider : IDriverProvider
{
    // память общая для всех устройств-симуляторов: значения переживают перезагрузку конфигурации
    private static readonly ConcurrentDictionary<string, object> Memory = new();

    public Protocol Protocol => Protocol.Simulator;

    public IDeviceDriver Create(DeviceDefinition device, ILoggerFactory loggers) =>
        new SimulatorDriver(device, Memory);
}

/// <summary>
/// Симулятор для стенда и демонстрации. Адреса:
///   sine:period=60,amp=10,offset=50     синусоида
///   ramp:period=30,min=0,max=100        пила
///   square:period=10                    меандр 0/1
///   random:min=0,max=100                шум
///   memory:имя[=начальное]              ячейка памяти, доступна для записи
///   follow:имя,tau=5                    инерционно следует за ячейкой памяти (модель объекта)
/// </summary>
public sealed class SimulatorDriver : IDeviceDriver
{
    private readonly DeviceDefinition _device;
    private readonly ConcurrentDictionary<string, object> _memory;
    private readonly Dictionary<int, double> _followState = new();
    private readonly Random _rnd = new();
    private DateTime _lastRead = DateTime.UtcNow;
    private bool _connected;

    public SimulatorDriver(DeviceDefinition device, ConcurrentDictionary<string, object> memory)
    {
        _device = device;
        _memory = memory;
    }

    public bool IsConnected => _connected;

    public Task ConnectAsync(CancellationToken ct)
    {
        // endpoint "fail" — имитация недоступного устройства для проверки диагностики
        if (_device.Endpoint.Equals("fail", StringComparison.OrdinalIgnoreCase))
            throw new IOException("Симулятор: устройство недоступно (endpoint=fail)");
        _connected = true;
        return Task.CompletedTask;
    }

    public Task DisconnectAsync()
    {
        _connected = false;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public Task<IReadOnlyList<RawValue>> ReadAsync(IReadOnlyList<TagDefinition> tags, CancellationToken ct)
    {
        if (!_connected) throw new IOException("Симулятор: нет соединения");
        var now = DateTime.UtcNow;
        var dt = Math.Max(0.001, (now - _lastRead).TotalSeconds);
        _lastRead = now;
        var t = now.Subtract(DateTime.UnixEpoch).TotalSeconds;

        var list = new List<RawValue>(tags.Count);
        foreach (var tag in tags)
        {
            try
            {
                list.Add(new RawValue(tag.Id, Evaluate(tag, t, dt), Quality.Good, now));
            }
            catch (Exception)
            {
                list.Add(new RawValue(tag.Id, null, Quality.BadConfigError, now));
            }
        }
        return Task.FromResult<IReadOnlyList<RawValue>>(list);
    }

    private object Evaluate(TagDefinition tag, double t, double dt)
    {
        var (kind, args, name) = Parse(tag.Address);
        double Arg(string k, double def) =>
            args.TryGetValue(k, out var s) ? double.Parse(s, CultureInfo.InvariantCulture) : def;

        switch (kind)
        {
            case "sine":
            {
                var period = Arg("period", 60);
                return Arg("offset", 50) + Arg("amp", 10) * Math.Sin(2 * Math.PI * t / period)
                       + Arg("noise", 0) * (_rnd.NextDouble() - 0.5);
            }
            case "ramp":
            {
                var period = Arg("period", 30);
                var min = Arg("min", 0);
                var max = Arg("max", 100);
                return min + (max - min) * (t % period) / period;
            }
            case "square":
                return (t % Arg("period", 10)) < Arg("period", 10) / 2;
            case "random":
            {
                var min = Arg("min", 0);
                var max = Arg("max", 100);
                return min + (max - min) * _rnd.NextDouble();
            }
            case "memory":
            {
                var init = args.TryGetValue("", out var s) ? s : "0";
                var v = _memory.GetOrAdd(name, _ => tag.DataType == TagDataType.String
                    ? init
                    : (object)double.Parse(init, CultureInfo.InvariantCulture));
                return tag.DataType == TagDataType.Bool ? Convert.ToDouble(v, CultureInfo.InvariantCulture) != 0 : v;
            }
            case "follow":
            {
                var target = _memory.TryGetValue(name, out var v) ? Convert.ToDouble(v, CultureInfo.InvariantCulture) : 0;
                var tau = Math.Max(0.1, Arg("tau", 5));
                var cur = _followState.TryGetValue(tag.Id, out var c) ? c : target;
                cur += (target - cur) * (1 - Math.Exp(-dt / tau));
                _followState[tag.Id] = cur;
                return cur + Arg("noise", 0) * (_rnd.NextDouble() - 0.5);
            }
            default:
                throw new TagAddressException($"Симулятор: неизвестный адрес '{tag.Address}'");
        }
    }

    public Task WriteAsync(TagDefinition tag, object rawValue, CancellationToken ct)
    {
        if (!_connected) throw new IOException("Симулятор: нет соединения");
        var (kind, _, name) = Parse(tag.Address);
        if (kind != "memory") throw new TagAddressException("Симулятор: запись только в memory:*");
        _memory[name] = tag.DataType == TagDataType.String
            ? Convert.ToString(rawValue, CultureInfo.InvariantCulture) ?? ""
            : (object)Convert.ToDouble(rawValue, CultureInfo.InvariantCulture);
        return Task.CompletedTask;
    }

    /// <summary>"kind:a=1,b=2" | "memory:name=init" | "follow:name,tau=5"</summary>
    internal static (string Kind, Dictionary<string, string> Args, string Name) Parse(string address)
    {
        var idx = address.IndexOf(':');
        var kind = (idx < 0 ? address : address[..idx]).Trim().ToLowerInvariant();
        var rest = idx < 0 ? "" : address[(idx + 1)..];
        var args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var name = "";

        if (kind == "memory")
        {
            var eq = rest.IndexOf('=');
            name = (eq < 0 ? rest : rest[..eq]).Trim();
            if (eq >= 0) args[""] = rest[(eq + 1)..].Trim();
            if (name.Length == 0) throw new TagAddressException("memory: требуется имя ячейки");
            return (kind, args, name);
        }

        foreach (var part in rest.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=');
            if (eq < 0) name = part;
            else args[part[..eq].Trim()] = part[(eq + 1)..].Trim();
        }
        return (kind, args, name);
    }
}
