using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Npgsql;
using Scada.Core.Notify;

namespace Scada.Web.Infrastructure;

public static class UserExt
{
    public static string Login(this ClaimsPrincipal u) => u.Identity?.Name ?? "?";
    public static string Ip(this HttpContext c) => c.Connection.RemoteIpAddress?.ToString() ?? "";
}

public sealed class Passwords
{
    private readonly PasswordHasher<object> _hasher = new();
    private static readonly object User = new();

    public string Hash(string password) => _hasher.HashPassword(User, password);

    public bool Verify(string hash, string password) =>
        _hasher.VerifyHashedPassword(User, hash, password) != PasswordVerificationResult.Failed;

    public static string? Validate(string password) =>
        password.Length < 8 ? "Пароль должен быть не короче 8 символов" : null;
}

/// <summary>Блокировка подбора: 5 неудачных попыток → пауза 5 минут на логин.</summary>
public sealed class LoginThrottle
{
    private readonly ConcurrentDictionary<string, (int Fails, DateTime Until)> _state = new(StringComparer.OrdinalIgnoreCase);

    public bool IsLocked(string login, out TimeSpan left)
    {
        left = TimeSpan.Zero;
        if (!_state.TryGetValue(login, out var s) || s.Until <= DateTime.UtcNow) return false;
        left = s.Until - DateTime.UtcNow;
        return true;
    }

    public void Fail(string login) => _state.AddOrUpdate(login, _ => (1, DateTime.MinValue), (_, s) =>
    {
        var fails = s.Until > DateTime.MinValue && s.Until <= DateTime.UtcNow ? 1 : s.Fails + 1;
        return fails >= 5 ? (0, DateTime.UtcNow.AddMinutes(5)) : (fails, DateTime.MinValue);
    });

    public void Success(string login) => _state.TryRemove(login, out _);
}

/// <summary>Журнал аудита. Неизменяемость и цепочка хешей обеспечиваются триггерами БД.</summary>
public sealed class Audit(NpgsqlDataSource ds, ILogger<Audit> log)
{
    public async Task WriteAsync(string? user, string action, string obj, object? details, string? ip)
    {
        try
        {
            await ds.ExecAsync(
                "INSERT INTO audit_log (user_login, action, object, details, client_ip) VALUES ($1,$2,$3,$4::jsonb,$5)",
                CancellationToken.None,
                user, action, obj, JsonSerializer.Serialize(details ?? new { }, JsonOpts.Web), ip);
        }
        catch (Exception ex)
        {
            // аудит не должен молча теряться — пишем в журнал службы
            log.LogError(ex, "Аудит не записан: {User} {Action} {Object}", user, action, obj);
        }
    }

    public Task WriteAsync(HttpContext http, string action, string obj, object? details = null) =>
        WriteAsync(http.User.Identity?.IsAuthenticated == true ? http.User.Login() : null, action, obj, details, http.Ip());
}
