using Scada.Core.Model;
using Scada.Core.Notify;
using Scada.Core.Processing;

namespace Scada.Tests;

public class ProcessingTests
{
    private static readonly DateTime Now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static TagDefinition Tag(TagDataType type = TagDataType.Float32, bool scaling = false) => new()
    {
        Id = 1, Name = "t", Address = "x", DataType = type,
        RawLow = scaling ? 4000 : null, RawHigh = scaling ? 20000 : null,
        EngLow = scaling ? 0 : null, EngHigh = scaling ? 10 : null,
    };

    [Fact]
    public void Scaling_maps_raw_to_engineering()
    {
        var v = ValueConverter.Normalize(Tag(TagDataType.UInt16, scaling: true), new RawValue(1, (ushort)12000, Quality.Good, Now), Now);
        Assert.Equal(5.0, v.Num!.Value, 6);
        Assert.Equal(Quality.Good, v.Quality);
        Assert.Equal(12000, ValueConverter.Unscale(Tag(scaling: true), 5.0), 6);
    }

    [Fact]
    public void Out_of_range_is_uncertain_not_dropped()
    {
        var v = ValueConverter.Normalize(Tag(TagDataType.UInt16, scaling: true), new RawValue(1, (ushort)25000, Quality.Good, Now), Now);
        Assert.Equal(Quality.UncertainEngineeringUnitsExceeded, v.Quality);
        Assert.NotNull(v.Num);
    }

    [Fact]
    public void Missing_value_is_null_not_zero()
    {
        var v = ValueConverter.Normalize(Tag(), new RawValue(1, null, Quality.BadCommFailure, Now), Now);
        Assert.Null(v.Num);
        Assert.Equal(Quality.BadCommFailure, v.Quality);
    }

    [Fact]
    public void NaN_is_rejected()
    {
        var v = ValueConverter.Normalize(Tag(), new RawValue(1, float.NaN, Quality.Good, Now), Now);
        Assert.Null(v.Num);
        Assert.True(v.Quality.IsBad());
    }

    [Fact]
    public void Deadband_filters_small_changes()
    {
        var a = new TagValue(1, 10.0, null, Quality.Good, Now, Now);
        Assert.False(Deadband.Passes(a, a with { Num = 10.4 }, 0.5));
        Assert.True(Deadband.Passes(a, a with { Num = 10.6 }, 0.5));
        Assert.True(Deadband.Passes(a, a with { Quality = Quality.BadStale }, 100)); // смена качества проходит всегда
        Assert.True(Deadband.Passes(a, a with { Num = 10.0001 }, 0));
    }

    [Fact]
    public void Opc_status_codes_map_to_quality()
    {
        Assert.Equal(Quality.Good, QualityExtensions.FromOpcUaStatus(0));
        Assert.Equal(Quality.Uncertain, QualityExtensions.FromOpcUaStatus(0x40000000));
        Assert.Equal(Quality.BadNotConnected, QualityExtensions.FromOpcUaStatus(0x808A0000));
        Assert.Equal(Quality.Bad, QualityExtensions.FromOpcUaStatus(0x80000000));
    }

    [Fact]
    public void Values_payload_roundtrip_and_chunking()
    {
        var values = Enumerable.Range(1, 2000)
            .Select(i => new TagValue(i, i * 1.5, i % 100 == 0 ? "строка \"x\"" : null, Quality.Good, Now, Now))
            .ToList();
        var chunks = ValuesPayload.Pack(values).ToList();
        Assert.True(chunks.Count > 1);
        Assert.All(chunks, c => Assert.True(System.Text.Encoding.UTF8.GetByteCount(c) <= PgChannels.MaxPayloadBytes));

        var back = chunks.SelectMany(c => ValuesPayload.Unpack(c, Now)).ToList();
        Assert.Equal(values.Count, back.Count);
        Assert.Equal(values[99].Str, back[99].Str);
        Assert.Equal(values[1999].Num, back[1999].Num);
        Assert.Equal(Now, back[0].TsSource);
    }
}
