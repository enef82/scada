using System.Text.Json;
using Scada.Core.Access;
using Scada.Core.Config;
using Scada.Core.Model;
using Scada.Drivers.Validation;

namespace Scada.Tests;

public class AccessTests
{
    // Установка(1) → Водоотведение(2) → КНС-1(3), КНС-2(4); Водоснабжение(5)
    private static readonly AreaTree Tree = new(
    [
        new AreaNode(1, null, "Установка"), new AreaNode(2, 1, "Водоотведение"), new AreaNode(3, 2, "КНС-1"),
        new AreaNode(4, 2, "КНС-2"), new AreaNode(5, 1, "Водоснабжение"),
    ]);

    private static readonly RoleDef Viewer = new(1, "viewer", "Наблюдатель", true, [Perm.View]);
    private static readonly RoleDef Operator = new(2, "operator", "Оператор", true, [Perm.View, Perm.AlarmAck, Perm.CmdControl, Perm.CmdSetpoint]);
    private static readonly RoleDef Senior = new(3, "senior_operator", "Старший оператор", true,
        [Perm.View, Perm.AlarmAck, Perm.AlarmShelve, Perm.CmdControl, Perm.CmdSetpoint, Perm.CmdCritical]);
    private static readonly RoleDef Security = new(6, "security_admin", "Администратор безопасности", true, [Perm.View, Perm.UsersManage, Perm.AuditView]);
    private static readonly RoleDef[] Roles = [Viewer, Operator, Senior, Security];
    private static readonly DateTime Now = new(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);

    private static UserPermissions For(string login, string[] groups, params Assignment[] a) =>
        AccessCalculator.Compute(Tree, Roles, a, login, groups, Now);

    private static Assignment U(string login, RoleDef role, int area, DateTime? until = null) => new(0, Assignment.User, login, role.Id, area, until);
    private static Assignment G(string group, RoleDef role, int area) => new(0, Assignment.Group, group, role.Id, area, null);

    [Fact]
    public void Assignment_covers_its_area_and_all_nested()
    {
        var p = For("ivanov", [], U("ivanov", Operator, 2));
        Assert.True(p.Has(Perm.CmdControl, 2));
        Assert.True(p.Has(Perm.CmdControl, 3));
        Assert.True(p.Has(Perm.CmdControl, 4));
        Assert.False(p.Has(Perm.CmdControl, 5));   // соседняя ветка
        Assert.False(p.Has(Perm.CmdControl, 1));   // родитель
        Assert.False(p.Has(Perm.CmdCritical, 3));  // этого права в роли нет
        Assert.Equal(3, p.AreasOrAll(Perm.View)!.Length);
    }

    [Fact]
    public void Rights_are_summed_from_personal_and_group_assignments()
    {
        var p = For("petrov", ["SCADA-Viewers", "SCADA-Senior-Operators"],
            G("SCADA-Viewers", Viewer, 1), G("SCADA-Senior-Operators", Senior, 3), U("someone-else", Security, 1));
        Assert.True(p.HasEverywhere(Perm.View));
        Assert.Null(p.AreasOrAll(Perm.View));       // везде — фильтр SQL не нужен
        Assert.True(p.Has(Perm.CmdCritical, 3));
        Assert.False(p.Has(Perm.CmdCritical, 4));
        Assert.False(p.HasAnywhere(Perm.UsersManage));
        Assert.True(p.Via(Perm.CmdCritical, 3, Tree)!.Contains("SCADA-Senior-Operators"));
    }

    [Fact]
    public void Group_names_are_case_insensitive_and_unknown_groups_give_nothing()
    {
        Assert.True(For("x", ["scada-viewers"], G("SCADA-Viewers", Viewer, 1)).Has(Perm.View, 4));
        Assert.False(For("x", ["Other"], G("SCADA-Viewers", Viewer, 1)).HasAnywhere(Perm.View));
    }

    [Fact]
    public void Expired_assignment_is_ignored()
    {
        var p = For("contractor", [], U("contractor", Operator, 4, Now.AddMinutes(-1)), U("contractor", Viewer, 4, Now.AddDays(3)));
        Assert.True(p.Has(Perm.View, 4));
        Assert.False(p.Has(Perm.CmdControl, 4));
    }

    [Fact]
    public void Users_and_audit_rights_work_only_on_root()
    {
        var onBranch = For("a", [], U("a", Security, 2));
        Assert.False(onBranch.HasAnywhere(Perm.UsersManage));
        Assert.False(onBranch.HasAnywhere(Perm.AuditView));
        Assert.True(onBranch.Has(Perm.View, 3));
        var onRoot = For("a", [], U("a", Security, 1));
        Assert.True(onRoot.HasEverywhere(Perm.UsersManage));
    }

    [Fact]
    public void Area_tree_paths_and_cycles()
    {
        Assert.Equal("Установка / Водоотведение / КНС-1", Tree.Path(3));
        Assert.True(Tree.IsWithin(3, 2));
        Assert.False(Tree.IsWithin(2, 3));
        // повреждённые данные с циклом не зацикливают расчёт
        var cyclic = new AreaTree([new AreaNode(1, null, "R"), new AreaNode(2, 3, "A"), new AreaNode(3, 2, "B")]);
        Assert.Equal(2, cyclic.Subtree(2).Count());
        Assert.False(cyclic.IsWithin(2, 1));
    }

    [Fact]
    public void Command_permission_depends_on_class_and_type()
    {
        Assert.Equal(Perm.CmdControl, AccessCalculator.CommandPermission(CommandClass.Normal, discrete: true));
        Assert.Equal(Perm.CmdSetpoint, AccessCalculator.CommandPermission(CommandClass.Important, discrete: false));
        Assert.Equal(Perm.CmdCritical, AccessCalculator.CommandPermission(CommandClass.Critical, discrete: true));
        Assert.Equal(CommandClass.Critical, EnumCodes.ParseCommandClass("critical"));
        Assert.Equal("important", CommandClass.Important.ToCode());
    }

    [Fact]
    public void Ldap_filter_values_are_escaped()
    {
        Assert.Equal(@"a\2a\28b\29\5c", LdapFilter.Escape(@"a*(b)\"));
        Assert.Equal("ivanov", LdapFilter.Escape("ivanov"));
    }

    // ------------------------------------------------------------------ конфигурация
    private static DeviceDefinition Dev(int id, int area) =>
        new() { Id = id, Name = $"ПЛК {id}", Protocol = Protocol.ModbusTcp, Endpoint = "10.0.0.1:502", AreaId = area };

    private static TagDefinition Tag(int id, int dev, int? area = null, CommandClass cls = CommandClass.Normal) =>
        new() { Id = id, Name = $"T{id}", DeviceId = dev, Address = $"hr:{id * 2}", DataType = TagDataType.UInt16, Writable = true,
                WriteMin = 0, WriteMax = 10, AreaId = area, CommandClass = cls };

    private static ConfigSnapshot Snap(DeviceDefinition[] d, TagDefinition[] t, MnemoRef[]? m = null) => new(d, t, [], m ?? []);

    [Fact]
    public void Tag_inherits_device_area_unless_it_has_its_own()
    {
        var s = Snap([Dev(1, 3)], [Tag(1, 1), Tag(2, 1, area: 4)]);
        Assert.Equal(3, s.TagArea(s.Tags[0]));
        Assert.Equal(4, s.TagArea(s.Tags[1]));
    }

    [Fact]
    public void Moving_object_touches_old_and_new_area()
    {
        var prev = Snap([Dev(1, 3)], [Tag(1, 1)]);
        var next = Snap([Dev(1, 4)], [Tag(1, 1)]);
        var change = ConfigDiff.Compute(prev, next).Single();
        Assert.True(change.Highlight);                       // перенос между областями выделяется
        var areas = ConfigDiff.ChangeAreas(change, prev, next).Order().ToArray();
        Assert.Equal("3,4", string.Join(',', areas));        // и устройство, и его теги в обеих областях
    }

    [Fact]
    public void Lowering_command_class_warns_and_unknown_area_is_error()
    {
        var prev = Snap([Dev(1, 3)], [Tag(1, 1, cls: CommandClass.Critical)]);
        var next = Snap([Dev(1, 3)], [Tag(1, 1, cls: CommandClass.Normal)], [new MnemoRef(1, "КНС", null, 0, 0, 99)]);
        var issues = ConfigValidator.Validate(next, prev, new Dictionary<int, JsonElement>(),
            new ValidationOptions { KnownAreas = new HashSet<int> { 1, 2, 3, 4 } });
        Assert.True(issues.Any(i => i.Code == "class-lowered" && i.Level == ValidationIssue.Warning));
        Assert.True(issues.Any(i => i.Code == "area-missing" && i.ObjectType == "mnemo"));
        // повышение класса — не предупреждение
        var up = ConfigValidator.Validate(prev, next, new Dictionary<int, JsonElement>());
        Assert.False(up.Any(i => i.Code == "class-lowered"));
    }

    [Fact]
    public void Old_snapshot_without_area_fields_reads_as_root()
    {
        // версия, опубликованная до появления областей
        const string json = """{"devices":[{"id":1,"name":"D","protocol":"modbusTcp"}],"tags":[{"id":1,"name":"T","deviceId":1,"address":"hr:0"}],"alarms":[],"mnemos":[{"id":1,"name":"M","parentId":null,"sortOrder":0,"version":1}]}""";
        var s = ConfigSnapshot.Deserialize(json);
        Assert.Equal(1, s.Devices[0].AreaId);
        Assert.Null(s.Tags[0].AreaId);
        Assert.Equal(CommandClass.Normal, s.Tags[0].CommandClass);
        Assert.Equal(1, s.Mnemos[0].AreaId);
        Assert.Equal(1, s.TagArea(s.Tags[0]));
    }
}
