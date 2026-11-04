using System.Globalization;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using Scada.Core.Model;

namespace Scada.Acquisition.Historian;

/// <summary>
/// Пакетная запись архива через бинарный COPY (EF/Dapper для потока не годятся).
/// При недоступности БД пачки уходят в дисковый буфер и досылаются после восстановления.
/// </summary>
public sealed class HistorianWriter
{
    private readonly NpgsqlDataSource _ds;
    private readonly ILogger<HistorianWriter> _log;
    private readonly AcquisitionOptions _opt;
    private readonly DiskSpool _spool;
    private readonly Channel<TagValue> _queue;
    private long _dropped;

    public HistorianWriter(NpgsqlDataSource ds, IOptions<AcquisitionOptions> opt, ILogger<HistorianWriter> log)
    {
        _ds = ds;
        _log = log;
        _opt = opt.Value;
        _spool = new DiskSpool(_opt.SpoolPath, _opt.SpoolMaxMb * 1024L * 1024L, log);
        _queue = Channel.CreateBounded<TagValue>(new BoundedChannelOptions(_opt.HistorianQueueCapacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.DropWrite,
        });
    }

    public int QueueLength => _queue.Reader.CanCount ? _queue.Reader.Count : -1;
    public long SpoolBytes => _spool.TotalBytes;

    public void Enqueue(in TagValue v)
    {
        if (!_queue.Writer.TryWrite(v) && Interlocked.Increment(ref _dropped) % 10_000 == 1)
            _log.LogError("Очередь архива переполнена, значения теряются (всего {Count})", _dropped);
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var batch = new List<TagValue>(_opt.HistorianBatchSize);
        var dbOk = true;
        while (!ct.IsCancellationRequested)
        {
            batch.Clear();
            try
            {
                using var flushCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                flushCts.CancelAfter(_opt.HistorianFlushMs);
                while (batch.Count < _opt.HistorianBatchSize)
                {
                    if (!_queue.Reader.TryRead(out var v))
                    {
                        if (!await _queue.Reader.WaitToReadAsync(flushCts.Token)) break;
                        continue;
                    }
                    batch.Add(v);
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { /* время сброса пачки */ }
            catch (OperationCanceledException) { break; }

            try
            {
                if (batch.Count > 0) await CopyAsync(batch, ct);
                if (!dbOk) _log.LogInformation("Архив: запись в БД восстановлена");
                dbOk = true;
                if (_spool.HasData) await ReplaySpoolAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                if (dbOk) _log.LogError(ex, "Архив: БД недоступна, запись в дисковый буфер");
                dbOk = false;
                if (batch.Count > 0) _spool.Write(batch);
                try { await Task.Delay(1000, ct); } catch (OperationCanceledException) { break; }
            }
        }

        // остаток очереди при остановке — в буфер, чтобы не потерять
        batch.Clear();
        while (_queue.Reader.TryRead(out var v)) batch.Add(v);
        if (batch.Count > 0)
        {
            try { await CopyAsync(batch, CancellationToken.None); }
            catch { _spool.Write(batch); }
        }
    }

    private async Task CopyAsync(IReadOnlyList<TagValue> batch, CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var w = await conn.BeginBinaryImportAsync(
            "COPY tag_history (tag_id, ts, num, str, quality) FROM STDIN (FORMAT BINARY)", ct);
        foreach (var v in batch)
        {
            await w.StartRowAsync(ct);
            await w.WriteAsync(v.TagId, NpgsqlDbType.Integer, ct);
            await w.WriteAsync(v.TsSource, NpgsqlDbType.TimestampTz, ct);
            if (v.Num is { } n) await w.WriteAsync(n, NpgsqlDbType.Double, ct); else await w.WriteNullAsync(ct);
            if (v.Str is { } s) await w.WriteAsync(s, NpgsqlDbType.Text, ct); else await w.WriteNullAsync(ct);
            await w.WriteAsync((short)v.Quality, NpgsqlDbType.Smallint, ct);
        }
        await w.CompleteAsync(ct);
    }

    private async Task ReplaySpoolAsync(CancellationToken ct)
    {
        foreach (var file in _spool.Files())
        {
            var rows = DiskSpool.Read(file);
            for (var i = 0; i < rows.Count; i += _opt.HistorianBatchSize)
                await CopyAsync(rows.GetRange(i, Math.Min(_opt.HistorianBatchSize, rows.Count - i)), ct);
            _spool.Delete(file);
            _log.LogInformation("Архив: дослано из буфера {Count} значений ({File})", rows.Count, Path.GetFileName(file));
        }
    }
}

/// <summary>
/// Дисковый буфер: файлы CSV-строк «tagId;tsTicks;num;str(base64);quality».
/// При превышении лимита удаляются самые старые файлы (с записью в журнал).
/// </summary>
public sealed class DiskSpool
{
    private readonly string _dir;
    private readonly long _maxBytes;
    private readonly ILogger _log;

    public DiskSpool(string dir, long maxBytes, ILogger log)
    {
        _dir = dir;
        _maxBytes = maxBytes;
        _log = log;
        try { Directory.CreateDirectory(dir); }
        catch (Exception ex) { _log.LogError(ex, "Не удалось создать каталог буфера {Dir}", dir); }
    }

    public bool HasData => Directory.Exists(_dir) && Directory.EnumerateFiles(_dir, "spool-*.dat").Any();

    public long TotalBytes =>
        Directory.Exists(_dir) ? Directory.EnumerateFiles(_dir, "spool-*.dat").Sum(f => new FileInfo(f).Length) : 0;

    public IEnumerable<string> Files() =>
        Directory.EnumerateFiles(_dir, "spool-*.dat").Order(StringComparer.Ordinal).ToList();

    public void Write(IReadOnlyList<TagValue> batch)
    {
        try
        {
            EnforceLimit();
            var path = Path.Combine(_dir, $"spool-{DateTime.UtcNow.Ticks:D20}-{Environment.ProcessId}.dat");
            var tmp = path + ".tmp";
            using (var sw = new StreamWriter(tmp, false, Encoding.UTF8))
            {
                foreach (var v in batch)
                {
                    sw.Write(v.TagId.ToString(CultureInfo.InvariantCulture)); sw.Write(';');
                    sw.Write(v.TsSource.Ticks.ToString(CultureInfo.InvariantCulture)); sw.Write(';');
                    sw.Write(v.Num?.ToString("R", CultureInfo.InvariantCulture) ?? ""); sw.Write(';');
                    sw.Write(v.Str is null ? "" : Convert.ToBase64String(Encoding.UTF8.GetBytes(v.Str))); sw.Write(';');
                    sw.WriteLine(((short)v.Quality).ToString(CultureInfo.InvariantCulture));
                }
            }
            File.Move(tmp, path); // атомарно: в буфере не бывает недописанных файлов
        }
        catch (Exception ex)
        {
            _log.LogCritical(ex, "Дисковый буфер недоступен, потеряно {Count} значений архива", batch.Count);
        }
    }

    public static List<TagValue> Read(string path)
    {
        var list = new List<TagValue>();
        foreach (var line in File.ReadLines(path))
        {
            var p = line.Split(';');
            if (p.Length != 5) continue;
            var ts = new DateTime(long.Parse(p[1], CultureInfo.InvariantCulture), DateTimeKind.Utc);
            list.Add(new TagValue(
                int.Parse(p[0], CultureInfo.InvariantCulture),
                p[2].Length == 0 ? null : double.Parse(p[2], CultureInfo.InvariantCulture),
                p[3].Length == 0 ? null : Encoding.UTF8.GetString(Convert.FromBase64String(p[3])),
                (Quality)short.Parse(p[4], CultureInfo.InvariantCulture),
                ts, ts));
        }
        return list;
    }

    public void Delete(string path) => File.Delete(path);

    private void EnforceLimit()
    {
        var files = Directory.EnumerateFiles(_dir, "spool-*.dat").Order(StringComparer.Ordinal)
            .Select(f => new FileInfo(f)).ToList();
        var total = files.Sum(f => f.Length);
        foreach (var f in files)
        {
            if (total < _maxBytes) break;
            total -= f.Length;
            f.Delete();
            _log.LogError("Дисковый буфер переполнен: удалён самый старый файл {File}", f.Name);
        }
    }
}
