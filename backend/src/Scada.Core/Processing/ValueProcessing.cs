using System.Globalization;
using Scada.Core.Model;

namespace Scada.Core.Processing;

public static class ValueConverter
{
    /// <summary>Сырое значение драйвера → нормализованное значение с масштабированием.</summary>
    public static TagValue Normalize(TagDefinition tag, in RawValue raw, DateTime nowUtc)
    {
        if (raw.Value is null || raw.Quality.IsBad())
            return new TagValue(tag.Id, null, null, raw.Quality.IsBad() ? raw.Quality : Quality.Bad,
                raw.TsSourceUtc, nowUtc);

        if (tag.DataType == TagDataType.String)
            return new TagValue(tag.Id, null, Convert.ToString(raw.Value, CultureInfo.InvariantCulture),
                raw.Quality, raw.TsSourceUtc, nowUtc);

        if (!TryToDouble(raw.Value, out var v))
            return new TagValue(tag.Id, null, null, Quality.BadTypeMismatch, raw.TsSourceUtc, nowUtc);

        if (tag.DataType == TagDataType.Bool)
            v = v != 0 ? 1 : 0;
        else if (tag.HasScaling)
            v = Scale(v, tag.RawLow!.Value, tag.RawHigh!.Value, tag.EngLow!.Value, tag.EngHigh!.Value);

        if (double.IsNaN(v) || double.IsInfinity(v))
            return new TagValue(tag.Id, null, null, Quality.BadTypeMismatch, raw.TsSourceUtc, nowUtc);

        var q = raw.Quality;
        // выход за инженерный диапазон — Uncertain (значение не отбрасываем)
        if (q.IsGood() && tag.HasScaling)
        {
            var lo = Math.Min(tag.EngLow!.Value, tag.EngHigh!.Value);
            var hi = Math.Max(tag.EngLow!.Value, tag.EngHigh!.Value);
            var span = hi - lo;
            if (v < lo - span * 0.02 || v > hi + span * 0.02)
                q = Quality.UncertainEngineeringUnitsExceeded;
        }

        return new TagValue(tag.Id, v, null, q, raw.TsSourceUtc, nowUtc);
    }

    public static double Scale(double v, double rawLo, double rawHi, double engLo, double engHi) =>
        engLo + (v - rawLo) * (engHi - engLo) / (rawHi - rawLo);

    /// <summary>Обратное масштабирование для записи: инженерное → сырое.</summary>
    public static double Unscale(TagDefinition tag, double eng) =>
        tag.HasScaling
            ? Scale(eng, tag.EngLow!.Value, tag.EngHigh!.Value, tag.RawLow!.Value, tag.RawHigh!.Value)
            : eng;

    public static bool TryToDouble(object value, out double result)
    {
        switch (value)
        {
            case bool b: result = b ? 1 : 0; return true;
            case double d: result = d; return true;
            case float f: result = f; return true;
            case string s:
                return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out result);
            case IConvertible c:
                try { result = c.ToDouble(CultureInfo.InvariantCulture); return true; }
                catch { result = 0; return false; }
            default: result = 0; return false;
        }
    }
}

/// <summary>
/// Зона нечувствительности. Пропускает значение, если изменились качество/строка
/// или число отличается от последнего пропущенного больше чем на deadband.
/// </summary>
public static class Deadband
{
    public static bool Passes(in TagValue last, in TagValue next, double deadband)
    {
        if (last.Quality != next.Quality) return true;
        if (last.Str != next.Str) return true;
        if (last.Num is null || next.Num is null) return last.Num != next.Num;
        if (deadband <= 0) return last.Num.Value != next.Num.Value;
        return Math.Abs(next.Num.Value - last.Num.Value) > deadband;
    }
}
