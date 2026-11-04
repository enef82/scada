using System.Text;

namespace Scada.Core.Access;

/// <summary>Коды прав. Право — одно действие в системе; роль — именованный набор прав.</summary>
public static class Perm
{
    public const string View = "view";
    public const string AlarmAck = "alarm.ack";
    public const string AlarmShelve = "alarm.shelve";
    public const string CmdControl = "cmd.control";
    public const string CmdSetpoint = "cmd.setpoint";
    public const string CmdCritical = "cmd.critical";
    public const string ConfigEdit = "config.edit";
    public const string MnemoEdit = "mnemo.edit";
    public const string ConfigPublish = "config.publish";
    public const string UsersManage = "users.manage";
    public const string AuditView = "audit.view";

    public static readonly string[] All =
    [
        View, AlarmAck, AlarmShelve, CmdControl, CmdSetpoint, CmdCritical,
        ConfigEdit, MnemoEdit, ConfigPublish, UsersManage, AuditView,
    ];

    /// <summary>Права, которые действуют только при назначении на корневую область.</summary>
    public static readonly HashSet<string> RootOnly = [UsersManage, AuditView];

    public static readonly Dictionary<string, string> Names = new()
    {
        [View] = "Просмотр",
        [AlarmAck] = "Квитирование",
        [AlarmShelve] = "Снятие с контроля",
        [CmdControl] = "Команды",
        [CmdSetpoint] = "Уставки",
        [CmdCritical] = "Критичные команды",
        [ConfigEdit] = "Конфигурация",
        [MnemoEdit] = "Схемы",
        [ConfigPublish] = "Публикация",
        [UsersManage] = "Пользователи",
        [AuditView] = "Аудит",
    };

    public static bool IsKnown(string code) => Names.ContainsKey(code);
}

public sealed record AreaNode(int Id, int? ParentId, string Name, int SortOrder = 0);

public sealed record RoleDef(int Id, string? Code, string Name, bool IsSystem, IReadOnlyList<string> Permissions, string Description = "");

public sealed record Assignment(
    int Id, string PrincipalType, string Principal, int RoleId, int AreaId, DateTime? ValidUntil, string Comment = "")
{
    public const string User = "user";
    public const string Group = "group";
}

/// <summary>Дерево областей: поиск потомков и пути к корню.</summary>
public sealed class AreaTree
{
    private readonly Dictionary<int, AreaNode> _nodes;
    private readonly Dictionary<int, List<int>> _children = new();

    public int RootId { get; }
    public IReadOnlyCollection<AreaNode> Nodes => _nodes.Values;

    public AreaTree(IEnumerable<AreaNode> nodes)
    {
        _nodes = nodes.ToDictionary(n => n.Id);
        foreach (var n in _nodes.Values)
            if (n.ParentId is { } p && _nodes.ContainsKey(p))
                (_children.TryGetValue(p, out var l) ? l : _children[p] = []).Add(n.Id);
        RootId = _nodes.Values.FirstOrDefault(n => n.ParentId is null)?.Id ?? 1;
    }

    public bool Contains(int id) => _nodes.ContainsKey(id);
    public AreaNode? Get(int id) => _nodes.GetValueOrDefault(id);

    /// <summary>Сам узел и все вложенные (защита от циклов).</summary>
    public IEnumerable<int> Subtree(int id)
    {
        if (!_nodes.ContainsKey(id)) yield break;
        var seen = new HashSet<int>();
        var stack = new Stack<int>([id]);
        while (stack.Count > 0)
        {
            var x = stack.Pop();
            if (!seen.Add(x)) continue;
            yield return x;
            if (_children.TryGetValue(x, out var ch)) foreach (var c in ch) stack.Push(c);
        }
    }

    /// <summary>Путь «Установка / Водоотведение / КНС-1».</summary>
    public string Path(int id)
    {
        var parts = new List<string>();
        var seen = new HashSet<int>();
        for (var n = Get(id); n is not null && seen.Add(n.Id); n = n.ParentId is { } p ? Get(p) : null) parts.Add(n.Name);
        parts.Reverse();
        return parts.Count == 0 ? $"#{id}" : string.Join(" / ", parts);
    }

    /// <summary>Узел лежит внутри поддерева ancestor (включая его самого).</summary>
    public bool IsWithin(int id, int ancestor)
    {
        var seen = new HashSet<int>();
        for (var n = Get(id); n is not null && seen.Add(n.Id); n = n.ParentId is { } p ? Get(p) : null)
            if (n.Id == ancestor) return true;
        return false;
    }
}

/// <summary>Откуда у пользователя право: личное назначение или группа AD — пишется в аудит.</summary>
public sealed record PermissionSource(string Perm, int AreaId, string Via, string Role);

/// <summary>Итоговые права пользователя: для каждого кода права — множество областей (с вложенными).</summary>
public sealed class UserPermissions
{
    private readonly Dictionary<string, HashSet<int>> _areas;
    private readonly List<PermissionSource> _sources;
    private readonly int _root;

    public static readonly UserPermissions None = new(new(), [], 1);

    public UserPermissions(Dictionary<string, HashSet<int>> areas, List<PermissionSource> sources, int root)
    {
        _areas = areas;
        _sources = sources;
        _root = root;
    }

    public IReadOnlyList<PermissionSource> Sources => _sources;

    public bool Has(string perm, int areaId)
    {
        if (areaId <= 0) areaId = _root;
        return _areas.TryGetValue(perm, out var set) && set.Contains(areaId);
    }

    public bool HasAnywhere(string perm) => _areas.TryGetValue(perm, out var s) && s.Count > 0;

    /// <summary>Право на всей установке (назначение на корень).</summary>
    public bool HasEverywhere(string perm) => Has(perm, _root);

    /// <summary>Области, где есть право; null — везде (для фильтров SQL «без ограничения»).</summary>
    public int[]? AreasOrAll(string perm) =>
        HasEverywhere(perm) ? null : (_areas.TryGetValue(perm, out var s) ? s.ToArray() : []);

    public IReadOnlyDictionary<string, int[]> ToDictionary() =>
        _areas.Where(kv => kv.Value.Count > 0).ToDictionary(kv => kv.Key, kv => kv.Value.Order().ToArray());

    /// <summary>Путь права для аудита: «группа SCADA-Operators-KNS → Оператор».</summary>
    public string? Via(string perm, int areaId, AreaTree tree)
    {
        var s = _sources.FirstOrDefault(x => x.Perm == perm && tree.IsWithin(areaId <= 0 ? _root : areaId, x.AreaId));
        return s is null ? null : $"{s.Via} → {s.Role}";
    }
}

public static class AccessCalculator
{
    /// <summary>
    /// Итоговые права = личные назначения + назначения групп пользователя.
    /// Назначение действует на свой узел и все вложенные; истёкшие не учитываются;
    /// users.manage и audit.view — только при назначении на корень.
    /// </summary>
    public static UserPermissions Compute(AreaTree tree, IEnumerable<RoleDef> roles, IEnumerable<Assignment> assignments,
        string login, IEnumerable<string> groups, DateTime nowUtc)
    {
        var roleById = roles.ToDictionary(r => r.Id);
        var groupSet = new HashSet<string>(groups, StringComparer.OrdinalIgnoreCase);
        var areas = new Dictionary<string, HashSet<int>>();
        var sources = new List<PermissionSource>();

        foreach (var a in assignments)
        {
            if (a.ValidUntil is { } until && until <= nowUtc) continue;
            var mine = a.PrincipalType == Assignment.User
                ? string.Equals(a.Principal, login, StringComparison.OrdinalIgnoreCase)
                : a.PrincipalType == Assignment.Group && groupSet.Contains(a.Principal);
            if (!mine || !roleById.TryGetValue(a.RoleId, out var role) || !tree.Contains(a.AreaId)) continue;

            var subtree = tree.Subtree(a.AreaId).ToList();
            var via = a.PrincipalType == Assignment.User ? "личное назначение" : $"группа {a.Principal}";
            foreach (var p in role.Permissions)
            {
                if (!Perm.IsKnown(p)) continue;
                if (Perm.RootOnly.Contains(p) && a.AreaId != tree.RootId) continue;
                if (!areas.TryGetValue(p, out var set)) areas[p] = set = [];
                set.UnionWith(subtree);
                sources.Add(new PermissionSource(p, a.AreaId, via, role.Name));
            }
        }
        return new UserPermissions(areas, sources, tree.RootId);
    }

    /// <summary>Какое право нужно для команды на теге данного типа и класса.</summary>
    public static string CommandPermission(Model.CommandClass cls, bool discrete) =>
        cls == Model.CommandClass.Critical ? Perm.CmdCritical : discrete ? Perm.CmdControl : Perm.CmdSetpoint;
}

public static class LdapFilter
{
    /// <summary>Экранирование значения фильтра LDAP (RFC 4515).</summary>
    public static string Escape(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
            sb.Append(c switch
            {
                '\\' => @"\5c", '*' => @"\2a", '(' => @"\28", ')' => @"\29", '\0' => @"\00",
                _ => c.ToString(),
            });
        return sb.ToString();
    }
}
