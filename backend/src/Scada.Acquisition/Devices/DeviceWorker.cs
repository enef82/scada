using System.Diagnostics;
using System.Threading.Channels;
using Scada.Acquisition.Engine;
using Scada.Core.Model;
using Scada.Drivers;

namespace Scada.Acquisition.Devices;

public sealed record WriteRequest(TagDefinition Tag, object RawValue, TaskCompletionSource<RawValue?> Done);

public sealed record DeviceDiagnostics(
    int DeviceId, string State, DateTime? LastOkAt, string? LastError,
    long ErrorCount, long ReadCount, double? AvgLatencyMs);

/// <summary>
/// Цикл одного устройства: подключение с backoff, групповой опрос по интервалам,
/// запись команд между циклами чтения. Отказ одного устройства не влияет на другие.
/// </summary>
public sealed class DeviceWorker : IAsyncDisposable
{
    private readonly IDeviceDriver _driver;
    private readonly TagEngine _engine;
    private readonly ILogger _log;
    private readonly Channel<WriteRequest> _writes = Channel.CreateBounded<WriteRequest>(32);
    private readonly CancellationTokenSource _cts = new();
    private Task? _task;

    private string _state = "connecting";
    private DateTime? _lastOkAt;
    private string? _lastError;
    private long _errors, _reads;
    private double? _avgLatency;

    public DeviceDefinition Device { get; }
    public IReadOnlyList<TagDefinition> Tags { get; }
    public bool IsOnline => _state == "online";

    public DeviceWorker(DeviceDefinition device, IReadOnlyList<TagDefinition> tags, IDeviceDriver driver,
        TagEngine engine, ILogger log)
    {
        Device = device;
        Tags = tags;
        _driver = driver;
        _engine = engine;
        _log = log;
    }

    public DeviceDiagnostics Diagnostics => new(Device.Id, _state, _lastOkAt, _lastError, _errors, _reads, _avgLatency);

    public void Start() => _task = Task.Run(() => RunAsync(_cts.Token));

    /// <summary>Ставит запись в очередь устройства. Возвращает значение контрольного чтения.</summary>
    public async Task<RawValue?> WriteAsync(TagDefinition tag, object raw, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<RawValue?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_writes.Writer.TryWrite(new WriteRequest(tag, raw, tcs)))
            throw new InvalidOperationException("Очередь команд устройства переполнена");
        await using var reg = ct.Register(() => tcs.TrySetCanceled(ct));
        return await tcs.Task;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var backoff = TimeSpan.FromSeconds(1);
        var groups = Tags.Where(t => t.Enabled)
            .GroupBy(t => t.PollMs ?? Device.PollMs)
            .Select(g => (PeriodMs: g.Key, Tags: (IReadOnlyList<TagDefinition>)g.ToList(), Next: DateTime.UtcNow))
            .ToArray();

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!_driver.IsConnected)
                {
                    _state = "connecting";
                    await _driver.ConnectAsync(ct);
                    _log.LogInformation("Устройство {Device}: связь установлена", Device.Name);
                    backoff = TimeSpan.FromSeconds(1);
                }
                _state = "online";

                // 1. команды имеют приоритет над опросом
                while (_writes.Reader.TryRead(out var w)) await ExecuteWriteAsync(w, ct);

                // 2. опрос групп, у которых наступил срок
                var now = DateTime.UtcNow;
                for (var i = 0; i < groups.Length; i++)
                {
                    if (groups[i].Next > now) continue;
                    var sw = Stopwatch.StartNew();
                    var values = await _driver.ReadAsync(groups[i].Tags, ct);
                    sw.Stop();
                    _reads++;
                    _lastOkAt = DateTime.UtcNow;
                    _avgLatency = _avgLatency is null ? sw.Elapsed.TotalMilliseconds
                        : _avgLatency * 0.9 + sw.Elapsed.TotalMilliseconds * 0.1;
                    await _engine.PostAsync(values, ct);

                    // следующий срок — от плановой точки, без накопления сдвига
                    var next = groups[i].Next.AddMilliseconds(groups[i].PeriodMs);
                    groups[i].Next = next > DateTime.UtcNow ? next : DateTime.UtcNow.AddMilliseconds(groups[i].PeriodMs);
                }

                // 3. ждём ближайшего срока или команды
                var wait = groups.Length == 0 ? TimeSpan.FromSeconds(1) : groups.Min(g => g.Next) - DateTime.UtcNow;
                if (wait > TimeSpan.Zero)
                {
                    using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    waitCts.CancelAfter(wait);
                    try { await _writes.Reader.WaitToReadAsync(waitCts.Token); }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _errors++;
                _lastError = ex.Message;
                var wasOnline = _state == "online";
                _state = "error";
                if (wasOnline) _log.LogWarning("Устройство {Device}: потеря связи: {Error}", Device.Name, ex.Message);
                else _log.LogDebug("Устройство {Device}: нет связи: {Error}", Device.Name, ex.Message);

                try { await _driver.DisconnectAsync(); } catch { /* уже разорвано */ }
                await MarkAllAsync(wasOnline ? Quality.BadCommFailure : Quality.BadNotConnected, ct);
                FailPendingWrites("нет связи с устройством");

                try { await Task.Delay(backoff, ct); } catch (OperationCanceledException) { break; }
                backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, 30));
            }
        }
        FailPendingWrites("служба остановлена");
    }

    private async Task ExecuteWriteAsync(WriteRequest w, CancellationToken ct)
    {
        try
        {
            await _driver.WriteAsync(w.Tag, w.RawValue, ct);
            // контрольное чтение
            var rb = await _driver.ReadAsync([w.Tag], ct);
            await _engine.PostAsync(rb, ct);
            w.Done.TrySetResult(rb.Count > 0 ? rb[0] : null);
        }
        catch (Exception ex) when (ex is TagAddressException or ArgumentException or OverflowException
                                       or InvalidOperationException or FormatException or InvalidCastException)
        {
            w.Done.TrySetException(ex); // ошибка значения/адреса — связь не рвём
        }
        catch (Exception ex)
        {
            w.Done.TrySetException(ex);
            throw; // ошибка связи — обработает основной цикл
        }
    }

    private void FailPendingWrites(string reason)
    {
        while (_writes.Reader.TryRead(out var w))
            w.Done.TrySetException(new IOException(reason));
    }

    private async Task MarkAllAsync(Quality q, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var list = Tags.Where(t => t.Enabled).Select(t => new RawValue(t.Id, null, q, now)).ToList();
        if (list.Count > 0)
        {
            try { await _engine.PostAsync(list, ct); } catch (OperationCanceledException) { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_task is not null)
        {
            try { await _task.WaitAsync(TimeSpan.FromSeconds(5)); } catch { /* остановка */ }
        }
        try { await _driver.DisconnectAsync(); } catch { }
        await _driver.DisposeAsync();
        _cts.Dispose();
    }
}
