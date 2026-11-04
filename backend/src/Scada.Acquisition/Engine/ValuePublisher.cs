using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using Scada.Core.Model;
using Scada.Core.Notify;

namespace Scada.Acquisition.Engine;

/// <summary>
/// Периодически (PublishIntervalMs) пишет изменившиеся значения в tag_current и рассылает
/// NOTIFY scada_values. Обе операции — в одном пакете/транзакции: веб, получив уведомление
/// или перечитав снимок, видит согласованное состояние.
/// </summary>
public sealed class ValuePublisher
{
    private readonly NpgsqlDataSource _ds;
    private readonly TagEngine _engine;
    private readonly ILogger<ValuePublisher> _log;
    private readonly AcquisitionOptions _opt;
    private readonly Dictionary<int, TagValue> _pending = new();

    public ValuePublisher(NpgsqlDataSource ds, TagEngine engine, IOptions<AcquisitionOptions> opt,
        ILogger<ValuePublisher> log)
    {
        _ds = ds;
        _engine = engine;
        _log = log;
        _opt = opt.Value;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(_opt.PublishIntervalMs));
        var failing = false;
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                foreach (var v in _engine.TakeDirty()) _pending[v.TagId] = v; // последнее значение побеждает
                if (_pending.Count == 0) continue;
                try
                {
                    await PublishAsync(_pending.Values.ToList(), ct);
                    _pending.Clear();
                    if (failing) _log.LogInformation("Публикация значений восстановлена");
                    failing = false;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    if (!failing) _log.LogError(ex, "Не удалось опубликовать значения, накапливаю");
                    failing = true;
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task PublishAsync(List<TagValue> values, CancellationToken ct)
    {
        var n = values.Count;
        var ids = new int[n];
        var nums = new double?[n];
        var strs = new string?[n];
        var qs = new short[n];
        var tsSrc = new DateTime[n];
        var tsSrv = new DateTime[n];
        for (var i = 0; i < n; i++)
        {
            var v = values[i];
            ids[i] = v.TagId; nums[i] = v.Num; strs[i] = v.Str; qs[i] = (short)v.Quality;
            tsSrc[i] = v.TsSource; tsSrv[i] = v.TsServer;
        }

        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var batch = new NpgsqlBatch(conn);

        var upsert = new NpgsqlBatchCommand(
            """
            INSERT INTO tag_current (tag_id, num, str, quality, ts_source, ts_server)
            SELECT u.* FROM unnest($1::int[], $2::float8[], $3::text[], $4::int2[], $5::timestamptz[], $6::timestamptz[])
                 AS u(tag_id, num, str, quality, ts_source, ts_server)
            JOIN tags t ON t.id = u.tag_id
            ON CONFLICT (tag_id) DO UPDATE SET
                num = EXCLUDED.num, str = EXCLUDED.str, quality = EXCLUDED.quality,
                ts_source = EXCLUDED.ts_source, ts_server = EXCLUDED.ts_server
            """);
        upsert.Parameters.Add(new NpgsqlParameter { Value = ids, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Integer });
        upsert.Parameters.Add(new NpgsqlParameter { Value = nums, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Double });
        upsert.Parameters.Add(new NpgsqlParameter { Value = strs, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text });
        upsert.Parameters.Add(new NpgsqlParameter { Value = qs, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Smallint });
        upsert.Parameters.Add(new NpgsqlParameter { Value = tsSrc, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.TimestampTz });
        upsert.Parameters.Add(new NpgsqlParameter { Value = tsSrv, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.TimestampTz });
        batch.BatchCommands.Add(upsert);

        var notify = new NpgsqlBatchCommand($"SELECT pg_notify('{PgChannels.Values}', p) FROM unnest($1::text[]) AS p");
        notify.Parameters.Add(new NpgsqlParameter
        {
            Value = ValuesPayload.Pack(values).ToArray(),
            NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text,
        });
        batch.BatchCommands.Add(notify);

        // NpgsqlBatch выполняется в неявной транзакции: уведомления уходят только после записи
        await batch.ExecuteNonQueryAsync(ct);
    }
}
