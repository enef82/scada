namespace Scada.Core.Model;

public enum Protocol { Simulator, ModbusTcp, OpcUa }

public enum TagDataType { Bool, Int16, UInt16, Int32, UInt32, Float32, Float64, String }

public enum ArchiveMode { None, Change, Interval }

/// <summary>Класс критичности команды на записываемом теге: какое право нужно и как подтверждать.</summary>
public enum CommandClass { Normal, Important, Critical }

public static class EnumCodes
{
    public static Protocol ParseProtocol(string s) => s switch
    {
        "simulator" => Protocol.Simulator,
        "modbus_tcp" => Protocol.ModbusTcp,
        "opcua" => Protocol.OpcUa,
        _ => throw new ArgumentException($"Неизвестный протокол '{s}'"),
    };

    public static string ToCode(this Protocol p) => p switch
    {
        Protocol.Simulator => "simulator",
        Protocol.ModbusTcp => "modbus_tcp",
        Protocol.OpcUa => "opcua",
        _ => throw new ArgumentOutOfRangeException(nameof(p)),
    };

    public static TagDataType ParseDataType(string s) => s switch
    {
        "bool" => TagDataType.Bool,
        "int16" => TagDataType.Int16,
        "uint16" => TagDataType.UInt16,
        "int32" => TagDataType.Int32,
        "uint32" => TagDataType.UInt32,
        "float32" => TagDataType.Float32,
        "float64" => TagDataType.Float64,
        "string" => TagDataType.String,
        _ => throw new ArgumentException($"Неизвестный тип данных '{s}'"),
    };

    public static string ToCode(this TagDataType t) => t.ToString().ToLowerInvariant();

    public static ArchiveMode ParseArchiveMode(string s) => s switch
    {
        "none" => ArchiveMode.None,
        "change" => ArchiveMode.Change,
        "interval" => ArchiveMode.Interval,
        _ => throw new ArgumentException($"Неизвестный режим архива '{s}'"),
    };

    public static bool IsNumeric(this TagDataType t) => t != TagDataType.String;

    public static CommandClass ParseCommandClass(string? s) => s switch
    {
        "important" => CommandClass.Important,
        "critical" => CommandClass.Critical,
        _ => CommandClass.Normal,
    };

    public static string ToCode(this CommandClass c) => c switch
    {
        CommandClass.Important => "important",
        CommandClass.Critical => "critical",
        _ => "normal",
    };
}

public sealed record DeviceDefinition
{
    public int Id { get; init; }
    public required string Name { get; init; }
    public Protocol Protocol { get; init; }
    public string Endpoint { get; init; } = "";
    /// <summary>JSON с параметрами протокола (unitId, byteOrder, security...).</summary>
    public string SettingsJson { get; init; } = "{}";
    public bool Enabled { get; init; } = true;
    public int PollMs { get; init; } = 1000;
    public int TimeoutMs { get; init; } = 2000;
    public string Description { get; init; } = "";
    /// <summary>Область (узел дерева объектов); 1 — корень установки.</summary>
    public int AreaId { get; init; } = 1;
}

public sealed record TagDefinition
{
    public int Id { get; init; }
    public required string Name { get; init; }
    public string Description { get; init; } = "";
    public int DeviceId { get; init; }
    public required string Address { get; init; }
    public TagDataType DataType { get; init; }
    public string Unit { get; init; } = "";
    public double? RawLow { get; init; }
    public double? RawHigh { get; init; }
    public double? EngLow { get; init; }
    public double? EngHigh { get; init; }
    public int? PollMs { get; init; }
    public int? StaleAfterMs { get; init; }
    public double DeadbandUi { get; init; }
    public double DeadbandArchive { get; init; }
    public ArchiveMode ArchiveMode { get; init; } = ArchiveMode.Change;
    public int ArchiveIntervalS { get; init; } = 60;
    public bool Writable { get; init; }
    public double? WriteMin { get; init; }
    public double? WriteMax { get; init; }
    public bool WriteConfirm { get; init; } = true;
    public bool Enabled { get; init; } = true;
    /// <summary>Собственная область тега; null — область устройства.</summary>
    public int? AreaId { get; init; }
    public CommandClass CommandClass { get; init; } = CommandClass.Normal;

    public bool HasScaling =>
        RawLow is not null && RawHigh is not null && EngLow is not null && EngHigh is not null
        && RawHigh != RawLow;
}

public enum AlarmKind { HiHi, Hi, Lo, LoLo, DiscreteOn, DiscreteOff, BadQuality }

public sealed record AlarmDefinition
{
    public int Id { get; init; }
    public int TagId { get; init; }
    public AlarmKind Kind { get; init; }
    public double? Limit { get; init; }
    public double Hysteresis { get; init; }
    public int DelayMs { get; init; }
    public short Priority { get; init; } = 2;
    public string Message { get; init; } = "";
    public bool Enabled { get; init; } = true;

    public static AlarmKind ParseKind(string s) => s switch
    {
        "hihi" => AlarmKind.HiHi,
        "hi" => AlarmKind.Hi,
        "lo" => AlarmKind.Lo,
        "lolo" => AlarmKind.LoLo,
        "discrete_on" => AlarmKind.DiscreteOn,
        "discrete_off" => AlarmKind.DiscreteOff,
        "bad_quality" => AlarmKind.BadQuality,
        _ => throw new ArgumentException($"Неизвестный тип тревоги '{s}'"),
    };
}

/// <summary>Полная конфигурация, загружаемая службой сбора.</summary>
public sealed record ScadaConfig(
    IReadOnlyList<DeviceDefinition> Devices,
    IReadOnlyList<TagDefinition> Tags,
    IReadOnlyList<AlarmDefinition> Alarms);
