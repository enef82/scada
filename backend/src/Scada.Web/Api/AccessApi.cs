using Npgsql;
using Scada.Core.Access;
using Scada.Core.Data;
using Scada.Web.Infrastructure;

namespace Scada.Web.Api;

public sealed record AreaDto(int Id, int? ParentId, string Name, int SortOrder, string Path,
    long Devices = 0, long Tags = 0, long Mnemos = 0);
public sealed record AreaSaveRequest(int? ParentId, string Name, int SortOrder);

public sealed record RoleDto(int Id, string? Code, string Name, string Description, bool IsSystem, string[] Permissions, long Assignments);
public sealed record RoleSaveRequest(string Name, string? Description, string[] Permissions);

public sealed record AssignmentDto(int Id, string PrincipalType, string Principal, int RoleId, string RoleName, int AreaId,
    string AreaPath, DateTime? ValidUntil, bool Expired, string Comment, DateTime CreatedAt, string? CreatedBy);
public sealed record AssignmentSaveRequest(string PrincipalType, string Principal, int RoleId, int AreaId, DateTime? ValidUntil, string? Comment);

/// <summary>
/// Области, роли, назначения, сопоставление групп AD. Меняются сразу (без черновика), каждое изменение — в аудит
/// с составом прав до и после: например, уволенного сотрудника нужно отключить немедленно.
/// </summary>
public static class AccessApi
{
    public static void Map(RouteGroupBuilder api)
    {
        var manage = Policies.Of(Perm.UsersManage);

        api.MapGet("/permissions", () => Perm.All.Select(p => new { code = p, name = Perm.Names[p], rootOnly = Perm.RootOnly.Contains(p) }))
            .RequireAuthorization();

        // ------------------------------------------------------------ области
        api.MapGet("/areas", async (NpgsqlDataSource ds, AccessService access, CancellationToken ct) =>
        {
            var m = await access.ModelAsync(ct);
            var counts = await ds.QueryAsync(
                """
                SELECT a.id,
                       (SELECT count(*) FROM devices d WHERE d.area_id = a.id AND NOT d.deleted),
                       (SELECT count(*) FROM tags t JOIN devices d ON d.id = t.device_id
                         WHERE coalesce(t.area_id, d.area_id) = a.id AND NOT t.deleted),
                       (SELECT count(*) FROM mnemos x WHERE x.area_id = a.id AND NOT x.deleted)
                FROM areas a
                """, r => (Id: r.GetInt32(0), D: r.GetInt64(1), T: r.GetInt64(2), M: r.GetInt64(3)), ct);
            var byId = counts.ToDictionary(c => c.Id);
            return m.Tree.Nodes.OrderBy(n => n.ParentId is null ? 0 : 1).ThenBy(n => n.SortOrder).ThenBy(n => n.Name)
                .Select(n => new AreaDto(n.Id, n.ParentId, n.Name, n.SortOrder, m.Tree.Path(n.Id),
                    byId.GetValueOrDefault(n.Id).D, byId.GetValueOrDefault(n.Id).T, byId.GetValueOrDefault(n.Id).M));
        }).RequireAuthorization();

        api.MapPost("/areas", (AreaSaveRequest req, HttpContext http, NpgsqlDataSource ds, AccessService access, Audit audit,
            CancellationToken ct) => ConfigApi.Guard(async () =>
            {
                if (string.IsNullOrWhiteSpace(req.Name)) return Results.Problem("Укажите название области", statusCode: 400);
                var m = await access.ModelAsync(ct);
                var parent = req.ParentId ?? m.Tree.RootId;
                if (!m.Tree.Contains(parent)) return Results.Problem("Родительская область не найдена", statusCode: 400);
                if (await AreaDeniedAsync(http, parent, "area:new") is { } denied) return denied;
                var id = await ds.ScalarAsync("INSERT INTO areas (parent_id, name, sort_order) VALUES ($1,$2,$3) RETURNING id",
                    ct, parent, req.Name.Trim(), req.SortOrder);
                access.Invalidate();
                await audit.WriteAsync(http, "area_change", $"area:{id}", new { op = "create", parent = m.Tree.Path(parent), name = req.Name.Trim() });
                return Results.Ok(new { id });
            })).RequireAuthorization(Policies.Of(Perm.ConfigEdit, Perm.UsersManage));

        api.MapPut("/areas/{id:int}", (int id, AreaSaveRequest req, HttpContext http, NpgsqlDataSource ds, AccessService access,
            Audit audit, CancellationToken ct) => ConfigApi.Guard(async () =>
            {
                var m = await access.ModelAsync(ct);
                var node = m.Tree.Get(id);
                if (node is null) return Results.NotFound();
                if (string.IsNullOrWhiteSpace(req.Name)) return Results.Problem("Укажите название области", statusCode: 400);
                if (await AreaDeniedAsync(http, id, $"area:{id}") is { } denied) return denied;
                int? parent = node.ParentId is null ? null : req.ParentId ?? node.ParentId;
                if (parent is { } p)
                {
                    if (!m.Tree.Contains(p)) return Results.Problem("Родительская область не найдена", statusCode: 400);
                    if (m.Tree.IsWithin(p, id)) return Results.Problem("Нельзя перенести область внутрь самой себя", statusCode: 400);
                    if (p != node.ParentId && await AreaDeniedAsync(http, p, $"area:{id}") is { } denied2) return denied2;
                }
                var before = m.Tree.Path(id);
                await ds.ExecAsync("UPDATE areas SET parent_id=$2, name=$3, sort_order=$4 WHERE id=$1", ct, id, parent, req.Name.Trim(), req.SortOrder);
                access.Invalidate();
                var after = (await access.ModelAsync(ct)).Tree.Path(id);
                await audit.WriteAsync(http, "area_change", $"area:{id}", new { op = "update", before, after });
                return Results.NoContent();
            })).RequireAuthorization(Policies.Of(Perm.ConfigEdit, Perm.UsersManage));

        api.MapDelete("/areas/{id:int}", (int id, HttpContext http, NpgsqlDataSource ds, AccessService access, Audit audit,
            CancellationToken ct) => ConfigApi.Guard(async () =>
            {
                var m = await access.ModelAsync(ct);
                var node = m.Tree.Get(id);
                if (node is null) return Results.NotFound();
                if (node.ParentId is null) return Results.Problem("Корневую область удалить нельзя", statusCode: 400);
                if (await AreaDeniedAsync(http, id, $"area:{id}") is { } denied) return denied;
                var busy = await ds.QuerySingleAsync(
                    """
                    SELECT (SELECT count(*) FROM areas WHERE parent_id = $1),
                           (SELECT count(*) FROM devices WHERE area_id = $1 AND NOT deleted)
                         + (SELECT count(*) FROM tags WHERE area_id = $1 AND NOT deleted)
                         + (SELECT count(*) FROM mnemos WHERE area_id = $1 AND NOT deleted),
                           (SELECT count(*) FROM role_assignments WHERE area_id = $1),
                           (SELECT count(*) FROM config_versions WHERE version = (SELECT max(version) FROM config_versions)
                              AND (snapshot::text LIKE '%"areaId":' || $1 || ',%' OR snapshot::text LIKE '%"areaId":' || $1 || '}%'))
                    """, r => (Children: r.GetInt64(0), Objects: r.GetInt64(1), Assignments: r.GetInt64(2), Published: r.GetInt64(3)), ct, id);
                if (busy.Children > 0) return Results.Problem("В области есть вложенные области", statusCode: 409);
                if (busy.Objects > 0) return Results.Problem($"В области есть объекты черновика ({busy.Objects}) — перенесите их", statusCode: 409);
                if (busy.Assignments > 0) return Results.Problem($"На область выданы назначения ({busy.Assignments}) — удалите их", statusCode: 409);
                if (busy.Published > 0) return Results.Problem("Область используется в работающей версии конфигурации — сначала опубликуйте перенос объектов", statusCode: 409);
                var path = m.Tree.Path(id);
                await ds.ExecAsync("DELETE FROM areas WHERE id=$1", ct, id);
                access.Invalidate();
                await audit.WriteAsync(http, "area_change", $"area:{id}", new { op = "delete", path });
                return Results.NoContent();
            })).RequireAuthorization(Policies.Of(Perm.ConfigEdit, Perm.UsersManage));

        // ------------------------------------------------------------ роли
        api.MapGet("/roles", async (NpgsqlDataSource ds, CancellationToken ct) =>
            await ds.QueryAsync(
                "SELECT r.id, r.code, r.name, r.description, r.is_system, r.permissions, " +
                "(SELECT count(*) FROM role_assignments a WHERE a.role_id = r.id) FROM roles r ORDER BY r.is_system DESC, r.id",
                r => new RoleDto(r.GetInt32(0), r.GetNullableString(1), r.GetString(2), r.GetString(3), r.GetBoolean(4),
                    (string[])r.GetValue(5), r.GetInt64(6)), ct))
            .RequireAuthorization(manage);

        api.MapPost("/roles", (RoleSaveRequest req, HttpContext http, NpgsqlDataSource ds, AccessService access, Audit audit,
            CancellationToken ct) => ConfigApi.Guard(async () =>
            {
                if (ValidateRole(req) is { } bad) return bad;
                var perms = Normalize(req.Permissions);
                var id = await ds.ScalarAsync("INSERT INTO roles (name, description, permissions) VALUES ($1,$2,$3) RETURNING id",
                    ct, req.Name.Trim(), req.Description ?? "", perms);
                access.Invalidate();
                await audit.WriteAsync(http, "role_change", $"role:{id}", new { op = "create", name = req.Name.Trim(), after = perms });
                return Results.Ok(new { id });
            })).RequireAuthorization(manage);

        api.MapPut("/roles/{id:int}", (int id, RoleSaveRequest req, HttpContext http, NpgsqlDataSource ds, AccessService access,
            Audit audit, CancellationToken ct) => ConfigApi.Guard(async () =>
            {
                var old = await ds.QuerySingleAsync("SELECT is_system, permissions, name FROM roles WHERE id=$1",
                    r => (System: r.GetBoolean(0), Perms: (string[])r.GetValue(1), Name: r.GetString(2)), ct, id);
                if (old.Perms is null) return Results.NotFound();
                if (old.System) return Results.Problem("Системную роль изменить нельзя — скопируйте её в свою и измените копию", statusCode: 400);
                if (ValidateRole(req) is { } bad) return bad;
                var perms = Normalize(req.Permissions);
                if (await SelfLockoutAsync(http, access, roleId: id, newPerms: perms, ct: ct) is { } locked) return locked;
                await ds.ExecAsync("UPDATE roles SET name=$2, description=$3, permissions=$4 WHERE id=$1", ct, id, req.Name.Trim(), req.Description ?? "", perms);
                access.Invalidate();
                await audit.WriteAsync(http, "role_change", $"role:{id}", new { op = "update", name = req.Name.Trim(), before = old.Perms, after = perms });
                return Results.NoContent();
            })).RequireAuthorization(manage);

        api.MapDelete("/roles/{id:int}", (int id, HttpContext http, NpgsqlDataSource ds, AccessService access, Audit audit,
            CancellationToken ct) => ConfigApi.Guard(async () =>
            {
                var old = await ds.QuerySingleAsync("SELECT is_system, name, permissions FROM roles WHERE id=$1",
                    r => (System: r.GetBoolean(0), Name: r.GetString(1), Perms: (string[])r.GetValue(2)), ct, id);
                if (old.Name is null) return Results.NotFound();
                if (old.System) return Results.Problem("Системную роль удалить нельзя", statusCode: 400);
                var used = Convert.ToInt64(await ds.ScalarAsync("SELECT count(*) FROM role_assignments WHERE role_id=$1", ct, id));
                if (used > 0) return Results.Problem($"Роль выдана в назначениях: {used}. Сначала удалите их.", statusCode: 409);
                await ds.ExecAsync("DELETE FROM roles WHERE id=$1", ct, id);
                access.Invalidate();
                await audit.WriteAsync(http, "role_change", $"role:{id}", new { op = "delete", name = old.Name, before = old.Perms });
                return Results.NoContent();
            })).RequireAuthorization(manage);

        // ------------------------------------------------------------ назначения и группы AD
        MapAssignments(api.MapGroup("/assignments"), null);
        MapAssignments(api.MapGroup("/ad-groups"), Assignment.Group);

        api.MapGet("/directory/status", (IDirectory dir) => dir.Health).RequireAuthorization(manage);
        api.MapPost("/directory/test", async (DirectoryTestRequest req, IDirectory dir, CancellationToken ct) =>
        {
            if (!dir.Enabled) return Results.Problem("Интеграция с AD выключена (Directory:Enabled)", statusCode: 400);
            var r = await dir.LookupAsync(req.Login.Trim(), ct);
            return Results.Ok(new { status = r.Status.ToString(), user = r.User, error = r.Error });
        }).RequireAuthorization(manage);
    }

    public sealed record DirectoryTestRequest(string Login);

    private static void MapAssignments(RouteGroupBuilder g, string? fixedType)
    {
        var manage = Policies.Of(Perm.UsersManage);

        g.MapGet("", async (string? principalType, string? principal, NpgsqlDataSource ds, AccessService access, CancellationToken ct) =>
        {
            var m = await access.ModelAsync(ct);
            var type = fixedType ?? principalType;
            return (await ds.QueryAsync(
                "SELECT a.id, a.principal_type, a.principal, a.role_id, r.name, a.area_id, a.valid_until, a.comment, a.created_at, a.created_by " +
                "FROM role_assignments a JOIN roles r ON r.id = a.role_id " +
                "WHERE ($1::text IS NULL OR a.principal_type = $1) AND ($2::text IS NULL OR lower(a.principal) = lower($2)) " +
                "ORDER BY a.principal_type, lower(a.principal), r.id",
                r => new AssignmentDto(r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetInt32(3), r.GetString(4), r.GetInt32(5),
                    "", r.GetNullableDateTime(6), r.GetNullableDateTime(6) is { } u && u <= DateTime.UtcNow, r.GetString(7), r.GetUtc(8),
                    r.GetNullableString(9)), ct, type, principal))
                .Select(a => a with { AreaPath = m.Tree.Path(a.AreaId) });
        }).RequireAuthorization(manage);

        g.MapPost("", (AssignmentSaveRequest req, HttpContext http, NpgsqlDataSource ds, AccessService access, IDirectory dir,
            Audit audit, CancellationToken ct) => ConfigApi.Guard(async () =>
            {
                var r = req with { PrincipalType = fixedType ?? req.PrincipalType };
                if (await ValidateAssignmentAsync(r, ds, access, dir, ct) is { } bad) return bad;
                var id = await ds.ScalarAsync(
                    "INSERT INTO role_assignments (principal_type, principal, role_id, area_id, valid_until, comment, created_by) " +
                    "VALUES ($1,$2,$3,$4,$5,$6,$7) RETURNING id", ct,
                    r.PrincipalType, r.Principal.Trim(), r.RoleId, r.AreaId, r.ValidUntil?.ToUniversalTime(), r.Comment ?? "", http.User.Login());
                access.Invalidate();
                await audit.WriteAsync(http, "assignment_change", $"assignment:{id}", await DescribeAsync(ds, access, r, "create", null, ct));
                return Results.Ok(new { id });
            })).RequireAuthorization(manage);

        g.MapPut("/{id:int}", (int id, AssignmentSaveRequest req, HttpContext http, NpgsqlDataSource ds, AccessService access,
            IDirectory dir, Audit audit, CancellationToken ct) => ConfigApi.Guard(async () =>
            {
                var old = await LoadAssignmentAsync(ds, id, ct);
                if (old is null || (fixedType is not null && old.PrincipalType != fixedType)) return Results.NotFound();
                var r = req with { PrincipalType = fixedType ?? req.PrincipalType };
                if (await ValidateAssignmentAsync(r, ds, access, dir, ct) is { } bad) return bad;
                if (await SelfLockoutAsync(http, access, assignmentId: id, replacement: r, ct: ct) is { } locked) return locked;
                await ds.ExecAsync(
                    "UPDATE role_assignments SET principal_type=$2, principal=$3, role_id=$4, area_id=$5, valid_until=$6, comment=$7 WHERE id=$1",
                    ct, id, r.PrincipalType, r.Principal.Trim(), r.RoleId, r.AreaId, r.ValidUntil?.ToUniversalTime(), r.Comment ?? "");
                access.Invalidate();
                await audit.WriteAsync(http, "assignment_change", $"assignment:{id}", await DescribeAsync(ds, access, r, "update", old, ct));
                return Results.NoContent();
            })).RequireAuthorization(manage);

        g.MapDelete("/{id:int}", (int id, HttpContext http, NpgsqlDataSource ds, AccessService access, Audit audit,
            CancellationToken ct) => ConfigApi.Guard(async () =>
            {
                var old = await LoadAssignmentAsync(ds, id, ct);
                if (old is null || (fixedType is not null && old.PrincipalType != fixedType)) return Results.NotFound();
                if (await SelfLockoutAsync(http, access, assignmentId: id, ct: ct) is { } locked) return locked;
                await ds.ExecAsync("DELETE FROM role_assignments WHERE id=$1", ct, id);
                access.Invalidate();
                await audit.WriteAsync(http, "assignment_change", $"assignment:{id}", await DescribeAsync(ds, access, null, "delete", old, ct));
                return Results.NoContent();
            })).RequireAuthorization(manage);
    }

    // ------------------------------------------------------------ помощники
    private static async Task<IResult?> AreaDeniedAsync(HttpContext http, int areaId, string what)
    {
        var p = await http.PermsAsync();
        // структуру дерева меняет инженер области или администратор безопасности
        return p.Has(Perm.UsersManage, areaId) ? null : await http.DenyUnlessAsync(Perm.ConfigEdit, areaId, what);
    }

    private static IResult? ValidateRole(RoleSaveRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Name)) return Results.Problem("Укажите название роли", statusCode: 400);
        var unknown = (req.Permissions ?? []).Where(p => !Perm.IsKnown(p)).ToList();
        return unknown.Count > 0 ? Results.Problem($"Неизвестные права: {string.Join(", ", unknown)}", statusCode: 400) : null;
    }

    private static string[] Normalize(string[]? perms) => Perm.All.Where(p => (perms ?? []).Contains(p)).ToArray();

    private static async Task<IResult?> ValidateAssignmentAsync(AssignmentSaveRequest r, NpgsqlDataSource ds, AccessService access,
        IDirectory dir, CancellationToken ct)
    {
        if (r.PrincipalType is not (Assignment.User or Assignment.Group)) return Results.Problem("Укажите, кому выдаётся роль: пользователю или группе AD", statusCode: 400);
        if (string.IsNullOrWhiteSpace(r.Principal)) return Results.Problem(r.PrincipalType == Assignment.User ? "Укажите логин" : "Укажите имя группы AD", statusCode: 400);
        var m = await access.ModelAsync(ct);
        if (!m.Tree.Contains(r.AreaId)) return Results.Problem("Область не найдена", statusCode: 400);
        var role = m.Roles.FirstOrDefault(x => x.Id == r.RoleId);
        if (role is null) return Results.Problem("Роль не найдена", statusCode: 400);
        var rootOnly = role.Permissions.Where(Perm.RootOnly.Contains).ToList();
        if (rootOnly.Count > 0 && r.AreaId != m.Tree.RootId)
            return Results.Problem($"Права «{string.Join("», «", rootOnly.Select(p => Perm.Names[p]))}» действуют только на корневой области «{m.Tree.Path(m.Tree.RootId)}»", statusCode: 400);
        if (r.ValidUntil is { } until && until.ToUniversalTime() <= DateTime.UtcNow) return Results.Problem("Срок действия уже прошёл", statusCode: 400);
        if (r.PrincipalType == Assignment.User)
        {
            var exists = await ds.ScalarAsync("SELECT 1 FROM users WHERE lower(login) = lower($1)", ct, r.Principal.Trim());
            // доменному пользователю можно выдать роль до первого входа
            if (exists is null && !dir.Enabled) return Results.Problem($"Пользователь «{r.Principal}» не найден", statusCode: 400);
        }
        else if (!dir.Enabled) return Results.Problem("Назначения группам AD работают только при включённой интеграции с AD (Directory:Enabled)", statusCode: 400);
        return null;
    }

    private static Task<Assignment?> LoadAssignmentAsync(NpgsqlDataSource ds, int id, CancellationToken ct) =>
        ds.QuerySingleAsync<Assignment?>(
            "SELECT id, principal_type, principal, role_id, area_id, valid_until, comment FROM role_assignments WHERE id = $1",
            r => new Assignment(r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetInt32(3), r.GetInt32(4), r.GetNullableDateTime(5), r.GetString(6)),
            ct, id);

    private static async Task<object> DescribeAsync(NpgsqlDataSource ds, AccessService access, AssignmentSaveRequest? after, string op,
        Assignment? before, CancellationToken ct)
    {
        var m = await access.ModelAsync(ct);
        object? Side(string? type, string? who, int roleId, int areaId, DateTime? until)
        {
            var role = m.Roles.FirstOrDefault(r => r.Id == roleId);
            return type is null ? null : new { principalType = type, principal = who, role = role?.Name, permissions = role?.Permissions, area = m.Tree.Path(areaId), validUntil = until };
        }
        await Task.CompletedTask;
        return new
        {
            op,
            before = before is null ? null : Side(before.PrincipalType, before.Principal, before.RoleId, before.AreaId, before.ValidUntil),
            after = after is null ? null : Side(after.PrincipalType, after.Principal.Trim(), after.RoleId, after.AreaId, after.ValidUntil),
        };
    }

    /// <summary>Администратор не может лишить себя права «Пользователи» (иначе установка останется без управления доступом).</summary>
    private static async Task<IResult?> SelfLockoutAsync(HttpContext http, AccessService access, int? assignmentId = null,
        AssignmentSaveRequest? replacement = null, int? roleId = null, string[]? newPerms = null, CancellationToken ct = default)
    {
        var m = await access.ModelAsync(ct);
        var roles = m.Roles.Select(r => r.Id == roleId && newPerms is not null ? r with { Permissions = newPerms } : r).ToList();
        var list = m.Assignments.Where(a => a.Id != assignmentId).ToList();
        if (replacement is not null && assignmentId is { } aid)
            list.Add(new Assignment(aid, replacement.PrincipalType, replacement.Principal.Trim(), replacement.RoleId, replacement.AreaId, replacement.ValidUntil?.ToUniversalTime()));
        var after = AccessCalculator.Compute(m.Tree, roles, list, http.User.Login(), http.User.Groups(), DateTime.UtcNow);
        return after.HasEverywhere(Perm.UsersManage)
            ? null
            : Results.Problem("Нельзя лишить права «Пользователи» самого себя — попросите другого администратора безопасности", statusCode: 400);
    }
}
