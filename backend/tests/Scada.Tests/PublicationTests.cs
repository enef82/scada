using System.Text.Json;
using Scada.Core.Config;
using Scada.Core.Model;
using Scada.Drivers.Validation;

namespace Scada.Tests;

public class PublicationTests
{
    private static DeviceDefinition Dev(int id = 1, Protocol p = Protocol.ModbusTcp) =>
        new() { Id = id, Name = $"ПЛК {id}", Protocol = p, Endpoint = "10.0.0.1:502", PollMs = 1000, TimeoutMs = 1000 };

    private static TagDefinition Tag(int id, string name, string addr, TagDataType type = TagDataType.UInt16, bool writable = false) =>
        new() { Id = id, Name = name, DeviceId = 1, Address = addr, DataType = type, Writable = writable, WriteMin = writable ? 0 : null, WriteMax = writable ? 10 : null };

    private static ConfigSnapshot Snap(IEnumerable<TagDefinition> tags, IEnumerable<AlarmDefinition>? alarms = null,
        IEnumerable<MnemoRef>? mnemos = null, IEnumerable<DeviceDefinition>? devices = null) =>
        new((devices ?? [Dev()]).ToList(), tags.ToList(), (alarms ?? []).ToList(), (mnemos ?? []).ToList());

    private static readonly Dictionary<int, JsonElement> NoDocs = new();

    private static List<string> Codes(List<ValidationIssue> issues, string level) =>
        issues.Where(i => i.Level == level).Select(i => i.Code).ToList();

    [Fact]
    public void Clean_config_has_no_errors()
    {
        var s = Snap([Tag(1, "L", "ir:0"), Tag(2, "SP", "hr:10", TagDataType.Float32, writable: true)]);
        var issues = ConfigValidator.Validate(s, s, NoDocs);
        Assert.Equal(0, Codes(issues, ValidationIssue.Error).Count);
    }

    [Fact]
    public void Device_warnings_only_for_new_or_changed_devices()
    {
        var fast = Dev() with { PollMs = 500, TimeoutMs = 2000 };
        var s = Snap([Tag(1, "L", "ir:0")], devices: [fast]);
        // устройство уже работало с такими параметрами — не повторяем замечание при каждой публикации
        Assert.False(ConfigValidator.Validate(s, s, NoDocs).Any(i => i.Code == "poll-lt-timeout"));
        // новое устройство или изменённый период опроса — предупреждаем
        Assert.True(Codes(ConfigValidator.Validate(s, null, NoDocs), ValidationIssue.Warning).Contains("poll-lt-timeout"));
        var before = Snap([Tag(1, "L", "ir:0")], devices: [fast with { PollMs = 3000 }]);
        Assert.True(Codes(ConfigValidator.Validate(s, before, NoDocs), ValidationIssue.Warning).Contains("poll-lt-timeout"));
    }

    [Fact]
    public void Bad_address_and_partial_scaling_are_errors()
    {
        var bad = Tag(1, "X", "hr100");
        var part = Tag(2, "Y", "ir:1") with { RawLow = 0, RawHigh = 100 };
        var errs = Codes(ConfigValidator.Validate(Snap([bad, part]), null, NoDocs), ValidationIssue.Error);
        Assert.True(errs.Contains("address-invalid"));
        Assert.True(errs.Contains("scaling-partial"));
    }

    [Fact]
    public void Overlap_with_writable_tag_is_error_read_only_is_warning_bits_ok()
    {
        // float32 на hr:10 занимает 10–11, uint16 на hr:11 — пересечение
        var w = ConfigValidator.Validate(Snap([Tag(1, "SP", "hr:10", TagDataType.Float32, true), Tag(2, "Z", "hr:11")]), null, NoDocs);
        Assert.True(Codes(w, ValidationIssue.Error).Contains("address-overlap"));

        var ro = ConfigValidator.Validate(Snap([Tag(1, "A", "ir:10", TagDataType.Float32), Tag(2, "B", "ir:11")]), null, NoDocs);
        Assert.True(Codes(ro, ValidationIssue.Warning).Contains("address-overlap-ro"));

        var bits = ConfigValidator.Validate(Snap([Tag(1, "B0", "hr:5.0", TagDataType.Bool), Tag(2, "B1", "hr:5.1", TagDataType.Bool)]), null, NoDocs);
        Assert.False(bits.Any(i => i.Code.StartsWith("address-overlap")));
    }

    [Fact]
    public void Alarm_order_and_missing_limit()
    {
        var t = Tag(1, "L", "ir:0");
        AlarmDefinition A(int id, AlarmKind k, double? lim) => new() { Id = id, TagId = 1, Kind = k, Limit = lim, Message = "m" };
        var errs = Codes(ConfigValidator.Validate(Snap([t], [A(1, AlarmKind.Hi, 3.6), A(2, AlarmKind.HiHi, 3.5), A(3, AlarmKind.Lo, null)]), null, NoDocs),
            ValidationIssue.Error);
        Assert.True(errs.Contains("alarm-order"));
        Assert.True(errs.Contains("alarm-no-limit"));
    }

    [Fact]
    public void Mnemo_with_unknown_tag_is_error()
    {
        var doc = JsonSerializer.Deserialize<JsonElement>(
            """{"elements":[{"type":"lamp","props":{"tag":"KNS1_P2_Fault","label":"Н-2"}},{"type":"link","props":{"target":99}}]}""");
        var issues = ConfigValidator.Validate(Snap([Tag(1, "L", "ir:0")], mnemos: [new MnemoRef(1, "КНС-1", null, 0, 3)]), null,
            new Dictionary<int, JsonElement> { [1] = doc });
        Assert.True(Codes(issues, ValidationIssue.Error).Contains("mnemo-tag-missing"));
        Assert.True(Codes(issues, ValidationIssue.Warning).Contains("mnemo-link-missing"));
    }

    [Fact]
    public void Becoming_writable_warns()
    {
        var before = Snap([Tag(1, "SP", "hr:10")]);
        var after = Snap([Tag(1, "SP", "hr:10", writable: true) with { WriteConfirm = false }]);
        var warns = Codes(ConfigValidator.Validate(after, before, NoDocs), ValidationIssue.Warning);
        Assert.True(warns.Contains("became-writable"));
        Assert.True(warns.Contains("confirm-removed"));
    }

    [Fact]
    public void Diff_reports_created_updated_deleted_with_fields()
    {
        var before = Snap([Tag(1, "A", "ir:0"), Tag(2, "B", "ir:1")]);
        var after = Snap([Tag(1, "A", "ir:5"), Tag(3, "C", "ir:2")]);
        var changes = ConfigDiff.Compute(before, after);
        Assert.Equal(3, changes.Count);
        var upd = changes.Single(c => c.Op == "updated");
        Assert.Equal("address", upd.Fields.Single().Field);
        Assert.True(upd.Highlight);
        Assert.Equal("C", changes.Single(c => c.Op == "created").Name);
        Assert.Equal("B", changes.Single(c => c.Op == "deleted").Name);
        Assert.Equal(1, ConfigDiff.Summary(changes)["tag"]["created"]);
    }

    [Fact]
    public void Snapshot_roundtrip_keeps_everything()
    {
        var s = Snap([Tag(1, "A", "ir:0") with { RawLow = 0, RawHigh = 1000, EngLow = 0, EngHigh = 4, ArchiveMode = ArchiveMode.Interval }],
            [new AlarmDefinition { Id = 7, TagId = 1, Kind = AlarmKind.DiscreteOn, Message = "Дверь" }],
            [new MnemoRef(3, "Обзор", null, 0, 12)]);
        var back = ConfigSnapshot.Deserialize(s.Serialize());
        Assert.Equal(s.Tags[0], back.Tags[0]);
        Assert.Equal(s.Devices[0], back.Devices[0]);
        Assert.Equal(s.Alarms[0], back.Alarms[0]);
        Assert.Equal(s.Mnemos[0], back.Mnemos[0]);
        Assert.Equal(0, ConfigDiff.Compute(s, back).Count);
    }
}
