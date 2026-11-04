using Scada.Acquisition.Engine;
using Scada.Core.Model;
using Scada.Drivers;

namespace Scada.Acquisition.Devices;

/// <summary>
/// Запускает/останавливает рабочие циклы устройств. При изменении конфигурации
/// перезапускаются только устройства, у которых изменились параметры или набор тегов.
/// </summary>
public sealed class DeviceManager : IAsyncDisposable
{
    private readonly DriverRegistry _drivers;
    private readonly TagEngine _engine;
    private readonly ILoggerFactory _loggers;
    private readonly ILogger<DeviceManager> _log;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private Dictionary<int, DeviceWorker> _workers = new();

    public DeviceManager(DriverRegistry drivers, TagEngine engine, ILoggerFactory loggers, ILogger<DeviceManager> log)
    {
        _drivers = drivers;
        _engine = engine;
        _loggers = loggers;
        _log = log;
    }

    public IReadOnlyCollection<DeviceWorker> Workers => _workers.Values;

    public DeviceWorker? Get(int deviceId) => _workers.GetValueOrDefault(deviceId);

    public async Task ApplyConfigAsync(ScadaConfig config)
    {
        await _lock.WaitAsync();
        try
        {
            var tagsByDevice = config.Tags.GroupBy(t => t.DeviceId).ToDictionary(g => g.Key, g => g.ToList());
            var next = new Dictionary<int, DeviceWorker>();

            foreach (var dev in config.Devices)
            {
                var tags = tagsByDevice.GetValueOrDefault(dev.Id) ?? [];
                if (!dev.Enabled) continue;
                if (!_drivers.Supports(dev.Protocol))
                {
                    _log.LogError("Устройство {Device}: драйвер {Protocol} не подключён в этой сборке", dev.Name, dev.Protocol);
                    continue;
                }

                if (_workers.TryGetValue(dev.Id, out var existing)
                    && existing.Device == dev && existing.Tags.SequenceEqual(tags))
                {
                    next[dev.Id] = existing; // без изменений — не трогаем
                    continue;
                }

                try
                {
                    var driver = _drivers.Create(dev, _loggers);
                    var worker = new DeviceWorker(dev, tags, driver, _engine,
                        _loggers.CreateLogger($"Scada.Device.{dev.Name}"));
                    next[dev.Id] = worker;
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Устройство {Device}: ошибка параметров драйвера", dev.Name);
                }
            }

            // остановить удалённые/изменённые
            foreach (var (id, w) in _workers)
            {
                if (!next.TryGetValue(id, out var n) || !ReferenceEquals(n, w))
                {
                    _log.LogInformation("Остановка устройства {Device}", w.Device.Name);
                    await w.DisposeAsync();
                }
            }
            // запустить новые
            foreach (var (id, w) in next)
            {
                if (!_workers.TryGetValue(id, out var old) || !ReferenceEquals(old, w))
                {
                    _log.LogInformation("Запуск устройства {Device} ({Protocol}, тегов: {Count})",
                        w.Device.Name, w.Device.Protocol, w.Tags.Count);
                    w.Start();
                }
            }
            _workers = next;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var w in _workers.Values) await w.DisposeAsync();
        _workers.Clear();
    }
}
