using System.Text.Json;
using System.Text.Json.Serialization;
using Scada.Core.Model;

namespace Scada.Core.Config;

/// <summary>Ссылка на зафиксированную версию мнемосхемы (документы схем хранятся в mnemo_versions).</summary>
public sealed record MnemoRef(int Id, string Name, int? ParentId, int SortOrder, int Version, int AreaId = 1);

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

    /// <summary>Область тега: собственная или область устройства (0/неизвестно — корень).</summary>
    public int TagArea(TagDefinition t) =>
        t.AreaId is > 0 and var a ? a : Devices.FirstOrDefault(d => d.Id == t.DeviceId)?.AreaId is > 0 and var da ? da : 1;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public string Serialize() => JsonSerializer.Serialize(this, Json);

    public static ConfigSnapshot Deserialize(string json) =>
        JsonSerializer.Deserialize<ConfigSnapshot>(json, Json) ?? Empty;
}
