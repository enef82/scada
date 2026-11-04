using System.Data.Common;
using Npgsql;
using Scada.Core.Data;

namespace Scada.Web.Infrastructure;

/// <summary>Тонкие помощники поверх Npgsql (позиционные параметры $1, $2, ...).</summary>
public static class DbExt
{
    public static async Task<List<T>> QueryAsync<T>(this NpgsqlDataSource ds, string sql,
        Func<DbDataReader, T> map, CancellationToken ct, params object?[] args)
    {
        await using var cmd = ds.CreateCommand(sql);
        foreach (var a in args) cmd.Parameters.Add(Db.P(a));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<T>();
        while (await r.ReadAsync(ct)) list.Add(map(r));
        return list;
    }

    public static async Task<T?> QuerySingleAsync<T>(this NpgsqlDataSource ds, string sql,
        Func<DbDataReader, T> map, CancellationToken ct, params object?[] args)
    {
        var list = await ds.QueryAsync(sql, map, ct, args);
        return list.Count > 0 ? list[0] : default;
    }

    public static async Task<int> ExecAsync(this NpgsqlDataSource ds, string sql, CancellationToken ct, params object?[] args)
    {
        await using var cmd = ds.CreateCommand(sql);
        foreach (var a in args) cmd.Parameters.Add(Db.P(a));
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    public static async Task<object?> ScalarAsync(this NpgsqlDataSource ds, string sql, CancellationToken ct, params object?[] args)
    {
        await using var cmd = ds.CreateCommand(sql);
        foreach (var a in args) cmd.Parameters.Add(Db.P(a));
        var v = await cmd.ExecuteScalarAsync(ct);
        return v is DBNull ? null : v;
    }

    public static bool IsUniqueViolation(this Exception ex) => ex is PostgresException { SqlState: "23505" };
    public static bool IsForeignKeyViolation(this Exception ex) => ex is PostgresException { SqlState: "23503" };
    public static bool IsCheckViolation(this Exception ex) => ex is PostgresException { SqlState: "23514" or "23502" or "22P02" };
}
