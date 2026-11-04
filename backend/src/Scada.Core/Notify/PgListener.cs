using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Scada.Core.Notify;

public readonly record struct PgNotification(string Channel, string Payload);

/// <summary>
/// Выделенное соединение с LISTEN на заданные каналы. Переподключается с backoff.
/// После каждого (пере)подключения вызывает onConnected — там потребитель
/// обязан пересинхронизировать состояние из таблиц, т.к. NOTIFY за время разрыва потеряны.
/// Обработчик вызывается в отдельной задаче, чтобы не блокировать приём уведомлений.
/// </summary>
public sealed class PgListener
{
    private readonly NpgsqlDataSource _ds;
    private readonly ILogger _log;
    private readonly string[] _channels;
    private readonly Func<PgNotification, CancellationToken, Task> _handler;
    private readonly Func<CancellationToken, Task>? _onConnected;
    private readonly Channel<PgNotification> _queue =
        Channel.CreateBounded<PgNotification>(new BoundedChannelOptions(50_000)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.DropWrite,
        });
    private long _dropped;

    public bool Connected { get; private set; }

    public PgListener(
        NpgsqlDataSource ds, ILogger log, IEnumerable<string> channels,
        Func<PgNotification, CancellationToken, Task> handler,
        Func<CancellationToken, Task>? onConnected = null)
    {
        _ds = ds;
        _log = log;
        _channels = channels.ToArray();
        _handler = handler;
        _onConnected = onConnected;
    }

    public Task RunAsync(CancellationToken ct) => Task.WhenAll(ListenLoop(ct), ConsumeLoop(ct));

    private async Task ListenLoop(CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(1);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var conn = await _ds.OpenConnectionAsync(ct);
                conn.Notification += (_, e) =>
                {
                    if (!_queue.Writer.TryWrite(new PgNotification(e.Channel, e.Payload)))
                        Interlocked.Increment(ref _dropped);
                };
                foreach (var ch in _channels)
                {
                    await using var cmd = new NpgsqlCommand($"LISTEN \"{ch}\"", conn);
                    await cmd.ExecuteNonQueryAsync(ct);
                }
                Connected = true;
                delay = TimeSpan.FromSeconds(1);
                _log.LogInformation("LISTEN подключён: {Channels}", string.Join(", ", _channels));

                if (_onConnected is not null)
                    await _onConnected(ct);

                while (!ct.IsCancellationRequested)
                {
                    // WaitAsync с тайм-аутом — заодно keepalive и проверка живости соединения
                    var got = await conn.WaitAsync(TimeSpan.FromSeconds(15), ct);
                    if (!got)
                    {
                        await using var ping = new NpgsqlCommand("SELECT 1", conn);
                        await ping.ExecuteScalarAsync(ct);
                    }
                    var d = Interlocked.Exchange(ref _dropped, 0);
                    if (d > 0)
                    {
                        _log.LogWarning("Очередь уведомлений переполнена, потеряно {Count}; ресинхронизация", d);
                        if (_onConnected is not null) await _onConnected(ct);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Connected = false;
                _log.LogWarning(ex, "LISTEN потерял соединение, повтор через {Delay}", delay);
                try { await Task.Delay(delay, ct); } catch (OperationCanceledException) { break; }
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 30));
            }
        }
        Connected = false;
        _queue.Writer.TryComplete();
    }

    private async Task ConsumeLoop(CancellationToken ct)
    {
        try
        {
            await foreach (var n in _queue.Reader.ReadAllAsync(ct))
            {
                try { await _handler(n, ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex) { _log.LogError(ex, "Ошибка обработки NOTIFY {Channel}", n.Channel); }
            }
        }
        catch (OperationCanceledException) { }
    }
}

public static class PgNotify
{
    public static async Task SendAsync(NpgsqlDataSource ds, string channel, string payload, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand("SELECT pg_notify($1, $2)");
        cmd.Parameters.Add(new NpgsqlParameter { Value = channel });
        cmd.Parameters.Add(new NpgsqlParameter { Value = payload });
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
