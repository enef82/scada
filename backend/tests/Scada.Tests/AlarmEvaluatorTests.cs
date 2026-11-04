using Scada.Core.Alarming;
using Scada.Core.Model;

namespace Scada.Tests;

public class AlarmEvaluatorTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static AlarmDefinition Hi(double limit, double hyst = 0, int delayMs = 0) => new()
    {
        Id = 1, TagId = 10, Kind = AlarmKind.Hi, Limit = limit, Hysteresis = hyst, DelayMs = delayMs, Message = "hi",
    };

    [Fact]
    public void Raises_and_clears_with_hysteresis()
    {
        var def = Hi(80, hyst: 2);
        var st = new AlarmState { DefId = 1 };

        Assert.Null(AlarmEvaluator.Evaluate(def, st, 79, Quality.Good, T0));
        var raise = AlarmEvaluator.Evaluate(def, st, 81, Quality.Good, T0.AddSeconds(1));
        Assert.Equal(AlarmEventType.Raise, raise!.Value.Event);
        Assert.True(st.Active);
        Assert.False(st.Acked);

        // внутри гистерезиса — остаётся активной
        Assert.Null(AlarmEvaluator.Evaluate(def, st, 79, Quality.Good, T0.AddSeconds(2)));
        Assert.True(st.Active);

        var clear = AlarmEvaluator.Evaluate(def, st, 77.9, Quality.Good, T0.AddSeconds(3));
        Assert.Equal(AlarmEventType.Clear, clear!.Value.Event);
        Assert.False(st.Active);
        Assert.False(st.Acked); // ReturnedUnacked
    }

    [Fact]
    public void Delay_requires_condition_to_hold()
    {
        var def = Hi(50, delayMs: 2000);
        var st = new AlarmState { DefId = 1 };

        Assert.Null(AlarmEvaluator.Evaluate(def, st, 60, Quality.Good, T0));
        Assert.Null(AlarmEvaluator.Tick(def, st, T0.AddMilliseconds(1500)));
        Assert.False(st.Active);

        // кратковременный выход обнуляет задержку
        Assert.Null(AlarmEvaluator.Evaluate(def, st, 40, Quality.Good, T0.AddMilliseconds(1600)));
        Assert.Null(AlarmEvaluator.Evaluate(def, st, 60, Quality.Good, T0.AddMilliseconds(1700)));
        Assert.Null(AlarmEvaluator.Tick(def, st, T0.AddMilliseconds(3000)));

        var t = AlarmEvaluator.Tick(def, st, T0.AddMilliseconds(3800));
        Assert.Equal(AlarmEventType.Raise, t!.Value.Event);
    }

    [Fact]
    public void Bad_quality_freezes_analog_alarm()
    {
        var def = Hi(50);
        var st = new AlarmState { DefId = 1 };
        AlarmEvaluator.Evaluate(def, st, 60, Quality.Good, T0);
        Assert.True(st.Active);

        Assert.Null(AlarmEvaluator.Evaluate(def, st, null, Quality.BadCommFailure, T0.AddSeconds(1)));
        Assert.True(st.Active); // не снимается из-за отсутствия данных
    }

    [Fact]
    public void Bad_quality_alarm_kind()
    {
        var def = new AlarmDefinition { Id = 2, TagId = 10, Kind = AlarmKind.BadQuality, Message = "связь" };
        var st = new AlarmState { DefId = 2 };
        Assert.Equal(AlarmEventType.Raise,
            AlarmEvaluator.Evaluate(def, st, null, Quality.BadNotConnected, T0)!.Value.Event);
        Assert.Equal(AlarmEventType.Clear,
            AlarmEvaluator.Evaluate(def, st, 1, Quality.Good, T0.AddSeconds(1))!.Value.Event);
    }

    [Fact]
    public void Ack_is_independent_of_active()
    {
        var def = Hi(50);
        var st = new AlarmState { DefId = 1 };
        AlarmEvaluator.Evaluate(def, st, 60, Quality.Good, T0);
        var ack = AlarmEvaluator.Acknowledge(st, "ivanov", "принято", T0.AddSeconds(1));
        Assert.Equal(AlarmEventType.Ack, ack!.Value.Event);
        Assert.True(st.Active);
        Assert.True(st.Acked);
        Assert.Null(AlarmEvaluator.Acknowledge(st, "ivanov", null, T0.AddSeconds(2))); // повторно — нет события

        AlarmEvaluator.Evaluate(def, st, 40, Quality.Good, T0.AddSeconds(3));
        Assert.True(st.IsNormal);
    }

    [Fact]
    public void Shelved_alarm_does_not_raise_until_expired()
    {
        var def = Hi(50);
        var st = new AlarmState { DefId = 1 };
        AlarmEvaluator.Shelve(st, TimeSpan.FromMinutes(10), "petrov", "ремонт датчика", T0);

        Assert.Null(AlarmEvaluator.Evaluate(def, st, 60, Quality.Good, T0.AddMinutes(1)));
        Assert.False(st.Active);

        var unshelve = AlarmEvaluator.Tick(def, st, T0.AddMinutes(11));
        Assert.Equal(AlarmEventType.Unshelve, unshelve!.Value.Event);
        var raise = AlarmEvaluator.Tick(def, st, T0.AddMinutes(11).AddSeconds(1));
        Assert.Equal(AlarmEventType.Raise, raise!.Value.Event);
    }

    [Theory]
    [InlineData(AlarmKind.Lo, 10, 9, true)]
    [InlineData(AlarmKind.Lo, 10, 10, false)]
    [InlineData(AlarmKind.LoLo, 5, 4.99, true)]
    [InlineData(AlarmKind.HiHi, 90, 90.01, true)]
    [InlineData(AlarmKind.DiscreteOn, 0, 1, true)]
    [InlineData(AlarmKind.DiscreteOff, 0, 1, false)]
    public void Conditions(AlarmKind kind, double limit, double value, bool expected)
    {
        var def = new AlarmDefinition { Id = 1, Kind = kind, Limit = limit, Message = "" };
        Assert.Equal(expected, AlarmEvaluator.Condition(def, false, value, Quality.Good));
    }
}
