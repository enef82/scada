using System.Collections.Concurrent;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Npgsql;
using Scada.Core.Access;
using Scada.Core.Config;
using Scada.Core.Data;
using Scada.Core.Model;

namespace Scada.Web.Infrastructure;

/// <summary>Утверждения (claims) сеанса.</summary>
public static class ScadaClaims
{
    public const string DisplayName = "display_name";
    public const string Source = "scada:src";          // local | ad
    public const string Group = "scada:grp";           // группы AD (cn), по одной на утверждение
    public const string Session = "scada:sid";         // идентификатор сеанса — второй подтверждающий должен быть с другого
    public const string CheckedAt = "scada:chk";       // когда последний раз проверялись группы / учётная запись
    public const string LastDirectoryOk = "scada:ok";  // последний успешный ответ AD (работа без AD до 8 часов)
    public const string Emergency = "scada:emerg";     // аварийная локальная учётная запись

    public static string[] Groups(this ClaimsPrincipal u) => u.FindAll(Group).Select(c => c.Value).ToArray();
    public static string SessionId(this ClaimsPrincipal u) => u.FindFirst(Session)?.Value ?? "";
    public static bool IsDomain(this ClaimsPrincipal u) => u.FindFirst(Source)?.Value == "ad";
}

/// <summary>
/// Политики по кодам прав: RequireAuthorization(Policies.Of(Perm.ConfigEdit)) — право хотя бы в одной области.
/// Право на конкретный объект (по его области) проверяется в обработчике: <see cref="AccessExt.DenyUnlessAsync"/>.
/// </summary>
public static class Policies
{
    public const string Prefix = "perm:";
    /// <summary>Любое из перечисленных прав.</summary>
    public static string Of(params string[] perms) => Prefix + string.Join('|', perms);
}

public sealed record PermissionRequirement(string[] AnyOf) : IAuthorizationRequirement;

public sealed class PermissionPolicyProvider(IOptions<AuthorizationOptions> options) : DefaultAuthorizationPolicyProvider(options)
{
    public override async Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        if (!policyName.StartsWith(Policies.Prefix, StringComparison.Ordinal)) return await base.GetPolicyAsync(policyName);
        var codes = policyName[Policies.Prefix.Length..].Split('|', StringSplitOptions.RemoveEmptyEntries);
        return new AuthorizationPolicyBuilder().RequireAuthenticatedUser().AddRequirements(new PermissionRequirement(codes)).Build();
    }
}

public sealed class PermissionHandler(AccessService access) : AuthorizationHandler<PermissionRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement req)
    {
        if (context.User.Identity?.IsAuthenticated != true) return;
        var perms = await access.ForAsync(context.User, CancellationToken.None);
        if (req.AnyOf.Any(perms.HasAnywhere)) context.Succeed(req);
    }
}

/// <summary>Опубликованная конфигурация, по которой проверяются права рантайма (команды, тревоги, значения).</summary>
public sealed class RuntimeIndex
{
    public int Version { get; }
    public ConfigSnapshot Snapshot { get; }
    public IReadOnlyDictionary<int, TagDefinition> Tags { get; }
    private readonly Dictionary<int, int> _tagArea;
    private readonly Dictionary<int, int> _alarmArea;
    public IReadOnlyDictionary<int, MnemoRef> Mnemos { get; }

    public RuntimeIndex(int version, ConfigSnapshot s)
    {
        Version = version;
        Snapshot = s;
        Tags = s.Tags.ToDictionary(t => t.Id);
        _tagArea = s.Tags.ToDictionary(t => t.Id, s.TagArea);
        _alarmArea = s.Alarms.ToDictionary(a => a.Id, a => _tagArea.GetValueOrDefault(a.TagId, 1));
        Mnemos = s.Mnemos.ToDictionary(m => m.Id);
    }

    /// <summary>0 — тег не опубликован (доступ к нему проверяется как к корню).</summary>
    public int TagArea(int tagId) => _tagArea.GetValueOrDefault(tagId);
    public int AlarmArea(int alarmId) => _alarmArea.GetValueOrDefault(alarmId);

    /// <summary>Отфильтровать идентификаторы тегов по праву пользователя.</summary>
    public int[] Allowed(UserPermissions p, IEnumerable<int> tagIds, string perm = Perm.View) =>
        tagIds.Where(id => p.Has(perm, TagArea(id))).ToArray();
}

/// <summary>
/// Модель доступа: дерево областей, роли, назначения. Кэшируется и сбрасывается при изменениях
/// (назначения действуют сразу — пользователи и роли не идут через черновик).
/// </summary>
public sealed class AccessService(NpgsqlDataSource ds, ILogger<AccessService> log)
{
    public sealed record Model(AreaTree Tree, IReadOnlyList<RoleDef> Roles, IReadOnlyList<Assignment> Assignments, DateTime LoadedAt);

    private static readonly TimeSpan ModelTtl = TimeSpan.FromSeconds(30);   // сроки назначений истекают без событий
    private volatile Model? _model;
    private volatile RuntimeIndex? _runtime;
    private readonly ConcurrentDictionary<string, UserPermissions> _users = new();
    private readonly SemaphoreSlim _lock = new(1, 1);

    public void Invalidate()
    {
        _model = null;
        _users.Clear();
    }

    public void InvalidateRuntime() => _runtime = null;

    public async Task<Model> ModelAsync(CancellationToken ct)
    {
        var m = _model;
        if (m is not null && DateTime.UtcNow - m.LoadedAt < ModelTtl) return m;
        await _lock.WaitAsync(ct);
        try
        {
            m = _model;
            if (m is not null && DateTime.UtcNow - m.LoadedAt < ModelTtl) return m;
            var areas = await ds.QueryAsync("SELECT id, parent_id, name, sort_order FROM areas ORDER BY sort_order, name",
                r => new AreaNode(r.GetInt32(0), r.GetNullableInt(1), r.GetString(2), r.GetInt32(3)), ct);
            var roles = await ds.QueryAsync("SELECT id, code, name, is_system, permissions, description FROM roles ORDER BY id",
                r => new RoleDef(r.GetInt32(0), r.GetNullableString(1), r.GetString(2), r.GetBoolean(3), (string[])r.GetValue(4), r.GetString(5)), ct);
            var assignments = await ds.QueryAsync(
                "SELECT id, principal_type, principal, role_id, area_id, valid_until, comment FROM role_assignments",
                r => new Assignment(r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetInt32(3), r.GetInt32(4),
                    r.GetNullableDateTime(5), r.GetString(6)), ct);
            m = new Model(new AreaTree(areas), roles, assignments, DateTime.UtcNow);
            _model = m;
            _users.Clear();
            return m;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<UserPermissions> ForAsync(ClaimsPrincipal user, CancellationToken ct)
    {
        if (user.Identity?.IsAuthenticated != true) return UserPermissions.None;
        return await ForAsync(user.Login(), user.Groups(), ct);
    }

    public async Task<UserPermissions> ForAsync(string login, IReadOnlyCollection<string> groups, CancellationToken ct)
    {
        var m = await ModelAsync(ct);
        var key = login.ToLowerInvariant() + "|" + string.Join(',', groups.Select(g => g.ToLowerInvariant()).Order());
        return _users.GetOrAdd(key, _ => AccessCalculator.Compute(m.Tree, m.Roles, m.Assignments, login, groups, DateTime.UtcNow));
    }

    public async Task<RuntimeIndex> RuntimeAsync(CancellationToken ct)
    {
        if (_runtime is { } r) return r;
        await using var conn = await ds.OpenConnectionAsync(ct);
        var p = await DraftStore.LoadPublishedAsync(conn, null, ct);
        r = p is { } v ? new RuntimeIndex(v.Version, v.Snapshot) : new RuntimeIndex(0, ConfigSnapshot.Empty);
        _runtime = r;
        log.LogDebug("Права рантайма: загружена версия конфигурации {Version}", r.Version);
        return r;
    }

    // ---------------------------------------------------------------- области объектов черновика
    public async Task<int> DraftDeviceAreaAsync(int deviceId, CancellationToken ct) =>
        Convert.ToInt32(await ds.ScalarAsync("SELECT area_id FROM devices WHERE id = $1", ct, deviceId) ?? 0);

    public async Task<int> DraftTagAreaAsync(int tagId, CancellationToken ct) =>
        Convert.ToInt32(await ds.ScalarAsync(
            "SELECT coalesce(t.area_id, d.area_id) FROM tags t JOIN devices d ON d.id = t.device_id WHERE t.id = $1", ct, tagId) ?? 0);

    public async Task<int> DraftAlarmAreaAsync(int alarmId, CancellationToken ct) =>
        Convert.ToInt32(await ds.ScalarAsync(
            "SELECT coalesce(t.area_id, d.area_id) FROM alarm_defs a JOIN tags t ON t.id = a.tag_id " +
            "JOIN devices d ON d.id = t.device_id WHERE a.id = $1", ct, alarmId) ?? 0);

    public async Task<int> DraftMnemoAreaAsync(int mnemoId, CancellationToken ct) =>
        Convert.ToInt32(await ds.ScalarAsync("SELECT area_id FROM mnemos WHERE id = $1", ct, mnemoId) ?? 0);
}

public static class AccessExt
{
    /// <summary>
    /// Проверка права на область объекта. Отказ пишется в аудит («какое право и на какую область потребовалось»).
    /// Возвращает null, если право есть.
    /// </summary>
    public static async Task<IResult?> DenyUnlessAsync(this HttpContext http, string perm, int areaId, string what)
    {
        var access = http.RequestServices.GetRequiredService<AccessService>();
        var p = await access.ForAsync(http.User, http.RequestAborted);
        if (p.Has(perm, areaId)) return null;
        var m = await access.ModelAsync(http.RequestAborted);
        var areaPath = m.Tree.Path(areaId <= 0 ? m.Tree.RootId : areaId);
        await http.RequestServices.GetRequiredService<Audit>().WriteAsync(http, "access_denied", what,
            new { perm, area = areaId, areaPath });
        return Results.Problem($"Нет права «{Perm.Names.GetValueOrDefault(perm, perm)}» на область «{areaPath}»", statusCode: 403);
    }

    public static async Task<UserPermissions> PermsAsync(this HttpContext http) =>
        await http.RequestServices.GetRequiredService<AccessService>().ForAsync(http.User, http.RequestAborted);
}
