using System.Buffers.Binary;
using System.Globalization;
using Scada.Core.Model;

namespace Scada.Drivers.Modbus;

public enum ModbusArea { Coil, DiscreteInput, InputRegister, HoldingRegister }

/// <summary>Порядок байт для 32/64-битных значений. ABCD — big-endian (стандарт Modbus).</summary>
public enum ByteOrder { ABCD, CDAB, BADC, DCBA }

/// <summary>
/// Адрес тега Modbus. Формат: <c>область:смещение[.бит][:порядок]</c>, смещение с нуля.
/// Области: co (coil), di (discrete input), ir (input register), hr (holding register).
/// Примеры: <c>hr:100</c>, <c>ir:20:cdab</c>, <c>hr:5.3</c> (бит 3 регистра), <c>co:0</c>.
/// Для string длина в регистрах задаётся через «#»: <c>hr:200#10</c>.
/// </summary>
public readonly record struct ModbusAddress(ModbusArea Area, int Offset, int? Bit, ByteOrder? Order, int StringRegs)
{
    public static ModbusAddress Parse(string address)
    {
        var parts = address.Trim().ToLowerInvariant().Split(':');
        if (parts.Length is < 2 or > 3)
            throw new TagAddressException($"Адрес Modbus '{address}': ожидается область:смещение[:порядок]");

        var area = parts[0] switch
        {
            "co" or "coil" => ModbusArea.Coil,
            "di" => ModbusArea.DiscreteInput,
            "ir" => ModbusArea.InputRegister,
            "hr" => ModbusArea.HoldingRegister,
            _ => throw new TagAddressException($"Адрес Modbus '{address}': неизвестная область '{parts[0]}'"),
        };

        var offPart = parts[1];
        var strRegs = 0;
        var hash = offPart.IndexOf('#');
        if (hash >= 0)
        {
            strRegs = int.Parse(offPart[(hash + 1)..], CultureInfo.InvariantCulture);
            offPart = offPart[..hash];
        }

        int? bit = null;
        var dot = offPart.IndexOf('.');
        if (dot >= 0)
        {
            bit = int.Parse(offPart[(dot + 1)..], CultureInfo.InvariantCulture);
            offPart = offPart[..dot];
            if (bit is < 0 or > 15) throw new TagAddressException($"Адрес '{address}': бит 0..15");
        }

        if (!int.TryParse(offPart, NumberStyles.None, CultureInfo.InvariantCulture, out var offset) || offset > 65535)
            throw new TagAddressException($"Адрес Modbus '{address}': смещение 0..65535");

        ByteOrder? order = null;
        if (parts.Length == 3)
        {
            order = Enum.TryParse<ByteOrder>(parts[2], ignoreCase: true, out var o)
                ? o
                : throw new TagAddressException($"Адрес '{address}': порядок ABCD/CDAB/BADC/DCBA");
        }

        return new ModbusAddress(area, offset, bit, order, strRegs);
    }

    public bool IsBitArea => Area is ModbusArea.Coil or ModbusArea.DiscreteInput;

    /// <summary>Сколько регистров (или битов для coil/di) занимает значение.</summary>
    public int Length(TagDataType type) =>
        IsBitArea ? 1 : type switch
        {
            TagDataType.Bool or TagDataType.Int16 or TagDataType.UInt16 => 1,
            TagDataType.Int32 or TagDataType.UInt32 or TagDataType.Float32 => 2,
            TagDataType.Float64 => 4,
            TagDataType.String => Math.Max(1, StringRegs),
            _ => 1,
        };
}

/// <summary>Кодирование/декодирование регистров.</summary>
public static class ModbusCodec
{
    public static object Decode(ReadOnlySpan<ushort> regs, TagDataType type, ModbusAddress addr, ByteOrder order)
    {
        switch (type)
        {
            case TagDataType.Bool:
                return addr.Bit is { } b ? ((regs[0] >> b) & 1) == 1 : regs[0] != 0;
            case TagDataType.Int16: return unchecked((short)regs[0]);
            case TagDataType.UInt16: return regs[0];
            case TagDataType.Int32: return BinaryPrimitives.ReadInt32BigEndian(ToBytes(regs[..2], order));
            case TagDataType.UInt32: return BinaryPrimitives.ReadUInt32BigEndian(ToBytes(regs[..2], order));
            case TagDataType.Float32: return BinaryPrimitives.ReadSingleBigEndian(ToBytes(regs[..2], order));
            case TagDataType.Float64: return BinaryPrimitives.ReadDoubleBigEndian(ToBytes(regs[..4], order));
            case TagDataType.String:
            {
                var bytes = new byte[regs.Length * 2];
                for (var i = 0; i < regs.Length; i++)
                    BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(i * 2), regs[i]);
                return System.Text.Encoding.ASCII.GetString(bytes).TrimEnd('\0', ' ');
            }
            default: throw new TagAddressException($"Тип {type} не поддерживается Modbus");
        }
    }

    public static ushort[] Encode(object value, TagDataType type, ByteOrder order)
    {
        var d = Convert.ToDouble(value, CultureInfo.InvariantCulture);
        byte[] be;
        switch (type)
        {
            case TagDataType.Bool: return [(ushort)(d != 0 ? 1 : 0)];
            case TagDataType.Int16: return [unchecked((ushort)checked((short)Math.Round(d)))];
            case TagDataType.UInt16: return [checked((ushort)Math.Round(d))];
            case TagDataType.Int32:
                be = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(be, checked((int)Math.Round(d))); break;
            case TagDataType.UInt32:
                be = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(be, checked((uint)Math.Round(d))); break;
            case TagDataType.Float32:
                be = new byte[4]; BinaryPrimitives.WriteSingleBigEndian(be, (float)d); break;
            case TagDataType.Float64:
                be = new byte[8]; BinaryPrimitives.WriteDoubleBigEndian(be, d); break;
            default: throw new TagAddressException($"Запись типа {type} в Modbus не поддерживается");
        }
        return FromBytes(be, order);
    }

    /// <summary>Регистры (в порядке order) → байты big-endian ABCD...</summary>
    internal static byte[] ToBytes(ReadOnlySpan<ushort> regs, ByteOrder order)
    {
        var raw = new byte[regs.Length * 2];
        for (var i = 0; i < regs.Length; i++)
            BinaryPrimitives.WriteUInt16BigEndian(raw.AsSpan(i * 2), regs[i]);
        return Reorder(raw, order);
    }

    internal static ushort[] FromBytes(byte[] bigEndian, ByteOrder order)
    {
        var raw = Reorder(bigEndian, order); // перестановка симметрична
        var regs = new ushort[raw.Length / 2];
        for (var i = 0; i < regs.Length; i++)
            regs[i] = BinaryPrimitives.ReadUInt16BigEndian(raw.AsSpan(i * 2));
        return regs;
    }

    private static byte[] Reorder(byte[] b, ByteOrder order)
    {
        var r = (byte[])b.Clone();
        var words = b.Length / 2;
        for (var w = 0; w < words; w++)
        {
            // CDAB/DCBA — обратный порядок слов, BADC/DCBA — перестановка байт в слове
            var src = order is ByteOrder.CDAB or ByteOrder.DCBA ? words - 1 - w : w;
            var swap = order is ByteOrder.BADC or ByteOrder.DCBA;
            r[w * 2] = swap ? b[src * 2 + 1] : b[src * 2];
            r[w * 2 + 1] = swap ? b[src * 2] : b[src * 2 + 1];
        }
        return r;
    }
}
