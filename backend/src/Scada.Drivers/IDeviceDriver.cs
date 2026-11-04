using Microsoft.Extensions.Logging;
using Scada.Core.Model;

namespace Scada.Drivers;

/// <summary>
/// Единый контракт драйвера протокола. Один экземпляр — одно устройство (канал связи).
/// Методы вызываются последовательно из одного рабочего цикла устройства,
/// поэтому драйверу не нужна собственная защита от параллельных вызовов.
/// </summary>
public interface IDeviceDriver : IAsyncDisposable
{
    bool IsConnected { get; }

    Task ConnectAsync(CancellationToken ct);

    Task DisconnectAsync();

    /// <summary>
    /// Групповое чтение. Возвращает значение для каждого запрошенного тега;
    /// ошибка адреса отдельного тега — Bad-качество этого тега, а не исключение.
    /// Исключение означает отказ связи целиком.
    /// </summary>
    Task<IReadOnlyList<RawValue>> ReadAsync(IReadOnlyList<TagDefinition> tags, CancellationToken ct);

    /// <summary>Запись значения в сырых единицах. Исключение — запись не выполнена.</summary>
    Task WriteAsync(TagDefinition tag, object rawValue, CancellationToken ct);
}

/// <summary>Фабрика драйверов одного протокола. Регистрируется в DI.</summary>
public interface IDriverProvider
{
    Protocol Protocol { get; }
    IDeviceDriver Create(DeviceDefinition device, ILoggerFactory loggers);
}

public sealed class DriverRegistry
{
    private readonly Dictionary<Protocol, IDriverProvider> _providers;

    public DriverRegistry(IEnumerable<IDriverProvider> providers) =>
        _providers = providers.ToDictionary(p => p.Protocol);

    public bool Supports(Protocol p) => _providers.ContainsKey(p);

    public IDeviceDriver Create(DeviceDefinition device, ILoggerFactory loggers) =>
        _providers.TryGetValue(device.Protocol, out var p)
            ? p.Create(device, loggers)
            : throw new NotSupportedException($"Драйвер протокола {device.Protocol} не подключён");
}

/// <summary>Ошибка адреса/конфигурации тега (не ошибка связи).</summary>
public sealed class TagAddressException(string message) : Exception(message);
