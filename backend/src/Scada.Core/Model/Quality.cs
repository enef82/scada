namespace Scada.Core.Model;

/// <summary>
/// Качество значения. Коды построены по мотивам OPC UA StatusCode:
/// 0..63 — Good, 64..127 — Uncertain, 128..255 — Bad.
/// Остальные протоколы маппятся в эти коды, OPC UA — напрямую.
/// </summary>
public enum Quality : short
{
    Good = 0,
    GoodLocalOverride = 1,

    Uncertain = 64,
    UncertainLastUsable = 65,
    UncertainSensorNotAccurate = 66,
    UncertainEngineeringUnitsExceeded = 67,

    Bad = 128,
    BadConfigError = 129,
    BadNotConnected = 130,
    BadDeviceFailure = 131,
    BadCommFailure = 132,
    BadOutOfService = 133,
    BadWaitingForInitialData = 134,
    BadStale = 135,
    BadTypeMismatch = 136,
}

public enum QualityCategory { Good, Uncertain, Bad }

public static class QualityExtensions
{
    public static QualityCategory Category(this Quality q) => (short)q switch
    {
        < 64 => QualityCategory.Good,
        < 128 => QualityCategory.Uncertain,
        _ => QualityCategory.Bad,
    };

    public static bool IsGood(this Quality q) => q.Category() == QualityCategory.Good;
    public static bool IsBad(this Quality q) => q.Category() == QualityCategory.Bad;

    /// <summary>Маппинг кода OPC UA StatusCode (старшие 2 бита — severity).</summary>
    public static Quality FromOpcUaStatus(uint statusCode)
    {
        var severity = statusCode >> 30;
        if (severity == 0) return Quality.Good;
        if (severity == 1) return Quality.Uncertain;
        return (statusCode & 0xFFFF0000) switch
        {
            0x808A0000 => Quality.BadNotConnected,       // BadNotConnected
            0x80050000 => Quality.BadCommFailure,        // BadCommunicationError
            0x808B0000 => Quality.BadDeviceFailure,      // BadDeviceFailure
            0x80890000 => Quality.BadConfigError,        // BadConfigurationError
            0x808D0000 => Quality.BadOutOfService,       // BadOutOfService
            0x80320000 => Quality.BadWaitingForInitialData,
            0x80740000 => Quality.BadTypeMismatch,
            _ => Quality.Bad,
        };
    }
}
