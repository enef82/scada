namespace Scada.Core.Model;

/// <summary>
/// Нормализованное значение тега. Числовые типы (и bool как 0/1) — в <see cref="Num"/>,
/// строки — в <see cref="Str"/>. Отсутствие значения — null, а НЕ ноль.
/// Метки времени всегда в UTC.
/// </summary>
public readonly record struct TagValue(
    int TagId,
    double? Num,
    string? Str,
    Quality Quality,
    DateTime TsSource,
    DateTime TsServer)
{
    public static TagValue NoData(int tagId, Quality q, DateTime now) =>
        new(tagId, null, null, q, now, now);

    public bool SameData(in TagValue other) =>
        Num == other.Num && Str == other.Str && Quality == other.Quality;
}

/// <summary>Сырое значение от драйвера (до масштабирования).</summary>
public readonly record struct RawValue(int TagId, object? Value, Quality Quality, DateTime TsSourceUtc);
