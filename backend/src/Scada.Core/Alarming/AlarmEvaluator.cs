using Scada.Core.Model;

namespace Scada.Core.Alarming;

/// <summary>
/// Состояние одной тревоги. «Активна» и «квитирована» — независимые признаки (ISA-18.2):
///   Normal            : !Active &amp;&amp;  Acked
///   ActiveUnacked     :  Active &amp;&amp; !Acked
///   ActiveAcked       :  Active &amp;&amp;  Acked
///   ReturnedUnacked   : !Active &amp;&amp; !Acked
/// </summary>
public sealed class AlarmState
{
    public int DefId { get; init; }
    public bool Active { get; set; }
    public bool Acked { get; set; } = true;
    public double? Value { get; set; }
    public DateTime? RaisedAt { get; set; }
    public DateTime? ClearedAt { get; set; }
    public DateTime? AckedAt { get; set; }
    public string? AckedBy { get; set; }
    public DateTime? ShelvedUntil { get; set; }
    public string? ShelvedBy { get; set; }
    public string? Comment { get; set; }

    // служебное: задержка срабатывания и последний вход
    public DateTime? PendingSince { get; set; }
    public double? LastNum { get; set; }
    public Quality LastQuality { get; set; } = Quality.BadWaitingForInitialData;
    public bool HasInput { get; set; }

    public bool IsNormal => !Active && Acked;
    public bool IsShelved(DateTime now) => ShelvedUntil is { } u && u > now;
}

public enum AlarmEventType { Raise, Clear, Ack, Shelve, Unshelve }

public readonly record struct AlarmTransition(
    int DefId, AlarmEventType Event, double? Value, DateTime Ts, string? User = null, string? Comment = null);

public static class AlarmEvaluator
{
    /// <summary>Новое значение тега → возможный переход состояния.</summary>
    public static AlarmTransition? Evaluate(AlarmDefinition def, AlarmState st, double? num, Quality q, DateTime now)
    {
        st.LastNum = num;
        st.LastQuality = q;
        st.HasInput = true;
        return Step(def, st, now);
    }

    /// <summary>Периодический вызов: отрабатывает задержку срабатывания и снятие с контроля.</summary>
    public static AlarmTransition? Tick(AlarmDefinition def, AlarmState st, DateTime now)
    {
        if (st.ShelvedUntil is { } until && until <= now)
        {
            st.ShelvedUntil = null;
            st.ShelvedBy = null;
            return new AlarmTransition(def.Id, AlarmEventType.Unshelve, st.LastNum, now);
        }
        return st.HasInput ? Step(def, st, now) : null;
    }

    private static AlarmTransition? Step(AlarmDefinition def, AlarmState st, DateTime now)
    {
        if (!def.Enabled) return null;

        var cond = Condition(def, st.Active, st.LastNum, st.LastQuality);
        if (cond is null) return null; // значение неизвестно — состояние «замораживаем»

        if (cond.Value)
        {
            if (st.Active) { st.Value = st.LastNum; return null; }
            if (st.IsShelved(now)) { st.PendingSince = null; return null; }

            st.PendingSince ??= now;
            if ((now - st.PendingSince.Value).TotalMilliseconds < def.DelayMs) return null;

            st.PendingSince = null;
            st.Active = true;
            st.Acked = false;
            st.Value = st.LastNum;
            st.RaisedAt = now;
            st.ClearedAt = null;
            st.AckedAt = null;
            st.AckedBy = null;
            return new AlarmTransition(def.Id, AlarmEventType.Raise, st.LastNum, now);
        }

        st.PendingSince = null;
        if (!st.Active) return null;

        st.Active = false;
        st.ClearedAt = now;
        st.Value = st.LastNum;
        return new AlarmTransition(def.Id, AlarmEventType.Clear, st.LastNum, now);
    }

    /// <summary>
    /// Условие тревоги с гистерезисом. null — оценить нельзя (плохое качество для аналоговых условий).
    /// </summary>
    public static bool? Condition(AlarmDefinition def, bool currentlyActive, double? num, Quality q)
    {
        if (def.Kind == AlarmKind.BadQuality) return q.IsBad();
        if (q.IsBad() || num is null) return null;

        var v = num.Value;
        var h = def.Hysteresis;
        var limit = def.Limit ?? 0;

        return def.Kind switch
        {
            AlarmKind.HiHi or AlarmKind.Hi => currentlyActive ? v > limit - h : v > limit,
            AlarmKind.Lo or AlarmKind.LoLo => currentlyActive ? v < limit + h : v < limit,
            AlarmKind.DiscreteOn => v != 0,
            AlarmKind.DiscreteOff => v == 0,
            _ => null,
        };
    }

    public static AlarmTransition? Acknowledge(AlarmState st, string user, string? comment, DateTime now)
    {
        if (st.Acked) return null;
        st.Acked = true;
        st.AckedAt = now;
        st.AckedBy = user;
        if (!string.IsNullOrWhiteSpace(comment)) st.Comment = comment;
        return new AlarmTransition(st.DefId, AlarmEventType.Ack, st.Value, now, user, comment);
    }

    /// <summary>Временное снятие с контроля (shelving). Активная тревога сбрасывается.</summary>
    public static AlarmTransition Shelve(AlarmState st, TimeSpan duration, string user, string? comment, DateTime now)
    {
        st.ShelvedUntil = now + duration;
        st.ShelvedBy = user;
        st.PendingSince = null;
        if (st.Active) { st.Active = false; st.ClearedAt = now; }
        st.Acked = true;
        if (!string.IsNullOrWhiteSpace(comment)) st.Comment = comment;
        return new AlarmTransition(st.DefId, AlarmEventType.Shelve, st.Value, now, user, comment);
    }
}
