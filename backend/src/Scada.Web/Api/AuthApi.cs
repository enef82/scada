using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;
using Npgsql;
using Scada.Core.Access;
using Scada.Core.Data;
using Scada.Web.Infrastructure;
using Scada.Web.Realtime;

namespace Scada.Web.Api;

public sealed record LoginRequest(string Login, string Password);
public sealed record ChangePasswordRequest(string OldPassword, string NewPassword);

/// <summary>Кто вошёл и что ему можно: для каждого права — список областей (с вложенными) или "all".</summary>
public sealed record MeResponse(string Login, string DisplayName, string Source, bool Emergency, bool MustChangePassword,
    IReadOnlyDictionary<string, object> Permissions, string[] Roles, string[] Groups);

public static class AuthApi
{
    public static void Map(RouteGroupBuilder api)
    {
        var g = api.MapGroup("/auth");

        g.MapPost("/login", async (LoginRequest req, HttpContext http, NpgsqlDataSource ds, Passwords pw,
            LoginThrottle throttle, Audit audit, IDirectory directory, RealtimeService rt, CancellationToken ct) =>
        {
            var login = (req.Login ?? "").Trim();
            if (throttle.IsLocked(login, out var left))
                return Results.Problem($"Слишком много попыток. Повторите через {Math.Ceiling(left.TotalMinutes)} мин.", statusCode: 429);

            var local = await ds.QuerySingleAsync(
                "SELECT login, display_name, password_hash, enabled, must_change_password, source, emergency FROM users WHERE lower(login) = lower($1)",
                r => (Login: r.GetString(0), Name: r.GetString(1), Hash: r.GetString(2), Enabled: r.GetBoolean(3),
                      MustChange: r.GetBoolean(4), Source: r.GetString(5), Emergency: r.GetBoolean(6)), ct, login);

            async Task<IResult> Fail(string reason, string message, int status = 401)
            {
                throttle.Fail(login);
                await audit.WriteAsync(login, "login_failed", "user:" + login, new { reason }, http.Ip());
                return Results.Problem(message, statusCode: status);
            }

            // ---- локальная учётная запись (в т.ч. аварийная)
            if (local.Login is not null && local.Source == "local")
            {
                if (!local.Enabled || !pw.Verify(local.Hash, req.Password ?? "")) return await Fail("password", "Неверный логин или пароль");
                throttle.Success(login);
                await SignInAsync(http, local.Login, local.Name, "local", [], local.Emergency);
                await ds.ExecAsync("UPDATE users SET last_login_at = now() WHERE login = $1", ct, local.Login);
                if (local.Emergency)
                {
                    // вход аварийной записью — отдельная пометка в аудите и оповещение всех АРМ
                    await audit.WriteAsync(local.Login, "emergency_login", "user:" + local.Login,
                        new { note = "вход аварийной локальной учётной записью" }, http.Ip());
                    await rt.SystemNoticeAsync("warning", $"Вход аварийной локальной учётной записью «{local.Login}» с {http.Ip()}", ct);
                }
                else await audit.WriteAsync(local.Login, "login", "user:" + local.Login, null, http.Ip());
                return Results.Ok(await MeAsync(http, ds, local.Login, ct, http.User));
            }

            // ---- доменная учётная запись
            if (!directory.Enabled) return await Fail("unknown", "Неверный логин или пароль");
            if (local.Login is not null && !local.Enabled) return await Fail("disabled_in_scada", "Учётная запись отключена в SCADA");
            var res = await directory.AuthenticateAsync(login, req.Password ?? "", ct);
            switch (res.Status)
            {
                case DirectoryStatus.Unavailable:
                    await audit.WriteAsync(login, "login_failed", "user:" + login, new { reason = "ad_unavailable", res.Error }, http.Ip());
                    return Results.Problem("Контроллер домена недоступен — вход доменной учётной записью сейчас невозможен. " +
                        "Используйте аварийную локальную учётную запись.", statusCode: 503);
                case DirectoryStatus.Disabled:
                    return await Fail("disabled_in_ad", "Учётная запись отключена в домене");
                case not DirectoryStatus.Ok:
                    return await Fail("password", "Неверный логин или пароль");
            }
            var du = res.User!;
            throttle.Success(login);
            // при первом входе создаётся запись пользователя с ФИО из AD; пароль в SCADA не хранится
            await ds.ExecAsync(
                "INSERT INTO users (login, display_name, password_hash, source, ad_checked_at, last_login_at) VALUES ($1,$2,'!','ad',now(),now()) " +
                "ON CONFLICT (login) DO UPDATE SET display_name = EXCLUDED.display_name, ad_checked_at = now(), last_login_at = now()",
                ct, du.Login, du.DisplayName);
            await SignInAsync(http, du.Login, du.DisplayName, "ad", du.Groups, false);
            await audit.WriteAsync(du.Login, "login", "user:" + du.Login, new { source = "ad", groups = du.Groups }, http.Ip());
            return Results.Ok(await MeAsync(http, ds, du.Login, ct, http.User, du.Groups));
        }).AllowAnonymous();

        g.MapPost("/logout", async (HttpContext http, Audit audit) =>
        {
            if (http.User.Identity?.IsAuthenticated == true) await audit.WriteAsync(http, "logout", "user:" + http.User.Login());
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.NoContent();
        }).AllowAnonymous();

        g.MapGet("/me", async (HttpContext http, NpgsqlDataSource ds, CancellationToken ct) =>
        {
            var me = await MeAsync(http, ds, http.User.Login(), ct, http.User, http.User.Groups());
            if (me is null)
            {
                await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                return Results.Unauthorized();
            }
            return Results.Ok(me);
        }).RequireAuthorization();

        g.MapPost("/change-password", async (ChangePasswordRequest req, HttpContext http, NpgsqlDataSource ds,
            Passwords pw, Audit audit, CancellationToken ct) =>
        {
            var login = http.User.Login();
            var u = await ds.QuerySingleAsync("SELECT password_hash, source FROM users WHERE login = $1", r => (Hash: r.GetString(0), Source: r.GetString(1)), ct, login);
            if (u.Source == "ad") return Results.Problem("Пароль доменной учётной записи меняется в Windows, а не в SCADA", statusCode: 400);
            if (u.Hash is null || !pw.Verify(u.Hash, req.OldPassword ?? ""))
                return Results.Problem("Текущий пароль указан неверно", statusCode: 400);
            if (Passwords.Validate(req.NewPassword ?? "") is { } err) return Results.Problem(err, statusCode: 400);
            await ds.ExecAsync("UPDATE users SET password_hash = $2, must_change_password = false WHERE login = $1",
                ct, login, pw.Hash(req.NewPassword!));
            await audit.WriteAsync(http, "password_change", "user:" + login);
            return Results.NoContent();
        }).RequireAuthorization();
    }

    private static async Task SignInAsync(HttpContext http, string login, string name, string source, string[] groups, bool emergency)
    {
        var now = DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture);
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, login),
            new(ScadaClaims.DisplayName, name),
            new(ScadaClaims.Source, source),
            new(ScadaClaims.Session, Guid.NewGuid().ToString("N")),
            new(ScadaClaims.CheckedAt, now),
            new(ScadaClaims.LastDirectoryOk, now),
        };
        if (emergency) claims.Add(new(ScadaClaims.Emergency, "1"));
        claims.AddRange(groups.Select(gr => new Claim(ScadaClaims.Group, gr)));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
        await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);
        http.User = principal;
    }

    private static async Task<MeResponse?> MeAsync(HttpContext http, NpgsqlDataSource ds, string login, CancellationToken ct,
        ClaimsPrincipal user, string[]? groups = null)
    {
        var u = await ds.QuerySingleAsync(
            "SELECT login, display_name, must_change_password, source, emergency FROM users WHERE login = $1 AND enabled",
            r => (Login: r.GetString(0), Name: r.GetString(1), MustChange: r.GetBoolean(2), Source: r.GetString(3), Emergency: r.GetBoolean(4)),
            ct, login);
        if (u.Login is null) return null;
        groups ??= user.Groups();
        var access = http.RequestServices.GetRequiredService<AccessService>();
        var perms = await access.ForAsync(u.Login, groups, ct);
        var m = await access.ModelAsync(ct);
        var dict = Perm.All.Where(perms.HasAnywhere)
            .ToDictionary(p => p, p => perms.HasEverywhere(p) ? (object)"all" : perms.ToDictionary()[p]);
        var roles = perms.Sources.Select(s => s.Role).Distinct().ToArray();
        return new MeResponse(u.Login, u.Name, u.Source, u.Emergency, u.MustChange && u.Source == "local", dict, roles, groups);
    }

    /// <summary>
    /// Проверка сеанса (cookie): доменным пользователям группы перечитываются раз в RefreshMinutes;
    /// отключённая в AD запись теряет сеанс; при недоступности AD — работа с последними правами до OfflineGraceHours.
    /// Локальным — раз в минуту проверяется, что запись не отключена.
    /// </summary>
    public static async Task ValidatePrincipalAsync(CookieValidatePrincipalContext ctx)
    {
        var user = ctx.Principal;
        if (user?.Identity?.IsAuthenticated != true) return;
        var sp = ctx.HttpContext.RequestServices;
        var opt = sp.GetRequiredService<IOptions<DirectoryOptions>>().Value;
        var domain = user.IsDomain();
        var checkedAt = Ticks(user.FindFirst(ScadaClaims.CheckedAt)?.Value);
        var interval = domain ? TimeSpan.FromMinutes(Math.Max(1, opt.RefreshMinutes)) : TimeSpan.FromMinutes(1);
        if (DateTime.UtcNow - checkedAt < interval) return;

        var ds = sp.GetRequiredService<NpgsqlDataSource>();
        var audit = sp.GetRequiredService<Audit>();
        var login = user.Login();
        var enabled = await ds.ScalarAsync("SELECT enabled FROM users WHERE login = $1", ctx.HttpContext.RequestAborted, login) as bool?;
        if (enabled != true) { await RejectAsync(ctx, audit, login, "disabled_in_scada"); return; }

        var claims = user.Claims.Where(c => c.Type != ScadaClaims.CheckedAt).ToList();
        var now = DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture);
        if (domain)
        {
            var dir = sp.GetRequiredService<IDirectory>();
            var res = await dir.LookupAsync(login, ctx.HttpContext.RequestAborted);
            switch (res.Status)
            {
                case DirectoryStatus.Ok:
                    claims = claims.Where(c => c.Type is not (ScadaClaims.Group or ScadaClaims.LastDirectoryOk)).ToList();
                    claims.AddRange(res.User!.Groups.Select(gr => new Claim(ScadaClaims.Group, gr)));
                    claims.Add(new(ScadaClaims.LastDirectoryOk, now));
                    await ds.ExecAsync("UPDATE users SET ad_checked_at = now() WHERE login = $1", CancellationToken.None, login);
                    break;
                case DirectoryStatus.Unavailable:
                    var lastOk = Ticks(user.FindFirst(ScadaClaims.LastDirectoryOk)?.Value);
                    if (DateTime.UtcNow - lastOk > TimeSpan.FromHours(opt.OfflineGraceHours))
                    { await RejectAsync(ctx, audit, login, "ad_unavailable_grace_expired"); return; }
                    break;
                default:   // отключена или удалена в AD
                    await RejectAsync(ctx, audit, login, res.Status == DirectoryStatus.Disabled ? "disabled_in_ad" : "not_found_in_ad");
                    return;
            }
        }
        claims.Add(new(ScadaClaims.CheckedAt, now));
        ctx.ReplacePrincipal(new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)));
        ctx.ShouldRenew = true;
    }

    private static async Task RejectAsync(CookieValidatePrincipalContext ctx, Audit audit, string login, string reason)
    {
        ctx.RejectPrincipal();
        await ctx.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        await audit.WriteAsync(login, "session_revoked", "user:" + login, new { reason }, ctx.HttpContext.Ip());
    }

    private static DateTime Ticks(string? s) =>
        long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var t) ? new DateTime(t, DateTimeKind.Utc) : DateTime.MinValue;

    /// <summary>
    /// Первый запуск: если пользователей нет — создаётся admin (Администратор безопасности + Инженер на корне)
    /// с обязательной сменой пароля.
    /// </summary>
    public static async Task EnsureAdminAsync(NpgsqlDataSource ds, Passwords pw, string initialPassword, ILogger log)
    {
        var count = Convert.ToInt64(await ds.ScalarAsync("SELECT count(*) FROM users", CancellationToken.None));
        if (count > 0) return;
        await ds.ExecAsync(
            "INSERT INTO users (login, display_name, password_hash, must_change_password) VALUES ('admin','Администратор',$1,true)",
            CancellationToken.None, pw.Hash(initialPassword));
        await ds.ExecAsync(
            "INSERT INTO role_assignments (principal_type, principal, role_id, area_id, created_by, comment) " +
            "SELECT 'user', 'admin', id, (SELECT id FROM areas WHERE parent_id IS NULL), 'system', 'первый запуск' " +
            "FROM roles WHERE code IN ('security_admin','engineer') ON CONFLICT DO NOTHING", CancellationToken.None);
        log.LogWarning("Создан пользователь admin с начальным паролем из конфигурации. Смените пароль при первом входе.");
    }
}
