using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;
using Scada.Core.Model;
using Quality = Scada.Core.Model.Quality;

namespace Scada.Drivers.OpcUa;

/// <summary>
/// Параметры устройства OPC UA (devices.settings):
/// {"security":true,"autoAcceptUntrusted":false,"user":"","password":"","pkiPath":"/var/lib/scada/pki"}
/// Адрес тега — NodeId в стандартной записи: ns=2;s=Line1.Pump1.Speed или ns=3;i=1001.
/// </summary>
public sealed record OpcUaSettings
{
    public bool Security { get; init; } = true;
    public bool AutoAcceptUntrusted { get; init; }
    public string? User { get; init; }
    public string? Password { get; init; }
    public string PkiPath { get; init; } = "/var/lib/scada/pki";

    public static OpcUaSettings Parse(string json)
    {
        var s = JsonSerializer.Deserialize<OpcUaSettings>(
            string.IsNullOrWhiteSpace(json) ? "{}" : json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        return s ?? new OpcUaSettings();
    }
}

public sealed class OpcUaDriverProvider : IDriverProvider
{
    public Protocol Protocol => Protocol.OpcUa;
    public IDeviceDriver Create(DeviceDefinition device, ILoggerFactory loggers) =>
        new OpcUaDriver(device, loggers.CreateLogger<OpcUaDriver>());
}

/// <summary>
/// Клиент OPC UA. В MVP — групповое чтение (Read) по циклу опроса; это единообразно с Modbus
/// и предсказуемо по нагрузке. Подписки (MonitoredItems) — следующий шаг.
/// </summary>
public sealed class OpcUaDriver : IDeviceDriver
{
    private readonly DeviceDefinition _device;
    private readonly OpcUaSettings _settings;
    private readonly ILogger _log;
    private readonly Dictionary<int, (NodeId Node, string Text)> _nodeCache = new();
    private ApplicationConfiguration? _config;
    private ISession? _session;

    public OpcUaDriver(DeviceDefinition device, ILogger log)
    {
        _device = device;
        _log = log;
        _settings = OpcUaSettings.Parse(device.SettingsJson);
    }

    public bool IsConnected => _session?.Connected == true;

    public async Task ConnectAsync(CancellationToken ct)
    {
        await DisconnectAsync();
        _config ??= await BuildConfigurationAsync();

        var endpointDescription = CoreClientUtils.SelectEndpoint(
            _config, _device.Endpoint, _settings.Security, _device.TimeoutMs);
        var endpoint = new ConfiguredEndpoint(null, endpointDescription, EndpointConfiguration.Create(_config));

        IUserIdentity identity = string.IsNullOrEmpty(_settings.User)
            ? new UserIdentity(new AnonymousIdentityToken())
            : new UserIdentity(_settings.User, _settings.Password ?? "");

        // (config, endpoint, updateBeforeConnect, checkDomain, sessionName, sessionTimeout, identity, locales, ct)
        _session = await Session.Create(
            _config, endpoint, false, false,
            $"SCADA MVP / {_device.Name}", 60_000u, identity, null, ct);

        _log.LogInformation("OPC UA {Device}: сессия открыта ({Policy})",
            _device.Name, endpointDescription.SecurityPolicyUri);
    }

    private async Task<ApplicationConfiguration> BuildConfigurationAsync()
    {
        var pki = _settings.PkiPath;
        var config = new ApplicationConfiguration
        {
            ApplicationName = "SCADA MVP",
            ApplicationUri = Utils.Format("urn:{0}:ScadaMvp", System.Net.Dns.GetHostName()),
            ApplicationType = ApplicationType.Client,
            SecurityConfiguration = new SecurityConfiguration
            {
                ApplicationCertificate = new CertificateIdentifier
                {
                    StoreType = CertificateStoreType.Directory,
                    StorePath = Path.Combine(pki, "own"),
                    SubjectName = "CN=SCADA MVP, O=NPK Energoeffekt",
                },
                TrustedIssuerCertificates = new CertificateTrustList
                    { StoreType = CertificateStoreType.Directory, StorePath = Path.Combine(pki, "issuer") },
                TrustedPeerCertificates = new CertificateTrustList
                    { StoreType = CertificateStoreType.Directory, StorePath = Path.Combine(pki, "trusted") },
                RejectedCertificateStore = new CertificateTrustList
                    { StoreType = CertificateStoreType.Directory, StorePath = Path.Combine(pki, "rejected") },
                AutoAcceptUntrustedCertificates = _settings.AutoAcceptUntrusted,
                AddAppCertToTrustedStore = false,
            },
            TransportConfigurations = new TransportConfigurationCollection(),
            TransportQuotas = new TransportQuotas { OperationTimeout = _device.TimeoutMs },
            ClientConfiguration = new ClientConfiguration { DefaultSessionTimeout = 60_000 },
        };
        await config.Validate(ApplicationType.Client);

        var app = new ApplicationInstance
        {
            ApplicationName = config.ApplicationName,
            ApplicationType = ApplicationType.Client,
            ApplicationConfiguration = config,
        };
        await app.CheckApplicationInstanceCertificate(false, 2048);

        // Недоверенный сертификат сервера попадает в pki/rejected. Инженер переносит его
        // в pki/trusted (в UI — следующая версия). Автоприём — только для стенда.
        config.CertificateValidator.CertificateValidation += (_, e) =>
        {
            if (e.Error.StatusCode == StatusCodes.BadCertificateUntrusted && _settings.AutoAcceptUntrusted)
                e.Accept = true;
        };
        return config;
    }

    public async Task DisconnectAsync()
    {
        if (_session is null) return;
        try { await _session.CloseAsync(); }
        catch (Exception ex) { _log.LogDebug(ex, "OPC UA: ошибка закрытия сессии"); }
        _session.Dispose();
        _session = null;
    }

    public async ValueTask DisposeAsync() => await DisconnectAsync();

    private NodeId Node(TagDefinition tag)
    {
        if (_nodeCache.TryGetValue(tag.Id, out var c) && c.Text == tag.Address) return c.Node;
        NodeId node;
        try { node = NodeId.Parse(tag.Address); }
        catch (Exception ex) { throw new TagAddressException($"NodeId '{tag.Address}': {ex.Message}"); }
        _nodeCache[tag.Id] = (node, tag.Address);
        return node;
    }

    public async Task<IReadOnlyList<RawValue>> ReadAsync(IReadOnlyList<TagDefinition> tags, CancellationToken ct)
    {
        var session = _session ?? throw new IOException("OPC UA: нет сессии");
        var result = new List<RawValue>(tags.Count);
        var request = new ReadValueIdCollection();
        var map = new List<TagDefinition>();

        foreach (var t in tags)
        {
            try
            {
                request.Add(new ReadValueId { NodeId = Node(t), AttributeId = Attributes.Value });
                map.Add(t);
            }
            catch (TagAddressException)
            {
                result.Add(new RawValue(t.Id, null, Quality.BadConfigError, DateTime.UtcNow));
            }
        }
        if (request.Count == 0) return result;

        var response = await session.ReadAsync(null, 0, TimestampsToReturn.Both, request, ct);
        var now = DateTime.UtcNow;
        for (var i = 0; i < map.Count; i++)
        {
            var dv = response.Results[i];
            var ts = dv.SourceTimestamp != DateTime.MinValue ? dv.SourceTimestamp
                   : dv.ServerTimestamp != DateTime.MinValue ? dv.ServerTimestamp : now;
            result.Add(new RawValue(map[i].Id, dv.Value,
                QualityExtensions.FromOpcUaStatus(dv.StatusCode.Code),
                DateTime.SpecifyKind(ts, DateTimeKind.Utc)));
        }
        return result;
    }

    public async Task WriteAsync(TagDefinition tag, object rawValue, CancellationToken ct)
    {
        var session = _session ?? throw new IOException("OPC UA: нет сессии");
        var typed = ConvertForWrite(rawValue, tag.DataType);
        var write = new WriteValueCollection
        {
            new WriteValue
            {
                NodeId = Node(tag),
                AttributeId = Attributes.Value,
                Value = new DataValue(new Variant(typed)),
            },
        };
        var response = await session.WriteAsync(null, write, ct);
        var status = response.Results[0];
        if (StatusCode.IsBad(status))
            throw new InvalidOperationException($"OPC UA запись отклонена: {status}");
    }

    private static object ConvertForWrite(object v, TagDataType t)
    {
        var c = CultureInfo.InvariantCulture;
        return t switch
        {
            TagDataType.Bool => Convert.ToDouble(v, c) != 0,
            TagDataType.Int16 => Convert.ToInt16(v, c),
            TagDataType.UInt16 => Convert.ToUInt16(v, c),
            TagDataType.Int32 => Convert.ToInt32(v, c),
            TagDataType.UInt32 => Convert.ToUInt32(v, c),
            TagDataType.Float32 => Convert.ToSingle(v, c),
            TagDataType.Float64 => Convert.ToDouble(v, c),
            TagDataType.String => Convert.ToString(v, c) ?? "",
            _ => v,
        };
    }
}
