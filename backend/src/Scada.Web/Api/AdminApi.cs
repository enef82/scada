using System.Text.Json;
using Npgsql;
using Scada.Core.Data;
using Scada.Web.Infrastructure;
using Scada.Web.Realtime;

namespace Scada.Web.Api;

public sealed record UserDto(int Id, string Login, string DisplayName, string Role, bool Enabled,
    bool MustChangePassword, DateTime CreatedAt, DateTime? LastLoginAt);
public sealed record UserSaveRequest(string Login, string DisplayName, string Role, bool Enabled, string? Password);

public sealed record AuditDto(long Id, DateTime Ts, string? User, string Action, string Object, JsonElement Details, string? Ip);

public static class AdminApi
{
    public static void Map(RouteGroupBuilder api)
    {
        // ------------------------------------------------------------ пользователи
        api.MapGet("/users", async (NpgsqlDataSource ds, CancellationToken ct) =>
            await ds.QueryAsync(
                "SELECT id, login, display_name, role, enabled, must_change_password, created_at, last_login_at " +
                "FROM users ORDER BY login",
                r => new UserDto(r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetBoolean(4),
                    r.GetBoolean(5), r.GetUtc(6), r.GetNullableDateTime(7)), ct))
            .RequireAuthorization(Roles.CanAdmin);

        api.MapPost("/users", (UserSaveRequest u, HttpContext http, NpgsqlDataSource ds, Passwords pw, Audit audit,
            CancellationToken ct) => ConfigApi.Guard(async () =>
            {
                if (!Roles.All.Contains(u.Role)) return Results.Problem("Неизвестная роль", statusCode: 400);
                if (Passwords.Validate(u.Password ?? "") is { } err) return Results.Problem(err, statusCode: 400);
                var id = await ds.ScalarAsync(
                    "INSERT INTO users (login, display_name, password_hash, role, enabled, must_change_password) " +
                    "VALUES ($1,$2,$3,$4,$5,true) RETURNING id", ct,
                    u.Login.Trim(), u.DisplayName, pw.Hash(u.Password!), u.Role, u.Enabled);
                await audit.WriteAsync(http, "user_change", $"user:{u.Login}", new { op = "create", u.Role, u.Enabled });
                return Results.Ok(new { id });
            })).RequireAuthorization(Roles.CanAdmin);

        api.MapPut("/users/{id:int}", (int id, UserSaveRequest u, HttpContext http, NpgsqlDataSource ds, Passwords pw,
            Audit audit, CancellationToken ct) => ConfigApi.Guard(async () =>
            {
                if (!Roles.All.Contains(u.Role)) return Results.Problem("Неизвестная роль", statusCode: 400);
                var me = http.User.Login();
                var login = (string?)await ds.ScalarAsync("SELECT login FROM users WHERE id=$1", ct, id);
                if (login is null) return Results.NotFound();
                if (login == me && (u.Role != Roles.Admin || !u.Enabled))
                    return Results.Problem("Нельзя лишить прав администратора самого себя", statusCode: 400);

                await ds.ExecAsync("UPDATE users SET display_name=$2, role=$3, enabled=$4 WHERE id=$1",
                    ct, id, u.DisplayName, u.Role, u.Enabled);
                if (!string.IsNullOrEmpty(u.Password))
                {
                    if (Passwords.Validate(u.Password) is { } err) return Results.Problem(err, statusCode: 400);
                    await ds.ExecAsync("UPDATE users SET password_hash=$2, must_change_password=true WHERE id=$1",
                        ct, id, pw.Hash(u.Password));
                }
                await audit.WriteAsync(http, "user_change", $"user:{login}",
                    new { op = "update", u.Role, u.Enabled, passwordReset = !string.IsNullOrEmpty(u.Password) });
                return Results.NoContent();
            })).RequireAuthorization(Roles.CanAdmin);

        api.MapDelete("/users/{id:int}", (int id, HttpContext http, NpgsqlDataSource ds, Audit audit,
            CancellationToken ct) => ConfigApi.Guard(async () =>
            {
                var login = (string?)await ds.ScalarAsync("SELECT login FROM users WHERE id=$1", ct, id);
                if (login is null) return Results.NotFound();
                if (login == http.User.Login()) return Results.Problem("Нельзя удалить самого себя", statusCode: 400);
                await ds.ExecAsync("DELETE FROM users WHERE id=$1", ct, id);
                await audit.WriteAsync(http, "user_change", $"user:{login}", new { op = "delete" });
                return Results.NoContent();
            })).RequireAuthorization(Roles.CanAdmin);

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
            .RequireAuthorization(Roles.CanEngineer);

        api.MapGet("/audit/verify", async (NpgsqlDataSource ds, CancellationToken ct) =>
        {
            var broken = await ds.ScalarAsync("SELECT audit_verify()", ct);
            return Results.Ok(new { intact = broken is null, firstBrokenId = broken });
        }).RequireAuthorization(Roles.CanAdmin);

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
        }).RequireAuthorization(Roles.CanView);
    }
}
