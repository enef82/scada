using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Npgsql;
using Scada.Core.Data;
using Scada.Core.Model;
using Scada.Core.Notify;
using Scada.Web.Infrastructure;

namespace Scada.Web.Realtime;

/// <summary>
/// Хаб реального времени. Клиент подписывается только на теги открытого экрана
/// и сразу получает их текущий снимок. Изменения рассылаются пачками не чаще FlushIntervalMs.
/// </summary>
[Authorize(Policy = Policies.Prefix + Scada.Core.Access.Perm.View)]
public sealed class RealtimeHub(RealtimeService rt, AccessService access) : Hub
{
    /// <summary>Подписка фильтруется по областям: значения тегов чужих областей не отправляются.</summary>
    public async Task<object[][]> Subscribe(int[] tagIds)
    {
        var perms = await access.ForAsync(Context.User!, Context.ConnectionAborted);
        var runtime = await access.RuntimeAsync(Context.ConnectionAborted);
        var allowed = runtime.Allowed(perms, tagIds.Take(10_000));
        rt.Subscribe(Context.ConnectionId, allowed);
        return rt.Snapshot(allowed);
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        rt.Unsubscribe(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }
}

public sealed class RealtimeOptions
{
    public int FlushIntervalMs { get; set; } = 250;
}

/// <summary>
/// Мост PostgreSQL LISTEN → SignalR. Держит кэш текущих значений;
/// после каждого переподключения LISTEN перечитывает tag_current целиком.
/// </summary>
public sealed class RealtimeService : BackgroundService
{
    private readonly NpgsqlDataSource _ds;
    private readonly IHubContext<RealtimeHub> _hub;
    private readonly ILogger<RealtimeService> _log;
    private readonly ILoggerFactory _loggers;
    private readonly CommandWaiter _waiter;
    private readonly AccessService _access;
    private readonly RealtimeOptions _opt;
    private readonly ConcurrentDictionary<int, TagValue> _values = new();
    private readonly ConcurrentDictionary<string, HashSet<int>> _subs = new();
    private readonly HashSet<int> _dirty = new();
    private readonly object _dirtyLock = new();
    private PgListener? _listener;

    public DateTime? LastHeartbeatAt { get; private set; }
    public string? LastHeartbeat { get; private set; }
    public bool ListenerConnected => _listener?.Connected == true;
    public int CachedValues => _values.Count;
    public int Connections => _subs.Count;

    public RealtimeService(NpgsqlDataSource ds, IHubContext<RealtimeHub> hub, CommandWaiter waiter, AccessService access,
        Microsoft.Extensions.Options.IOptions<RealtimeOptions> opt, ILogger<RealtimeService> log, ILoggerFactory loggers)
    {
        _ds = ds;
        _hub = hub;
        _waiter = waiter;
        _access = access;
        _opt = opt.Value;
        _log = log;
        _loggers = loggers;
    }

    public void Subscribe(string connectionId, int[] tagIds)
    {
        // повторный Subscribe заменяет набор (смена экрана)
        _subs[connectionId] = new HashSet<int>(tagIds.Take(10_000));
    }

    public void Unsubscribe(string connectionId) => _subs.TryRemove(connectionId, out _);

    public object[][] Snapshot(IEnumerable<int> tagIds) =>
        tagIds.Where(_values.ContainsKey).Select(id => Pack(_values[id])).ToArray();

    public bool TryGet(int tagId, out TagValue v) => _values.TryGetValue(tagId, out v);

    private static object[] Pack(in TagValue v) =>
    [
        v.TagId, v.Num!, v.Str!, (short)v.Quality,
        new DateTimeOffset(v.TsSource, TimeSpan.Zero).ToUnixTimeMilliseconds(),
    ];

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _listener = new PgListener(_ds, _loggers.CreateLogger<PgListener>(),
            [PgChannels.Values, PgChannels.Alarm, PgChannels.CommandDone, PgChannels.CommandConfirm, PgChannels.Device,
             PgChannels.Heartbeat, PgChannels.Config, PgChannels.ConfigApplied, PgChannels.Draft],
            OnNotificationAsync, ResyncAsync);
        await Task.WhenAll(_listener.RunAsync(ct), FlushLoop(ct));
    }

    private async Task ResyncAsync(CancellationToken ct)
    {
        var rows = await _ds.QueryAsync(
            "SELECT tag_id, num, str, quality, ts_source, ts_server FROM tag_current",
            r => new TagValue(r.GetInt32(0), r.GetNullableDouble(1), r.GetNullableString(2),
                (Quality)r.GetInt16(3), r.GetUtc(4), r.GetUtc(5)), ct);
        var present = new HashSet<int>();
        lock (_dirtyLock)
        {
            foreach (var v in rows)
            {
                _values[v.TagId] = v;
                _dirty.Add(v.TagId);
                present.Add(v.TagId);
            }
            foreach (var id in _values.Keys.Where(id => !present.Contains(id)).ToList()) _values.TryRemove(id, out _);
        }
        _log.LogInformation("Снимок текущих значений перечитан: {Count}", rows.Count);

        // признак жизни службы сбора — из таблицы (heartbeat-уведомления до подключения потеряны)
        var hb = await _ds.QuerySingleAsync(
            "SELECT value::text, updated_at FROM settings WHERE key = 'acquisition_heartbeat'",
            r => (Json: r.GetString(0), At: r.GetUtc(1)), ct);
        if (hb.Json is not null && (LastHeartbeatAt is null || hb.At > LastHeartbeatAt))
        {
            LastHeartbeatAt = hb.At;
            LastHeartbeat = hb.Json;
        }
        _access.InvalidateRuntime();
        // клиентам — перечитать тревоги и диагностику
        await _hub.Clients.All.SendAsync("resync", ct);
    }

    private async Task OnNotificationAsync(PgNotification n, CancellationToken ct)
    {
        switch (n.Channel)
        {
            case PgChannels.Values:
                var list = ValuesPayload.Unpack(n.Payload, DateTime.UtcNow);
                lock (_dirtyLock)
                {
                    foreach (var v in list)
                    {
                        _values[v.TagId] = v;
                        _dirty.Add(v.TagId);
                    }
                }
                break;

            case PgChannels.Alarm:
                await _hub.Clients.All.SendAsync("alarm", JsonDocument.Parse(n.Payload).RootElement, ct);
                break;

            case PgChannels.CommandDone:
                using (var doc = JsonDocument.Parse(n.Payload))
                {
                    var root = doc.RootElement;
                    var id = root.GetProperty("id").GetInt64();
                    var status = root.GetProperty("status").GetString() ?? "";
                    var result = root.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.String
                        ? r.GetString() : null;
                    _waiter.Complete(id, status, result);
                    await _hub.Clients.All.SendAsync("command", root.Clone(), ct);
                }
                break;

            case PgChannels.CommandConfirm:
                // окно подтверждения: клиенты с правом «Критичные команды» перечитывают список ожидающих
                if (long.TryParse(n.Payload, NumberStyles.None, CultureInfo.InvariantCulture, out var cid))
                    await _hub.Clients.All.SendAsync("confirmRequest", cid, ct);
                break;

            case PgChannels.Device:
                if (int.TryParse(n.Payload, NumberStyles.None, CultureInfo.InvariantCulture, out var devId))
                    await _hub.Clients.All.SendAsync("device", devId, ct);
                break;

            case PgChannels.Heartbeat:
                LastHeartbeatAt = DateTime.UtcNow;
                LastHeartbeat = n.Payload;
                await _hub.Clients.All.SendAsync("heartbeat", JsonDocument.Parse(n.Payload).RootElement, ct);
                break;

            case PgChannels.Draft:
                // правка черновика: инженерам — обновить списки и плашку черновика
                await _hub.Clients.All.SendAsync("config", n.Payload, ct);
                break;

            case PgChannels.Config:
                _access.InvalidateRuntime();
                // опубликована версия: экраны операторов перечитывают схемы
                await _hub.Clients.All.SendAsync("published", n.Payload, ct);
                break;

            case PgChannels.ConfigApplied:
                await _hub.Clients.All.SendAsync("configApplied", JsonDocument.Parse(n.Payload).RootElement, ct);
                break;
        }
    }

    /// <summary>Системное оповещение всех АРМ (например, вход аварийной учётной записью).</summary>
    public Task SystemNoticeAsync(string level, string text, CancellationToken ct) =>
        _hub.Clients.All.SendAsync("systemNotice", new { level, text, ts = DateTime.UtcNow }, ct);

    private async Task FlushLoop(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(_opt.FlushIntervalMs));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                int[] dirty;
                lock (_dirtyLock)
                {
                    if (_dirty.Count == 0) continue;
                    dirty = _dirty.ToArray();
                    _dirty.Clear();
                }

                foreach (var (conn, set) in _subs)
                {
                    var batch = new List<object[]>();
                    foreach (var id in dirty)
                        if (set.Contains(id) && _values.TryGetValue(id, out var v)) batch.Add(Pack(v));
                    if (batch.Count == 0) continue;
                    try
                    {
                        // медленный клиент не тормозит остальных: SendAsync буферизуется SignalR по соединению
                        _ = _hub.Clients.Client(conn).SendAsync("values", batch, ct);
                    }
                    catch (Exception ex)
                    {
                        _log.LogDebug(ex, "Не удалось отправить значения клиенту {Conn}", conn);
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
    }
}

/// <summary>Ожидание результата команды: POST /api/commands возвращает итог исполнения.</summary>
public sealed class CommandWaiter
{
    private readonly ConcurrentDictionary<long, TaskCompletionSource<(string Status, string? Result)>> _waits = new();

    public Task<(string Status, string? Result)> Register(long id)
    {
        var tcs = new TaskCompletionSource<(string, string?)>(TaskCreationOptions.RunContinuationsAsynchronously);
        _waits[id] = tcs;
        return tcs.Task;
    }

    public void Complete(long id, string status, string? result)
    {
        if (_waits.TryRemove(id, out var tcs)) tcs.TrySetResult((status, result));
    }

    public void Forget(long id) => _waits.TryRemove(id, out _);
}
