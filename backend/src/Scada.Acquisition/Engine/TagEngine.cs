using System.Threading.Channels;
using Scada.Acquisition.Alarms;
using Scada.Acquisition.Historian;
using Scada.Core.Model;
using Scada.Core.Processing;

namespace Scada.Acquisition.Engine;

/// <summary>
/// Движок тегов: нормализация, масштабирование, качество, устаревание,
/// раздельные зоны нечувствительности для интерфейса и архива, передача в тревоги.
/// Один поток-потребитель; публикатор и командный модуль читают состояние под блокировкой.
/// </summary>
public sealed class TagEngine
{
    private sealed class Runtime
    {
        public TagValue Current;
        public TagValue LastPublished;
        public bool PublishedOnce;
        public TagValue LastArchived;
        public DateTime LastArchivedAt;
        public bool ArchivedOnce;
        public DateTime LastReceivedAt;
    }

    private readonly HistorianWriter _historian;
    private readonly AlarmService _alarms;
    private readonly ILogger<TagEngine> _log;
    private readonly object _sync = new();
    private Dictionary<int, TagDefinition> _tags = new();
    private readonly Dictionary<int, Runtime> _rt = new();
    private readonly HashSet<int> _dirty = new();

    private readonly Channel<IReadOnlyList<RawValue>> _input =
        Channel.CreateBounded<IReadOnlyList<RawValue>>(new BoundedChannelOptions(2000)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,   // обратное давление на драйверы
        });

    public TagEngine(HistorianWriter historian, AlarmService alarms, ILogger<TagEngine> log)
    {
        _historian = historian;
        _alarms = alarms;
        _log = log;
    }

    public int TagCount { get { lock (_sync) return _tags.Count; } }

    public ValueTask PostAsync(IReadOnlyList<RawValue> values, CancellationToken ct) =>
        _input.Writer.WriteAsync(values, ct);

    public void ApplyConfig(ScadaConfig config)
    {
        var now = DateTime.UtcNow;
        var devices = config.Devices.ToDictionary(d => d.Id);
        lock (_sync)
        {
            _tags = config.Tags.ToDictionary(t => t.Id);
            foreach (var id in _rt.Keys.Where(id => !_tags.ContainsKey(id)).ToList()) _rt.Remove(id);

            foreach (var t in config.Tags)
            {
                var off = !t.Enabled || !devices.TryGetValue(t.DeviceId, out var d) || !d.Enabled;
                if (!_rt.TryGetValue(t.Id, out var rt))
                {
                    rt = new Runtime
                    {
                        Current = TagValue.NoData(t.Id, off ? Quality.BadOutOfService : Quality.BadWaitingForInitialData, now),
                        LastReceivedAt = now,
                    };
                    _rt[t.Id] = rt;
                    _dirty.Add(t.Id);
                }
                else if (off && rt.Current.Quality != Quality.BadOutOfService)
                {
                    rt.Current = rt.Current with { Quality = Quality.BadOutOfService, TsServer = now };
                    _dirty.Add(t.Id);
                }
            }
        }
    }

    public bool TryGetCurrent(int tagId, out TagValue value)
    {
        lock (_sync)
        {
            if (_rt.TryGetValue(tagId, out var rt)) { value = rt.Current; return true; }
            value = default;
            return false;
        }
    }

    public bool TryGetTag(int tagId, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out TagDefinition? tag)
    {
        lock (_sync) return _tags.TryGetValue(tagId, out tag);
    }

    /// <summary>Забирает изменившиеся для интерфейса значения (с учётом deadband_ui).</summary>
    public List<TagValue> TakeDirty()
    {
        lock (_sync)
        {
            var list = new List<TagValue>(_dirty.Count);
            foreach (var id in _dirty)
                if (_rt.TryGetValue(id, out var rt)) list.Add(rt.Current);
            _dirty.Clear();
            return list;
        }
    }

    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var batch in _input.Reader.ReadAllAsync(ct))
            {
                var now = DateTime.UtcNow;
                lock (_sync)
                {
                    foreach (var raw in batch) Process(raw, now);
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private void Process(in RawValue raw, DateTime now)
    {
        if (!_tags.TryGetValue(raw.TagId, out var tag) || !_rt.TryGetValue(raw.TagId, out var rt)) return;

        var v = ValueConverter.Normalize(tag, raw, now);
        rt.LastReceivedAt = now;
        Accept(tag, rt, v, now);
    }

    private void Accept(TagDefinition tag, Runtime rt, TagValue v, DateTime now)
    {
        // В архив и тревоги идёт значение как есть (Bad → null).
        // В оперативное состояние при Bad переносим последнее известное значение:
        // интерфейс показывает его «серым», но никогда не подставляет ноль.
        var current = v;
        if (v.Quality.IsBad() && v.Num is null && v.Str is null)
            current = v with { Num = rt.Current.Num, Str = rt.Current.Str };
        rt.Current = current;

        if (!rt.PublishedOnce || Deadband.Passes(rt.LastPublished, current, tag.DeadbandUi))
        {
            rt.LastPublished = current;
            rt.PublishedOnce = true;
            _dirty.Add(tag.Id);
        }

        if (tag.ArchiveMode == ArchiveMode.Change)
        {
            var forced = (now - rt.LastArchivedAt).TotalSeconds >= tag.ArchiveIntervalS;
            if (!rt.ArchivedOnce || forced || Deadband.Passes(rt.LastArchived, v, tag.DeadbandArchive))
                Archive(rt, v, now);
        }

        _alarms.OnValue(tag.Id, v.Num, v.Quality, now);
    }

    private void Archive(Runtime rt, in TagValue v, DateTime now)
    {
        _historian.Enqueue(v);
        rt.LastArchived = v;
        rt.LastArchivedAt = now;
        rt.ArchivedOnce = true;
    }

    /// <summary>Раз в секунду: устаревание значений и архив по интервалу.</summary>
    public void Tick(DateTime now)
    {
        lock (_sync)
        {
            foreach (var (id, tag) in _tags)
            {
                if (!_rt.TryGetValue(id, out var rt)) continue;

                if (tag.StaleAfterMs is { } stale && !rt.Current.Quality.IsBad()
                    && (now - rt.LastReceivedAt).TotalMilliseconds > stale)
                {
                    Accept(tag, rt, new TagValue(id, null, null, Quality.BadStale, rt.Current.TsSource, now), now);
                }

                if (tag.ArchiveMode == ArchiveMode.Interval
                    && (now - rt.LastArchivedAt).TotalSeconds >= tag.ArchiveIntervalS
                    && rt.Current.Quality != Quality.BadWaitingForInitialData)
                {
                    // по интервалу пишем текущее значение с меткой сервера; Bad — без значения
                    var v = rt.Current.Quality.IsBad()
                        ? new TagValue(id, null, null, rt.Current.Quality, now, now)
                        : rt.Current with { TsSource = now };
                    Archive(rt, v, now);
                }
            }
        }
        _alarms.Tick(now);
    }
}
