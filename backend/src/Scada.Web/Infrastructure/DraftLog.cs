using Npgsql;
using Scada.Core.Data;

namespace Scada.Web.Infrastructure;

/// <summary>Журнал правок черновика: кто и что менял до публикации.</summary>
public static class DraftLog
{
    public static Task WriteAsync(NpgsqlDataSource ds, HttpContext http, string type, int id, string name, string op, CancellationToken ct) =>
        ds.ExecAsync("INSERT INTO draft_changes (user_login, object_type, object_id, object_name, op) VALUES ($1,$2,$3,$4,$5)",
            ct, http.User.Login(), type, id, name, op);
}
