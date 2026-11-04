using System.DirectoryServices.Protocols;
using System.Net;
using System.Text;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Scada.Web.Infrastructure;

/// <summary>
/// Интеграция с Active Directory (раздел «Интеграция с AD» документа о правах).
/// Только LDAPS; пароль проверяется привязкой (bind) и в SCADA не хранится.
/// </summary>
public sealed class DirectoryOptions
{
    public bool Enabled { get; set; }
    /// <summary>Контроллеры домена (имена или адреса).</summary>
    public string[] Servers { get; set; } = [];
    public int Port { get; set; } = 636;
    /// <summary>Где искать пользователей, например DC=vodokanal,DC=local.</summary>
    public string BaseDn { get; set; } = "";
    /// <summary>Где искать группы; пусто — BaseDn.</summary>
    public string? GroupBaseDn { get; set; }
    /// <summary>Служебная учётная запись только для чтения (DN или user@domain).</summary>
    public string BindDn { get; set; } = "";
    public string BindPassword { get; set; } = "";
    /// <summary>{0} — логин (экранируется по RFC 4515).</summary>
    public string UserFilter { get; set; } = "(&(objectCategory=person)(objectClass=user)(sAMAccountName={0}))";
    public bool NestedGroups { get; set; } = true;
    public int RefreshMinutes { get; set; } = 15;
    /// <summary>Сколько уже вошедшие пользователи работают с последними известными правами, если AD недоступен.</summary>
    public int OfflineGraceHours { get; set; } = 8;
    public int TimeoutSeconds { get; set; } = 5;
}

public sealed record DirectoryUser(string Login, string DisplayName, string Dn, string[] Groups, bool Disabled);

public enum DirectoryStatus { Ok, InvalidCredentials, NotFound, Disabled, Unavailable }

public sealed record DirectoryResult(DirectoryStatus Status, DirectoryUser? User = null, string? Error = null);

public interface IDirectory
{
    bool Enabled { get; }
    /// <summary>Вход: найти пользователя, проверить пароль привязкой, прочитать группы.</summary>
    Task<DirectoryResult> AuthenticateAsync(string login, string password, CancellationToken ct);
    /// <summary>Перечитать группы и состояние учётной записи (без пароля, служебной записью).</summary>
    Task<DirectoryResult> LookupAsync(string login, CancellationToken ct);
    DirectoryHealth Health { get; }
}

public sealed record DirectoryHealth(bool Enabled, DateTime? LastOkAt, DateTime? LastErrorAt, string? LastError);

public sealed class LdapDirectory : IDirectory
{
    private const int LdapInvalidCredentials = 49;
    private const int UacAccountDisabled = 0x2;
    private const string MatchingRuleInChain = "1.2.840.113556.1.4.1941";

    private readonly DirectoryOptions _o;
    private readonly ILogger<LdapDirectory> _log;
    private DateTime? _lastOk, _lastErr;
    private string? _lastError;

    public LdapDirectory(IOptions<DirectoryOptions> o, ILogger<LdapDirectory> log)
    {
        _o = o.Value;
        _log = log;
        if (_o.Enabled)
        {
            if (_o.Port == 389) throw new InvalidOperationException("Directory: незащищённый LDAP (389) не допускается — используйте LDAPS (636)");
            if (_o.Servers.Length == 0 || string.IsNullOrWhiteSpace(_o.BaseDn))
                throw new InvalidOperationException("Directory: не заданы Servers и BaseDn");
        }
    }

    public bool Enabled => _o.Enabled;
    public DirectoryHealth Health => new(_o.Enabled, _lastOk, _lastErr, _lastError);

    private LdapConnection Connect(NetworkCredential cred)
    {
        var conn = new LdapConnection(new LdapDirectoryIdentifier(_o.Servers, _o.Port, false, false), cred, AuthType.Basic)
        {
            Timeout = TimeSpan.FromSeconds(_o.TimeoutSeconds),
        };
        conn.SessionOptions.ProtocolVersion = 3;
        conn.SessionOptions.SecureSocketLayer = true;   // LDAPS; сертификат контроллера проверяется системой
        conn.SessionOptions.ReferralChasing = ReferralChasingOptions.None;
        conn.Bind();
        return conn;
    }

    public Task<DirectoryResult> AuthenticateAsync(string login, string password, CancellationToken ct) => Task.Run(() =>
    {
        if (string.IsNullOrEmpty(password)) return new DirectoryResult(DirectoryStatus.InvalidCredentials);
        var found = Find(login);
        if (found.Status != DirectoryStatus.Ok) return found;
        try
        {
            // пустой пароль привёл бы к анонимной привязке — отсечён выше
            using var _ = Connect(new NetworkCredential(found.User!.Dn, password));
            return found.User.Disabled ? new DirectoryResult(DirectoryStatus.Disabled, found.User) : found;
        }
        catch (LdapException ex) when (ex.ErrorCode == LdapInvalidCredentials)
        {
            return new DirectoryResult(DirectoryStatus.InvalidCredentials);
        }
        catch (Exception ex) when (ex is LdapException or DirectoryOperationException)
        {
            return Fail(ex);
        }
    }, ct);

    public Task<DirectoryResult> LookupAsync(string login, CancellationToken ct) => Task.Run(() =>
    {
        var r = Find(login);
        return r.Status == DirectoryStatus.Ok && r.User!.Disabled ? r with { Status = DirectoryStatus.Disabled } : r;
    }, ct);

    private DirectoryResult Find(string login)
    {
        try
        {
            using var conn = Connect(new NetworkCredential(_o.BindDn, _o.BindPassword));
            var filter = string.Format(System.Globalization.CultureInfo.InvariantCulture, _o.UserFilter, Escape(login));
            var res = (SearchResponse)conn.SendRequest(new SearchRequest(_o.BaseDn, filter, SearchScope.Subtree,
                "distinguishedName", "displayName", "sAMAccountName", "userAccountControl", "memberOf"));
            if (res.Entries.Count == 0) { Ok(); return new DirectoryResult(DirectoryStatus.NotFound); }
            var e = res.Entries[0];
            var dn = e.DistinguishedName;
            var uac = int.TryParse(Attr(e, "userAccountControl"), out var u) ? u : 0;
            var groups = ReadGroups(conn, e, dn);
            Ok();
            return new DirectoryResult(DirectoryStatus.Ok,
                new DirectoryUser(Attr(e, "sAMAccountName") ?? login, Attr(e, "displayName") ?? login, dn, groups, (uac & UacAccountDisabled) != 0));
        }
        catch (Exception ex) when (ex is LdapException or DirectoryOperationException)
        {
            return Fail(ex);
        }
    }

    private string[] ReadGroups(LdapConnection conn, SearchResultEntry user, string dn)
    {
        if (!_o.NestedGroups)
            return (user.Attributes["memberOf"]?.GetValues(typeof(string)) ?? []).Cast<string>().Select(Cn).ToArray();
        // все группы, включая вложенные, одним запросом (LDAP_MATCHING_RULE_IN_CHAIN)
        var req = new SearchRequest(string.IsNullOrWhiteSpace(_o.GroupBaseDn) ? _o.BaseDn : _o.GroupBaseDn, $"(&(objectClass=group)(member:{MatchingRuleInChain}:={Escape(dn)}))",
            SearchScope.Subtree, "cn");
        var res = (SearchResponse)conn.SendRequest(req);
        return res.Entries.Cast<SearchResultEntry>().Select(x => Attr(x, "cn") ?? Cn(x.DistinguishedName)).Distinct().ToArray();
    }

    private void Ok() => _lastOk = DateTime.UtcNow;

    private DirectoryResult Fail(Exception ex)
    {
        _lastErr = DateTime.UtcNow;
        _lastError = ex.Message;
        _log.LogWarning("AD недоступен: {Error}", ex.Message);
        return new DirectoryResult(DirectoryStatus.Unavailable, Error: ex.Message);
    }

    private static string? Attr(SearchResultEntry e, string name) =>
        e.Attributes[name] is { Count: > 0 } a ? a[0]?.ToString() : null;

    private static string Cn(string dn) =>
        dn.StartsWith("CN=", StringComparison.OrdinalIgnoreCase) ? dn[3..].Split(',')[0] : dn;

    public static string Escape(string s) => Scada.Core.Access.LdapFilter.Escape(s);
}

/// <summary>Повторный ввод пароля (публикация, важные и критичные команды): локально или привязкой к AD.</summary>
public sealed class Credentials(NpgsqlDataSource ds, Passwords passwords, IDirectory directory)
{
    public async Task<bool> VerifyAsync(string login, string? password, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(password)) return false;
        var u = await ds.QuerySingleAsync("SELECT source, password_hash FROM users WHERE login = $1 AND enabled",
            r => (Source: r.GetString(0), Hash: r.GetString(1)), ct, login);
        if (u.Source is null) return false;
        if (u.Source == "local") return passwords.Verify(u.Hash, password);
        if (!directory.Enabled) return false;
        var r = await directory.AuthenticateAsync(login, password, ct);
        return r.Status == DirectoryStatus.Ok;
    }
}
