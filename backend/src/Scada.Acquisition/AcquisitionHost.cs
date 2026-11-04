using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Npgsql;
using Scada.Acquisition.Alarms;
using Scada.Acquisition.Commands;
using Scada.Acquisition.Devices;
using Scada.Acquisition.Engine;
using Scada.Acquisition.Historian;
using Scada.Core.Config;
using Scada.Core.Data;
using Scada.Core.Notify;

namespace Scada.Acquisition;

/// <summary>Оркестратор службы сбора: связывает все циклы и обрабатывает LISTEN/NOTIFY.</summary>
public sealed class AcquisitionHost : BackgroundService
{
    private readonly NpgsqlDataSource _ds;
    private readonly TagEngine _engine;
    private readonly ValuePublisher _publisher;
    private readonly HistorianWriter _historian;
    private readonly AlarmService _alarms;
    private readonly DeviceManager _devices;
    private readonly CommandExecutor _commands;
    private readonly ILogger<AcquisitionHost> _log;
    private readonly ILoggerFactory _loggers;
    private readonly AcquisitionOptions _opt;
    private readonly SemaphoreSlim _reloadSignal = new(0, 1);
    private int _loadedVersion;

    public AcquisitionHost(NpgsqlDataSource ds, TagEngine engine, ValuePublisher publisher, HistorianWriter historian,
        AlarmService alarms, DeviceManager devices, CommandExecutor commands, IOptions<AcquisitionOptions> opt,
        ILogger<AcquisitionHost> log, ILoggerFactory loggers)
    {
        _ds = ds;
        _engine = engine;
        _publisher = publisher;
        _historian = historian;
        _alarms = alarms;
        _devices = devices;
        _commands = commands;
        _opt = opt.Value;
        _log = log;
        _loggers = loggers;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await WaitForDatabaseAsync(ct);
        await RunMaintenanceAsync(ct);
        await _commands.RecoverAfterRestartAsync(ct);
        await _alarms.LoadStatesAsync(ct);
        await DraftStore.EnsureInitialVersionAsync(_ds, ct);
        await ReloadConfigAsync(ct);

        var listener = new PgListener(_ds, _loggers.CreateLogger<PgListener>(),
            [PgChannels.Command, PgChannels.AlarmAck, PgChannels.Config],
            OnNotificationAsync,
            // после переподключения: забрать команды, пришедшие за время разрыва (истёкшие не исполнятся),
            // и проверить, не опубликована ли новая версия конфигурации за время разрыва
            async c =>
            {
                await _commands.ScanPendingAsync(c);
                try { _reloadSignal.Release(); } catch (SemaphoreFullException) { }
            });

        _log.LogInformation("Служба сбора запущена");
        var tasks = new List<Task>
        {
            _engine.RunAsync(ct),
            _publisher.RunAsync(ct),
            _historian.RunAsync(ct),
            _alarms.RunPersistAsync(ct),
            listener.RunAsync(ct),
            TickLoop(ct),
            ReloadLoop(ct),
            DiagnosticsLoop(ct),
            MaintenanceLoop(ct),
        };
        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally
        {
            await _devices.DisposeAsync();
        }
    }

    private async Task WaitForDatabaseAsync(CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(1);
        while (true)
        {
            try
            {
                await using var cmd = _ds.CreateCommand("SELECT max(version) FROM schema_version");
                var v = await cmd.ExecuteScalarAsync(ct);
                _log.LogInformation("БД доступна, версия схемы {Version}", v);
                return;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _log.LogWarning("БД недоступна ({Error}), повтор через {Delay}", ex.Message, delay);
                await Task.Delay(delay, ct);
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 30));
            }
        }
    }

    private async Task OnNotificationAsync(PgNotification n, CancellationToken ct)
    {
        switch (n.Channel)
        {
            case PgChannels.Command when long.TryParse(n.Payload, NumberStyles.None, CultureInfo.InvariantCulture, out var id):
                // не блокируем приём уведомлений на время записи в ПЛК
                _ = Task.Run(async () =>
                {
                    try { await _commands.ExecuteAsync(id, ct); }
                    catch (Exception ex) { _log.LogError(ex, "Команда {Id}: сбой исполнителя", id); }
                }, ct);
                break;
            case PgChannels.AlarmAck:
                var req = JsonSerializer.Deserialize<AlarmAckRequest>(n.Payload, JsonOpts.Web);
                if (req is not null) _alarms.HandleAckRequest(req);
                break;
            case PgChannels.Config:
                try { _reloadSignal.Release(); }
                catch (SemaphoreFullException) { /* перезагрузка уже запрошена */ }
                break;
        }
    }

    private async Task ReloadLoop(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await _reloadSignal.WaitAsync(ct);
                await Task.Delay(200, ct);
                while (_reloadSignal.CurrentCount > 0) await _reloadSignal.WaitAsync(ct);
                try { await ReloadConfigAsync(ct); }
                catch (Exception ex) when (!ct.IsCancellationRequested) { _log.LogError(ex, "Ошибка загрузки конфигурации"); }
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// Загружает ПОСЛЕДНЮЮ ОПУБЛИКОВАННУЮ версию (правки черновика сюда не попадают)
    /// и подтверждает применение: config_versions.applied_at → NOTIFY scada_config_applied.
    /// </summary>
    private async Task ReloadConfigAsync(CancellationToken ct)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        var published = await DraftStore.LoadPublishedAsync(_ds, ct);
        if (published is null)
        {
            _log.LogError("Нет опубликованной версии конфигурации");
            return;
        }
        var (version, snapshot) = published.Value;
        if (version == _loadedVersion) return;

        var config = snapshot.ToRuntime();
        _engine.ApplyConfig(config);
        _alarms.ApplyConfig(config.Alarms);
        await _devices.ApplyConfigAsync(config);
        _loadedVersion = version;
        _log.LogInformation("Применена версия конфигурации {Version}: устройств {Devices}, тегов {Tags}, тревог {Alarms}",
            version, config.Devices.Count, config.Tags.Count, config.Alarms.Count);

        await using var cmd = _ds.CreateCommand(
            "UPDATE config_versions SET applied_at = now(), applied_ms = $2 WHERE version = $1 AND applied_at IS NULL");
        cmd.Parameters.Add(Db.P(version));
        cmd.Parameters.Add(Db.P((int)started.ElapsedMilliseconds));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task TickLoop(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        try
        {
            while (await timer.WaitForNextTickAsync(ct)) _engine.Tick(DateTime.UtcNow);
        }
        catch (OperationCanceledException) { }
    }

    private async Task DiagnosticsLoop(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(_opt.DiagnosticsIntervalMs));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                try
                {
                    foreach (var w in _devices.Workers) await WriteDeviceStatusAsync(w.Diagnostics, ct);
                    await _commands.ExpireAwaitingAsync(ct);
                    var hb = new HeartbeatPayload(
                        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), _engine.TagCount, _devices.Workers.Count,
                        _historian.QueueLength, _historian.SpoolBytes, _loadedVersion);
                    var json = JsonSerializer.Serialize(hb, JsonOpts.Web);
                    await using var cmd = _ds.CreateCommand(
                        "INSERT INTO settings(key, value, updated_at) VALUES ('acquisition_heartbeat', $1::jsonb, now()) " +
                        "ON CONFLICT (key) DO UPDATE SET value = EXCLUDED.value, updated_at = now()");
                    cmd.Parameters.Add(Db.P(json));
                    await cmd.ExecuteNonQueryAsync(ct);
                    await PgNotify.SendAsync(_ds, PgChannels.Heartbeat, json, ct);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    _log.LogDebug(ex, "Диагностика не записана");
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task WriteDeviceStatusAsync(Devices.DeviceDiagnostics d, CancellationToken ct)
    {
        await using var cmd = _ds.CreateCommand(
            """
            INSERT INTO device_status (device_id, state, last_ok_at, last_error, error_count, read_count, avg_latency_ms, updated_at)
            SELECT $1,$2,$3,$4,$5,$6,$7, now() WHERE EXISTS (SELECT 1 FROM devices WHERE id = $1)
            ON CONFLICT (device_id) DO UPDATE SET state = EXCLUDED.state, last_ok_at = EXCLUDED.last_ok_at,
                last_error = EXCLUDED.last_error, error_count = EXCLUDED.error_count,
                read_count = EXCLUDED.read_count, avg_latency_ms = EXCLUDED.avg_latency_ms, updated_at = now()
            """);
        cmd.Parameters.Add(Db.P(d.DeviceId));
        cmd.Parameters.Add(Db.P(d.State));
        cmd.Parameters.Add(Db.P(d.LastOkAt));
        cmd.Parameters.Add(Db.P(d.LastError));
        cmd.Parameters.Add(Db.P(d.ErrorCount));
        cmd.Parameters.Add(Db.P(d.ReadCount));
        cmd.Parameters.Add(Db.P(d.AvgLatencyMs));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task MaintenanceLoop(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(6));
        try
        {
            while (await timer.WaitForNextTickAsync(ct)) await RunMaintenanceAsync(ct);
        }
        catch (OperationCanceledException) { }
    }

    private async Task RunMaintenanceAsync(CancellationToken ct)
    {
        try
        {
            await using (var cmd = _ds.CreateCommand("SELECT ensure_history_partitions(2)"))
                await cmd.ExecuteScalarAsync(ct);
            await using (var cmd = _ds.CreateCommand("SELECT drop_history_older_than(make_interval(days => $1))"))
            {
                cmd.Parameters.Add(Db.P(_opt.HistoryRetentionDays));
                var dropped = await cmd.ExecuteScalarAsync(ct);
                if (dropped is int n && n > 0) _log.LogInformation("Удалено секций архива: {Count}", n);
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _log.LogError(ex, "Обслуживание архива не выполнено");
        }
    }
}
