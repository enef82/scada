using System.Text.Json;
using Npgsql;
using Scada.Core.Access;
using Scada.Core.Data;
using Scada.Web.Infrastructure;
using Scada.Web.Realtime;

namespace Scada.Web.Api;

public sealed record UserDto(int Id, string Login, string DisplayName, string Source, bool Emergency, bool Enabled,
    bool MustChangePassword, DateTime CreatedAt, DateTime? LastLoginAt, string[] Roles);
public sealed record UserSaveRequest(string Login, string DisplayName, bool Enabled, string? Password, bool Emergency);

public sealed record AuditDto(long Id, DateTime Ts, string? User, string Action, string Object, JsonElement Details, string? Ip);

public static class AdminApi
{
    public static void Map(RouteGroupBuilder api)
    {
        // ------------------------------------------------------------ пользователи
        // Права выдаются назначениями (AccessApi); здесь — учётные записи: локальные, аварийные, доменные.
        var manage = Policies.Of(Perm.UsersManage);
        api.MapGet("/users", async (NpgsqlDataSource ds, AccessService access, CancellationToken ct) =>
        {
            var m = await access.ModelAsync(ct);
            var roleName = m.Roles.ToDictionary(r => r.Id, r => r.Name);
            var byUser = m.Assignments.Where(a => a.PrincipalType == Assignment.User)
                .GroupBy(a => a.Principal.ToLowerInvariant())
                .ToDictionary(g => g.Key, g => g.Select(a => $"{roleName.GetValueOrDefault(a.RoleId, "?")} — {m.Tree.Path(a.AreaId)}"
                    + (a.ValidUntil is { } u ? $" (до {u:dd.MM.yyyy})" : "")).ToArray());
            return (await ds.QueryAsync(
                "SELECT id, login, display_name, source, emergency, enabled, must_change_password, created_at, last_login_at FROM users ORDER BY login",
                r => new UserDto(r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetBoolean(4), r.GetBoolean(5),
                    r.GetBoolean(6), r.GetUtc(7), r.GetNullableDateTime(8), []), ct))
                .Select(u => u with { Roles = byUser.GetValueOrDefault(u.Login.ToLowerInvariant(), []) });
        }).RequireAuthorization(manage);

        api.MapPost("/users", (UserSaveRequest u, HttpContext http, NpgsqlDataSource ds, Passwords pw, Audit audit,
            CancellationToken ct) => ConfigApi.Guard(async () =>
            {
                if (string.IsNullOrWhiteSpace(u.Login)) return Results.Problem("Укажите логин", statusCode: 400);
                if (Passwords.Validate(u.Password ?? "") is { } err) return Results.Problem(err, statusCode: 400);
                var id = await ds.ScalarAsync(
                    "INSERT INTO users (login, display_name, password_hash, enabled, emergency, must_change_password, source) " +
                    "VALUES ($1,$2,$3,$4,$5,true,'local') RETURNING id", ct,
                    u.Login.Trim(), u.DisplayName, pw.Hash(u.Password!), u.Enabled, u.Emergency);
                await audit.WriteAsync(http, "user_change", $"user:{u.Login}", new { op = "create", u.Enabled, u.Emergency });
                return Results.Ok(new { id });
            })).RequireAuthorization(manage);

        api.MapPut("/users/{id:int}", (int id, UserSaveRequest u, HttpContext http, NpgsqlDataSource ds, Passwords pw,
            AccessService access, Audit audit, CancellationToken ct) => ConfigApi.Guard(async () =>
            {
                var cur = await ds.QuerySingleAsync("SELECT login, source FROM users WHERE id=$1", r => (Login: r.GetString(0), Source: r.GetString(1)), ct, id);
                if (cur.Login is null) return Results.NotFound();
                if (cur.Login == http.User.Login() && !u.Enabled) return Results.Problem("Нельзя отключить самого себя", statusCode: 400);
                if (cur.Source == "ad" && (!string.IsNullOrEmpty(u.Password) || u.Emergency))
                    return Results.Problem("Пароль доменной учётной записи задаётся в AD; аварийной может быть только локальная запись", statusCode: 400);

                await ds.ExecAsync("UPDATE users SET display_name=$2, enabled=$3, emergency=$4 WHERE id=$1", ct, id, u.DisplayName, u.Enabled, u.Emergency);
                if (!string.IsNullOrEmpty(u.Password))
                {
                    if (Passwords.Validate(u.Password) is { } err) return Results.Problem(err, statusCode: 400);
                    await ds.ExecAsync("UPDATE users SET password_hash=$2, must_change_password=true WHERE id=$1", ct, id, pw.Hash(u.Password));
                }
                access.Invalidate();
                await audit.WriteAsync(http, "user_change", $"user:{cur.Login}",
                    new { op = "update", u.Enabled, u.Emergency, passwordReset = !string.IsNullOrEmpty(u.Password) });
                return Results.NoContent();
            })).RequireAuthorization(manage);

        api.MapDelete("/users/{id:int}", (int id, HttpContext http, NpgsqlDataSource ds, AccessService access, Audit audit,
            CancellationToken ct) => ConfigApi.Guard(async () =>
            {
                var login = (string?)await ds.ScalarAsync("SELECT login FROM users WHERE id=$1", ct, id);
                if (login is null) return Results.NotFound();
                if (login == http.User.Login()) return Results.Problem("Нельзя удалить самого себя", statusCode: 400);
                var removed = await ds.ExecAsync("DELETE FROM role_assignments WHERE principal_type='user' AND lower(principal)=lower($1)", ct, login);
                await ds.ExecAsync("DELETE FROM users WHERE id=$1", ct, id);
                access.Invalidate();
                await audit.WriteAsync(http, "user_change", $"user:{login}", new { op = "delete", assignmentsRemoved = removed });
                return Results.NoContent();
            })).RequireAuthorization(manage);

        // ------------------------------------------------------------ аудит
        api.MapGet("/audit", async (DateTimeOffset? from, DateTimeOffset? to, string? action, int? limit,
            NpgsqlDataSource ds, CancellationToken ct) =>
            await ds.QueryAsync(
                "SELECT id, ts, user_login, action, object, details::text, client_ip FROM audit_log " +
                "WHERE ts >= $1 AND ts <= $2 AND ($3::text IS NULL OR action = $3) ORDER BY id DESC LIMIT $4",
                r => new AuditDto(r.GetInt64(0), r.GetUtc(1), r.GetNullableString(2), r.GetString(3), r.GetString(4),
                    JsonSerializer.Deserialize<JsonElement>(r.GetString(5)), r.GetNullableString(6)), ct,
                (from ?? DateTimeOffset.UtcNow.AddDays(-7)).UtcDateTime, (to ?? DateTimeOffset.UtcNow).UtcDateTime,
                string.IsNullOrWhiteSpace(action) ? null : action, Math.Clamp(limit ?? 500, 1, 5000)))
            .RequireAuthorization(Policies.Of(Perm.AuditView));

        api.MapGet("/audit/verify", async (NpgsqlDataSource ds, CancellationToken ct) =>
        {
            var broken = await ds.ScalarAsync("SELECT audit_verify()", ct);
            return Results.Ok(new { intact = broken is null, firstBrokenId = broken });
        }).RequireAuthorization(Policies.Of(Perm.AuditView));

        // ------------------------------------------------------------ диагностика
        api.MapGet("/diagnostics", async (NpgsqlDataSource ds, RealtimeService rt, CancellationToken ct) =>
        {
            var db = await ds.QuerySingleAsync(
                """
                SELECT pg_database_size(current_database()),
                       (SELECT count(*) FROM tags WHERE NOT deleted), (SELECT count(*) FROM devices WHERE NOT deleted),
                       (SELECT count(*) FROM alarm_active WHERE active),
                       (SELECT count(*) FROM pg_inherits i JOIN pg_class p ON p.oid = i.inhparent
                         WHERE p.relname = 'tag_history')
                """,
                r => new { dbBytes = r.GetInt64(0), tags = r.GetInt64(1), devices = r.GetInt64(2),
                           activeAlarms = r.GetInt64(3), historyPartitions = r.GetInt64(4) }, ct);
            return Results.Ok(new
            {
                web = new
                {
                    listenerConnected = rt.ListenerConnected,
                    cachedValues = rt.CachedValues,
                    connections = rt.Connections,
                    version = typeof(AdminApi).Assembly.GetName().Version?.ToString(),
                },
                acquisition = new
                {
                    lastHeartbeatAt = rt.LastHeartbeatAt,
                    heartbeat = rt.LastHeartbeat is null ? (JsonElement?)null : JsonSerializer.Deserialize<JsonElement>(rt.LastHeartbeat),
                },
                db,
            });
        }).RequireAuthorization(Policies.Of(Perm.View));
    }
}
