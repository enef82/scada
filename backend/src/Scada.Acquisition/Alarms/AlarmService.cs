using System.Threading.Channels;
using Npgsql;
using Scada.Core.Alarming;
using Scada.Core.Data;
using Scada.Core.Model;
using Scada.Core.Notify;

namespace Scada.Acquisition.Alarms;

/// <summary>
/// Владелец состояния тревог. Состояние персистентно (alarm_active), каждое изменение
/// пишется в журнал alarm_events. Квитирование приходит от веба через NOTIFY scada_alarm_ack.
/// </summary>
public sealed class AlarmService
{
    private readonly NpgsqlDataSource _ds;
    private readonly ILogger<AlarmService> _log;
    private readonly object _sync = new();
    private Dictionary<int, AlarmDefinition> _defs = new();
    private Dictionary<int, List<AlarmDefinition>> _byTag = new();
    private readonly Dictionary<int, AlarmState> _states = new();
    private readonly Channel<(AlarmTransition T, AlarmState Snapshot)> _persist =
        Channel.CreateUnbounded<(AlarmTransition, AlarmState)>(new UnboundedChannelOptions { SingleReader = true });

    public AlarmService(NpgsqlDataSource ds, ILogger<AlarmService> log)
    {
        _ds = ds;
        _log = log;
    }

    public int ActiveCount { get { lock (_sync) return _states.Values.Count(s => s.Active); } }

    /// <summary>Загрузка сохранённых состояний — тревоги переживают перезапуск службы.</summary>
    public async Task LoadStatesAsync(CancellationToken ct)
    {
        await using var cmd = _ds.CreateCommand(
            "SELECT alarm_def_id, active, acked, value, raised_at, cleared_at, acked_at, acked_by, " +
            "shelved_until, shelved_by, comment FROM alarm_active");
        await using var r = await cmd.ExecuteReaderAsync(ct);
        lock (_sync)
        {
            _states.Clear();
            while (r.Read())
            {
                var st = new AlarmState
                {
                    DefId = r.GetInt32(0),
                    Active = r.GetBoolean(1),
                    Acked = r.GetBoolean(2),
                    Value = r.GetNullableDouble(3),
                    RaisedAt = r.GetNullableDateTime(4),
                    ClearedAt = r.GetNullableDateTime(5),
                    AckedAt = r.GetNullableDateTime(6),
                    AckedBy = r.GetNullableString(7),
                    ShelvedUntil = r.GetNullableDateTime(8),
                    ShelvedBy = r.GetNullableString(9),
                    Comment = r.GetNullableString(10),
                };
                _states[st.DefId] = st;
            }
        }
        _log.LogInformation("Загружено состояний тревог: {Count}", _states.Count);
    }

    public void ApplyConfig(IReadOnlyList<AlarmDefinition> defs)
    {
        lock (_sync)
        {
            _defs = defs.ToDictionary(d => d.Id);
            _byTag = defs.Where(d => d.Enabled).GroupBy(d => d.TagId).ToDictionary(g => g.Key, g => g.ToList());
            foreach (var id in _states.Keys.Where(id => !_defs.ContainsKey(id)).ToList())
                _states.Remove(id);
            foreach (var d in defs)
            {
                if (!_states.ContainsKey(d.Id)) _states[d.Id] = new AlarmState { DefId = d.Id };
                if (!d.Enabled && _states[d.Id].Active)
                {
                    // отключённая тревога снимается с журналированием
                    var st = _states[d.Id];
                    st.Active = false;
                    st.Acked = true;
                    st.ClearedAt = DateTime.UtcNow;
                    Enqueue(new AlarmTransition(d.Id, AlarmEventType.Clear, st.Value, DateTime.UtcNow, null,
                        "тревога отключена в конфигурации"), st);
                }
            }
        }
    }

    /// <summary>Вызывается движком тегов на каждое новое значение.</summary>
    public void OnValue(int tagId, double? num, Quality q, DateTime now)
    {
        lock (_sync)
        {
            if (!_byTag.TryGetValue(tagId, out var defs)) return;
            foreach (var def in defs)
            {
                var st = _states[def.Id];
                if (AlarmEvaluator.Evaluate(def, st, num, q, now) is { } t) Enqueue(t, st);
            }
        }
    }

    public void Tick(DateTime now)
    {
        lock (_sync)
        {
            foreach (var def in _defs.Values)
            {
                if (_states.TryGetValue(def.Id, out var st) && AlarmEvaluator.Tick(def, st, now) is { } t)
                    Enqueue(t, st);
            }
        }
    }

    public void HandleAckRequest(AlarmAckRequest req)
    {
        var now = DateTime.UtcNow;
        lock (_sync)
        {
            foreach (var id in req.Ids)
            {
                if (!_states.TryGetValue(id, out var st)) continue;
                AlarmTransition? t = req.Action switch
                {
                    "ack" => AlarmEvaluator.Acknowledge(st, req.User, req.Comment, now),
                    "shelve" => AlarmEvaluator.Shelve(st, TimeSpan.FromMinutes(Math.Clamp(req.ShelveMinutes ?? 60, 1, 24 * 60)),
                        req.User, req.Comment, now),
                    "unshelve" when st.ShelvedUntil is not null => Unshelve(st, req.User, now),
                    _ => null,
                };
                if (t is { } tr) Enqueue(tr, st);
            }
        }
    }

    private static AlarmTransition Unshelve(AlarmState st, string user, DateTime now)
    {
        st.ShelvedUntil = null;
        st.ShelvedBy = null;
        return new AlarmTransition(st.DefId, AlarmEventType.Unshelve, st.Value, now, user);
    }

    private void Enqueue(AlarmTransition t, AlarmState st)
    {
        // снимок, чтобы писатель не видел последующих изменений
        var copy = new AlarmState
        {
            DefId = st.DefId, Active = st.Active, Acked = st.Acked, Value = st.Value,
            RaisedAt = st.RaisedAt, ClearedAt = st.ClearedAt, AckedAt = st.AckedAt, AckedBy = st.AckedBy,
            ShelvedUntil = st.ShelvedUntil, ShelvedBy = st.ShelvedBy, Comment = st.Comment,
        };
        _persist.Writer.TryWrite((t, copy));
    }

    /// <summary>Писатель состояний в БД. При ошибке повторяет — порядок событий сохраняется.</summary>
    public async Task RunPersistAsync(CancellationToken ct)
    {
        await foreach (var (t, st) in _persist.Reader.ReadAllAsync(ct))
        {
            while (true)
            {
                try
                {
                    await PersistAsync(t, st, ct);
                    break;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Не удалось сохранить состояние тревоги {Id}, повтор", t.DefId);
                    await Task.Delay(2000, ct);
                }
            }
        }
    }

    private async Task PersistAsync(AlarmTransition t, AlarmState st, CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await using (var cmd = new NpgsqlCommand(
            """
            INSERT INTO alarm_active (alarm_def_id, active, acked, value, raised_at, cleared_at, acked_at,
                                      acked_by, shelved_until, shelved_by, comment, updated_at)
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11, now())
            ON CONFLICT (alarm_def_id) DO UPDATE SET
                active = EXCLUDED.active, acked = EXCLUDED.acked, value = EXCLUDED.value,
                raised_at = EXCLUDED.raised_at, cleared_at = EXCLUDED.cleared_at,
                acked_at = EXCLUDED.acked_at, acked_by = EXCLUDED.acked_by,
                shelved_until = EXCLUDED.shelved_until, shelved_by = EXCLUDED.shelved_by,
                comment = EXCLUDED.comment, updated_at = now()
            """, conn, tx))
        {
            cmd.Parameters.Add(Db.P(st.DefId));
            cmd.Parameters.Add(Db.P(st.Active));
            cmd.Parameters.Add(Db.P(st.Acked));
            cmd.Parameters.Add(Db.P(st.Value));
            cmd.Parameters.Add(Db.P(st.RaisedAt));
            cmd.Parameters.Add(Db.P(st.ClearedAt));
            cmd.Parameters.Add(Db.P(st.AckedAt));
            cmd.Parameters.Add(Db.P(st.AckedBy));
            cmd.Parameters.Add(Db.P(st.ShelvedUntil));
            cmd.Parameters.Add(Db.P(st.ShelvedBy));
            cmd.Parameters.Add(Db.P(st.Comment));
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await using (var ev = new NpgsqlCommand(
            "INSERT INTO alarm_events (ts, alarm_def_id, event, value, user_login, comment) VALUES ($1,$2,$3,$4,$5,$6)",
            conn, tx))
        {
            ev.Parameters.Add(Db.P(t.Ts));
            ev.Parameters.Add(Db.P(t.DefId));
            ev.Parameters.Add(Db.P(t.Event.ToString().ToLowerInvariant()));
            ev.Parameters.Add(Db.P(t.Value));
            ev.Parameters.Add(Db.P(t.User));
            ev.Parameters.Add(Db.P(t.Comment));
            await ev.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }
}
