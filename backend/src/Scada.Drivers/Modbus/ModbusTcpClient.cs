using System.Buffers.Binary;
using System.Net.Sockets;

namespace Scada.Drivers.Modbus;

public sealed class ModbusException(byte function, byte code)
    : Exception($"Modbus исключение: функция 0x{function:X2}, код {code} ({Describe(code)})")
{
    public byte Code { get; } = code;

    private static string Describe(byte c) => c switch
    {
        1 => "недопустимая функция",
        2 => "недопустимый адрес",
        3 => "недопустимое значение",
        4 => "отказ устройства",
        6 => "устройство занято",
        10 => "шлюз: нет пути",
        11 => "шлюз: устройство не отвечает",
        _ => "неизвестно",
    };
}

/// <summary>
/// Минимальный клиент Modbus TCP (функции 1, 2, 3, 4, 5, 6, 15, 16).
/// Один запрос в момент времени; запросы сериализуются вызывающим кодом.
/// </summary>
public sealed class ModbusTcpClient : IAsyncDisposable
{
    private readonly string _host;
    private readonly int _port;
    private readonly int _timeoutMs;
    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private ushort _transaction;

    public ModbusTcpClient(string host, int port, int timeoutMs)
    {
        _host = host;
        _port = port;
        _timeoutMs = timeoutMs;
    }

    public bool IsConnected => _tcp?.Connected == true;

    public async Task ConnectAsync(CancellationToken ct)
    {
        await CloseAsync();
        var tcp = new TcpClient { NoDelay = true };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_timeoutMs);
        try
        {
            await tcp.ConnectAsync(_host, _port, cts.Token);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
        _tcp = tcp;
        _stream = tcp.GetStream();
    }

    public Task CloseAsync()
    {
        _stream?.Dispose();
        _tcp?.Dispose();
        _stream = null;
        _tcp = null;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => new(CloseAsync());

    public async Task<bool[]> ReadBitsAsync(byte unit, bool coils, int start, int count, CancellationToken ct)
    {
        var fc = (byte)(coils ? 0x01 : 0x02);
        var pdu = new byte[5];
        pdu[0] = fc;
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(1), (ushort)start);
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(3), (ushort)count);
        var resp = await RequestAsync(unit, pdu, ct);
        var byteCount = resp[1];
        if (byteCount < (count + 7) / 8) throw new IOException("Modbus: короткий ответ");
        var result = new bool[count];
        for (var i = 0; i < count; i++)
            result[i] = ((resp[2 + i / 8] >> (i % 8)) & 1) == 1;
        return result;
    }

    public async Task<ushort[]> ReadRegistersAsync(byte unit, bool holding, int start, int count, CancellationToken ct)
    {
        var fc = (byte)(holding ? 0x03 : 0x04);
        var pdu = new byte[5];
        pdu[0] = fc;
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(1), (ushort)start);
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(3), (ushort)count);
        var resp = await RequestAsync(unit, pdu, ct);
        var byteCount = resp[1];
        if (byteCount != count * 2 || resp.Length < 2 + byteCount) throw new IOException("Modbus: неверная длина ответа");
        var regs = new ushort[count];
        for (var i = 0; i < count; i++)
            regs[i] = BinaryPrimitives.ReadUInt16BigEndian(resp.AsSpan(2 + i * 2));
        return regs;
    }

    public async Task WriteCoilAsync(byte unit, int address, bool value, CancellationToken ct)
    {
        var pdu = new byte[5];
        pdu[0] = 0x05;
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(1), (ushort)address);
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(3), value ? (ushort)0xFF00 : (ushort)0x0000);
        await RequestAsync(unit, pdu, ct);
    }

    public async Task WriteRegistersAsync(byte unit, int address, ushort[] values, CancellationToken ct)
    {
        byte[] pdu;
        if (values.Length == 1)
        {
            pdu = new byte[5];
            pdu[0] = 0x06;
            BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(1), (ushort)address);
            BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(3), values[0]);
        }
        else
        {
            pdu = new byte[6 + values.Length * 2];
            pdu[0] = 0x10;
            BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(1), (ushort)address);
            BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(3), (ushort)values.Length);
            pdu[5] = (byte)(values.Length * 2);
            for (var i = 0; i < values.Length; i++)
                BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(6 + i * 2), values[i]);
        }
        await RequestAsync(unit, pdu, ct);
    }

    /// <summary>Отправляет PDU, возвращает PDU ответа. Проверяет transaction id и исключения.</summary>
    private async Task<byte[]> RequestAsync(byte unit, byte[] pdu, CancellationToken ct)
    {
        var stream = _stream ?? throw new IOException("Modbus: нет соединения");
        var tid = unchecked(++_transaction);

        var frame = new byte[7 + pdu.Length];
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(0), tid);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2), 0);                 // protocol id
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(4), (ushort)(pdu.Length + 1));
        frame[6] = unit;
        pdu.CopyTo(frame, 7);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_timeoutMs);
        try
        {
            await stream.WriteAsync(frame, cts.Token);

            // ответы с чужим transaction id (запоздавшие) пропускаем
            while (true)
            {
                var header = new byte[7];
                await stream.ReadExactlyAsync(header, cts.Token);
                var rtid = BinaryPrimitives.ReadUInt16BigEndian(header);
                var len = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4));
                if (len is < 2 or > 260) throw new IOException("Modbus: неверный заголовок MBAP");
                var body = new byte[len - 1];
                await stream.ReadExactlyAsync(body, cts.Token);
                if (rtid != tid) continue;

                if ((body[0] & 0x80) != 0)
                    throw new ModbusException(pdu[0], body.Length > 1 ? body[1] : (byte)0);
                if (body[0] != pdu[0])
                    throw new IOException($"Modbus: ответ на функцию 0x{body[0]:X2} вместо 0x{pdu[0]:X2}");
                return body;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // тайм-аут: соединение в неизвестном состоянии — закрываем
            await CloseAsync();
            throw new TimeoutException($"Modbus: нет ответа за {_timeoutMs} мс");
        }
        catch (IOException)
        {
            await CloseAsync();
            throw;
        }
    }
}
