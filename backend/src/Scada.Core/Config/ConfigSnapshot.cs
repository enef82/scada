using System.Text.Json;
using System.Text.Json.Serialization;
using Scada.Core.Model;

namespace Scada.Core.Config;

/// <summary>Ссылка на зафиксированную версию мнемосхемы (документы схем хранятся в mnemo_versions).</summary>
public sealed record MnemoRef(int Id, string Name, int? ParentId, int SortOrder, int Version);

/// <summary>
/// Полная конфигурация в одной точке времени: черновик или опубликованная версия.
/// Опубликованный снимок хранится в config_versions.snapshot и не изменяется.
/// </summary>
public sealed record ConfigSnapshot(
    IReadOnlyList<DeviceDefinition> Devices,
    IReadOnlyList<TagDefinition> Tags,
    IReadOnlyList<AlarmDefinition> Alarms,
    IReadOnlyList<MnemoRef> Mnemos)
{
    public static readonly ConfigSnapshot Empty = new([], [], [], []);

    public ScadaConfig ToRuntime() => new(Devices, Tags, Alarms);

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public string Serialize() => JsonSerializer.Serialize(this, Json);

    public static ConfigSnapshot Deserialize(string json) =>
        JsonSerializer.Deserialize<ConfigSnapshot>(json, Json) ?? Empty;
}
