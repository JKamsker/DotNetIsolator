namespace DotNetIsolator.Internal;

internal static class ObjectGraphPrimitives
{
    public static bool TryWrite(BinaryWriter writer, Type type, object value)
    {
        if (type == typeof(string)) writer.Write((string)value);
        else if (type == typeof(bool)) writer.Write((bool)value);
        else if (type == typeof(byte)) writer.Write((byte)value);
        else if (type == typeof(sbyte)) writer.Write((sbyte)value);
        else if (type == typeof(short)) writer.Write((short)value);
        else if (type == typeof(ushort)) writer.Write((ushort)value);
        else if (type == typeof(int)) writer.Write((int)value);
        else if (type == typeof(uint)) writer.Write((uint)value);
        else if (type == typeof(long)) writer.Write((long)value);
        else if (type == typeof(ulong)) writer.Write((ulong)value);
        else if (type == typeof(float)) writer.Write((float)value);
        else if (type == typeof(double)) writer.Write((double)value);
        else if (type == typeof(char)) writer.Write((char)value);
        else if (type == typeof(byte[])) WriteBytes(writer, (byte[])value);
        else if (type == typeof(DateTime)) writer.Write(((DateTime)value).ToBinary());
        else if (type == typeof(TimeSpan)) writer.Write(((TimeSpan)value).Ticks);
        else if (type == typeof(Guid)) WriteGuid(writer, (Guid)value);
        else if (type == typeof(decimal)) WriteDecimal(writer, (decimal)value);
        else return false;

        return true;
    }

    public static bool TryRead(BinaryReader reader, Type type, out object? value)
    {
        value = type == typeof(string) ? reader.ReadString()
            : type == typeof(bool) ? reader.ReadBoolean()
            : type == typeof(byte) ? reader.ReadByte()
            : type == typeof(sbyte) ? reader.ReadSByte()
            : type == typeof(short) ? reader.ReadInt16()
            : type == typeof(ushort) ? reader.ReadUInt16()
            : type == typeof(int) ? reader.ReadInt32()
            : type == typeof(uint) ? reader.ReadUInt32()
            : type == typeof(long) ? reader.ReadInt64()
            : type == typeof(ulong) ? reader.ReadUInt64()
            : type == typeof(float) ? reader.ReadSingle()
            : type == typeof(double) ? reader.ReadDouble()
            : type == typeof(char) ? reader.ReadChar()
            : type == typeof(byte[]) ? ReadByteArray(reader)
            : type == typeof(DateTime) ? DateTime.FromBinary(reader.ReadInt64())
            : type == typeof(TimeSpan) ? new TimeSpan(reader.ReadInt64())
            : type == typeof(Guid) ? ReadGuid(reader)
            : type == typeof(decimal) ? ReadDecimal(reader)
            : null;

        return value is not null || type == typeof(string);
    }

    private static void WriteBytes(BinaryWriter writer, byte[] value)
    {
        writer.Write(value.Length);
        writer.Write(value);
    }

    private static byte[] ReadByteArray(BinaryReader reader)
    {
        var length = reader.ReadInt32();
        if (length < 0)
        {
            throw new InvalidDataException("Byte array length cannot be negative.");
        }

        var bytes = new byte[length];
        ReadExactly(reader, bytes);
        return bytes;
    }

    private static void WriteGuid(BinaryWriter writer, Guid value)
    {
        Span<byte> bytes = stackalloc byte[16];
        if (!value.TryWriteBytes(bytes))
        {
            throw new InvalidOperationException("Could not serialize Guid bytes.");
        }

        writer.Write(bytes);
    }

    private static Guid ReadGuid(BinaryReader reader)
    {
        Span<byte> bytes = stackalloc byte[16];
        ReadExactly(reader, bytes);
        return new Guid(bytes);
    }

    private static void WriteDecimal(BinaryWriter writer, decimal value)
    {
        Span<int> parts = stackalloc int[4];
        decimal.GetBits(value, parts);
        foreach (var part in parts)
        {
            writer.Write(part);
        }
    }

    private static decimal ReadDecimal(BinaryReader reader)
    {
        var lo = reader.ReadInt32();
        var mid = reader.ReadInt32();
        var hi = reader.ReadInt32();
        var flags = reader.ReadInt32();
        var isNegative = (flags & unchecked((int)0x80000000)) != 0;
        var scale = (byte)((flags >> 16) & 0xFF);
        return new decimal(lo, mid, hi, isNegative, scale);
    }

    internal static void ReadExactly(BinaryReader reader, Span<byte> destination)
    {
        var totalRead = 0;
        while (totalRead < destination.Length)
        {
            var bytesRead = reader.Read(destination[totalRead..]);
            if (bytesRead == 0)
            {
                throw new EndOfStreamException();
            }

            totalRead += bytesRead;
        }
    }
}
