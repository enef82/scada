using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Npgsql;
using Scada.Core.Data;
using Scada.Web.Infrastructure;

namespace Scada.Web.Api;

public sealed record LoginRequest(string Login, string Password);
public sealed record ChangePasswordRequest(string OldPassword, string NewPassword);
public sealed record MeResponse(string Login, string DisplayName, string Role, bool MustChangePassword);

public static class AuthApi
{
    public static void Map(RouteGroupBuilder api)
    {
        var g = api.MapGroup("/auth");

        g.MapPost("/login", async (LoginRequest req, HttpContext http, NpgsqlDataSource ds, Passwords pw,
            LoginThrottle throttle, Audit audit, CancellationToken ct) =>
        {
            var login = (req.Login ?? "").Trim();
            if (throttle.IsLocked(login, out var left))
                return Results.Problem($"Слишком много попыток. Повторите через {Math.Ceiling(left.TotalMinutes)} мин.",
                    statusCode: 429);

            var user = await ds.QuerySingleAsync(
                "SELECT login, display_name, password_hash, role, enabled, must_change_password FROM users WHERE login = $1",
                r => (Login: r.GetString(0), Name: r.GetString(1), Hash: r.GetString(2), Role: r.GetString(3),
                      Enabled: r.GetBoolean(4), MustChange: r.GetBoolean(5)), ct, login);

            if (user.Login is null || !user.Enabled || !pw.Verify(user.Hash, req.Password ?? ""))
            {
                throttle.Fail(login);
                await audit.WriteAsync(login, "login_failed", "user:" + login, null, http.Ip());
                return Results.Problem("Неверный логин или пароль", statusCode: 401);
            }

            throttle.Success(login);
            var claims = new List<Claim>
            {
                new(ClaimTypes.Name, user.Login),
                new(ClaimTypes.Role, user.Role),
                new("display_name", user.Name),
            };
            await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
                new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)));
            await ds.ExecAsync("UPDATE users SET last_login_at = now() WHERE login = $1", ct, user.Login);
            await audit.WriteAsync(user.Login, "login", "user:" + user.Login, null, http.Ip());
            return Results.Ok(new MeResponse(user.Login, user.Name, user.Role, user.MustChange));
        }).AllowAnonymous();

        g.MapPost("/logout", async (HttpContext http, Audit audit) =>
        {
            if (http.User.Identity?.IsAuthenticated == true) await audit.WriteAsync(http, "logout", "user:" + http.User.Login());
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.NoContent();
        }).AllowAnonymous();

        g.MapGet("/me", async (HttpContext http, NpgsqlDataSource ds, CancellationToken ct) =>
        {
            var me = await ds.QuerySingleAsync(
                "SELECT login, display_name, role, must_change_password FROM users WHERE login = $1 AND enabled",
                r => new MeResponse(r.GetString(0), r.GetString(1), r.GetString(2), r.GetBoolean(3)), ct,
                http.User.Login());
            if (me is null)
            {
                await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                return Results.Unauthorized();
            }
            return Results.Ok(me);
        }).RequireAuthorization(Roles.CanView);

        g.MapPost("/change-password", async (ChangePasswordRequest req, HttpContext http, NpgsqlDataSource ds,
            Passwords pw, Audit audit, CancellationToken ct) =>
        {
            var login = http.User.Login();
            var hash = (string?)await ds.ScalarAsync("SELECT password_hash FROM users WHERE login = $1", ct, login);
            if (hash is null || !pw.Verify(hash, req.OldPassword ?? ""))
                return Results.Problem("Текущий пароль указан неверно", statusCode: 400);
            if (Passwords.Validate(req.NewPassword ?? "") is { } err) return Results.Problem(err, statusCode: 400);
            await ds.ExecAsync("UPDATE users SET password_hash = $2, must_change_password = false WHERE login = $1",
                ct, login, pw.Hash(req.NewPassword!));
            await audit.WriteAsync(http, "password_change", "user:" + login);
            return Results.NoContent();
        }).RequireAuthorization(Roles.CanView);
    }

    /// <summary>Первый запуск: если пользователей нет — создаётся admin с обязательной сменой пароля.</summary>
    public static async Task EnsureAdminAsync(NpgsqlDataSource ds, Passwords pw, string initialPassword, ILogger log)
    {
        var count = Convert.ToInt64(await ds.ScalarAsync("SELECT count(*) FROM users", CancellationToken.None));
        if (count > 0) return;
        await ds.ExecAsync(
            "INSERT INTO users (login, display_name, password_hash, role, must_change_password) VALUES ('admin','Администратор',$1,'admin',true)",
            CancellationToken.None, pw.Hash(initialPassword));
        log.LogWarning("Создан пользователь admin с начальным паролем из конфигурации. Смените пароль при первом входе.");
    }
}
