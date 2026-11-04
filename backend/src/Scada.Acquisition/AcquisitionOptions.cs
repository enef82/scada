namespace Scada.Acquisition;

public sealed class AcquisitionOptions
{
    /// <summary>Период публикации изменений в tag_current + NOTIFY (не связан с опросом ПЛК).</summary>
    public int PublishIntervalMs { get; set; } = 250;

    public int HistorianBatchSize { get; set; } = 5000;
    public int HistorianFlushMs { get; set; } = 1000;
    public int HistorianQueueCapacity { get; set; } = 500_000;

    /// <summary>Дисковый буфер архива на время недоступности БД.</summary>
    public string SpoolPath { get; set; } = "/var/lib/scada/spool";
    public int SpoolMaxMb { get; set; } = 1024;

    public int HistoryRetentionDays { get; set; } = 90;

    /// <summary>Сколько ждать результата записи в устройство, включая контрольное чтение.</summary>
    public int CommandExecuteTimeoutMs { get; set; } = 5000;

    public int DiagnosticsIntervalMs { get; set; } = 5000;
}
