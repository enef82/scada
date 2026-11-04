using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using Scada.Core.Model;
using Scada.Drivers;
using Scada.Drivers.Modbus;

namespace Scada.Tests;

public class ModbusCodecTests
{
    [Theory]
    [InlineData("hr:100", ModbusArea.HoldingRegister, 100, null)]
    [InlineData("IR:0:cdab", ModbusArea.InputRegister, 0, ByteOrder.CDAB)]
    [InlineData("co:15", ModbusArea.Coil, 15, null)]
    public void Parses_addresses(string text, ModbusArea area, int offset, ByteOrder? order)
    {
        var a = ModbusAddress.Parse(text);
        Assert.Equal(area, a.Area);
        Assert.Equal(offset, a.Offset);
        Assert.Equal(order, a.Order);
    }

    [Fact]
    public void Parses_bit_and_string_length()
    {
        var bit = ModbusAddress.Parse("hr:5.3");
        Assert.Equal(3, bit.Bit);
        var str = ModbusAddress.Parse("hr:200#10");
        Assert.Equal(10, str.Length(TagDataType.String));
    }

    [Theory]
    [InlineData("xx:1")]
    [InlineData("hr:-1")]
    [InlineData("hr:70000")]
    [InlineData("hr:1.16")]
    [InlineData("hr:1:zzzz")]
    public void Rejects_bad_addresses(string text) =>
        Assert.Throws<TagAddressException>(() => ModbusAddress.Parse(text));

    [Theory]
    [InlineData(ByteOrder.ABCD)]
    [InlineData(ByteOrder.CDAB)]
    [InlineData(ByteOrder.BADC)]
    [InlineData(ByteOrder.DCBA)]
    public void Float_roundtrip_all_byte_orders(ByteOrder order)
    {
        var regs = ModbusCodec.Encode(123.456f, TagDataType.Float32, order);
        var back = (float)ModbusCodec.Decode(regs, TagDataType.Float32, ModbusAddress.Parse("hr:0"), order);
        Assert.Equal(123.456f, back);
    }

    [Fact]
    public void Float_cdab_matches_known_layout()
    {
        // 10.0f = 0x41200000; в CDAB младшее слово идёт первым
        ushort[] regs = [0x0000, 0x4120];
        Assert.Equal(10.0f, (float)ModbusCodec.Decode(regs, TagDataType.Float32, ModbusAddress.Parse("hr:0"), ByteOrder.CDAB));
        ushort[] abcd = [0x4120, 0x0000];
        Assert.Equal(10.0f, (float)ModbusCodec.Decode(abcd, TagDataType.Float32, ModbusAddress.Parse("hr:0"), ByteOrder.ABCD));
    }

    [Fact]
    public void Int16_is_signed_and_bit_extraction()
    {
        ushort[] regs = [0xFFFE];
        Assert.Equal((short)-2, ModbusCodec.Decode(regs, TagDataType.Int16, ModbusAddress.Parse("hr:0"), ByteOrder.ABCD));
        ushort[] bits = [0b1000];
        Assert.Equal(true, ModbusCodec.Decode(bits, TagDataType.Bool, ModbusAddress.Parse("hr:0.3"), ByteOrder.ABCD));
        Assert.Equal(false, ModbusCodec.Decode(bits, TagDataType.Bool, ModbusAddress.Parse("hr:0.2"), ByteOrder.ABCD));
    }

    [Fact]
    public void Encode_rejects_overflow() =>
        Assert.Throws<OverflowException>(() => ModbusCodec.Encode(70000, TagDataType.Int16, ByteOrder.ABCD));
}

/// <summary>Интеграционный тест драйвера с минимальным Modbus TCP-сервером в памяти.</summary>
public class ModbusDriverTests
{
    private sealed class FakeServer : IAsyncDisposable
    {
        public readonly ushort[] Holding = new ushort[1000];
        public readonly bool[] Coils = new bool[100];
        public int Requests;
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public FakeServer()
        {
            _listener.Start();
            _loop = Task.Run(AcceptLoop);
        }

        private async Task AcceptLoop()
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                    _ = Task.Run(() => Serve(client));
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
        }

        private async Task Serve(TcpClient client)
        {
            using var _ = client;
            var s = client.GetStream();
            var header = new byte[7];
            try
            {
                while (true)
                {
                    await s.ReadExactlyAsync(header, _cts.Token);
                    var len = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4));
                    var pdu = new byte[len - 1];
                    await s.ReadExactlyAsync(pdu, _cts.Token);
                    Interlocked.Increment(ref Requests);
                    var resp = Handle(pdu);
                    var frame = new byte[7 + resp.Length];
                    header.AsSpan(0, 4).CopyTo(frame);
                    BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(4), (ushort)(resp.Length + 1));
                    frame[6] = header[6];
                    resp.CopyTo(frame, 7);
                    await s.WriteAsync(frame, _cts.Token);
                }
            }
            catch { /* клиент закрыл соединение */ }
        }

        private byte[] Handle(byte[] pdu)
        {
            var fc = pdu[0];
            var addr = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(1));
            switch (fc)
            {
                case 0x03:
                {
                    var count = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(3));
                    if (addr + count > Holding.Length) return [(byte)(fc | 0x80), 2];
                    var r = new byte[2 + count * 2];
                    r[0] = fc; r[1] = (byte)(count * 2);
                    for (var i = 0; i < count; i++) BinaryPrimitives.WriteUInt16BigEndian(r.AsSpan(2 + i * 2), Holding[addr + i]);
                    return r;
                }
                case 0x01:
                {
                    var count = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(3));
                    var r = new byte[2 + (count + 7) / 8];
                    r[0] = fc; r[1] = (byte)((count + 7) / 8);
                    for (var i = 0; i < count; i++) if (Coils[addr + i]) r[2 + i / 8] |= (byte)(1 << (i % 8));
                    return r;
                }
                case 0x05:
                    Coils[addr] = pdu[3] == 0xFF;
                    return pdu;
                case 0x06:
                    Holding[addr] = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(3));
                    return pdu;
                case 0x10:
                {
                    var count = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(3));
                    for (var i = 0; i < count; i++) Holding[addr + i] = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(6 + i * 2));
                    return pdu[..5];
                }
                default: return [(byte)(fc | 0x80), 1];
            }
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            _listener.Stop();
            try { await _loop; } catch { }
        }
    }

    private static TagDefinition T(int id, string addr, TagDataType type) => new()
    {
        Id = id, Name = $"t{id}", Address = addr, DataType = type, Writable = true,
    };

    [Fact]
    public async Task Reads_block_and_writes_values()
    {
        await using var srv = new FakeServer();
        srv.Holding[10] = 0x4120; srv.Holding[11] = 0x0000;   // 10.0f ABCD
        srv.Holding[12] = 0xFFFF;                               // -1 int16
        srv.Holding[14] = 0b100;                                // бит 2
        srv.Coils[3] = true;

        var dev = new DeviceDefinition { Id = 1, Name = "plc", Protocol = Protocol.ModbusTcp, Endpoint = $"127.0.0.1:{srv.Port}", TimeoutMs = 2000 };
        await using var drv = new ModbusTcpDriver(dev, NullLogger.Instance);
        await drv.ConnectAsync(CancellationToken.None);

        var tags = new[]
        {
            T(1, "hr:10", TagDataType.Float32), T(2, "hr:12", TagDataType.Int16), T(3, "hr:14.2", TagDataType.Bool),
            T(4, "co:3", TagDataType.Bool), T(5, "hr:999:abcd", TagDataType.Float32), T(6, "bad", TagDataType.Int16),
        };
        var values = (await drv.ReadAsync(tags, CancellationToken.None)).ToDictionary(v => v.TagId);

        Assert.Equal(10.0f, values[1].Value);
        Assert.Equal((short)-1, values[2].Value);
        Assert.Equal(true, values[3].Value);
        Assert.Equal(true, values[4].Value);
        Assert.Equal(Quality.BadConfigError, values[5].Quality);   // вне диапазона адресов сервера
        Assert.Equal(Quality.BadConfigError, values[6].Quality);   // неверный формат адреса
        // hr:10..14 — одним запросом, co:3 — вторым, hr:999 — третьим
        Assert.Equal(3, srv.Requests);

        await drv.WriteAsync(T(7, "hr:20", TagDataType.Float32), 2.5, CancellationToken.None);
        Assert.Equal(2.5f, BinaryPrimitives.ReadSingleBigEndian(
            [(byte)(srv.Holding[20] >> 8), (byte)srv.Holding[20], (byte)(srv.Holding[21] >> 8), (byte)srv.Holding[21]]));

        await drv.WriteAsync(T(8, "hr:14.0", TagDataType.Bool), 1, CancellationToken.None);
        Assert.Equal(0b101, srv.Holding[14]);                       // бит 2 сохранился

        await drv.WriteAsync(T(9, "co:5", TagDataType.Bool), 1, CancellationToken.None);
        Assert.True(srv.Coils[5]);
    }

    [Fact]
    public async Task Connection_refused_throws()
    {
        var dev = new DeviceDefinition { Id = 1, Name = "x", Protocol = Protocol.ModbusTcp, Endpoint = "127.0.0.1:1", TimeoutMs = 500 };
        await using var drv = new ModbusTcpDriver(dev, NullLogger.Instance);
        await Assert.ThrowsAnyAsync<Exception>(() => drv.ConnectAsync(CancellationToken.None));
        Assert.False(drv.IsConnected);
    }
}
