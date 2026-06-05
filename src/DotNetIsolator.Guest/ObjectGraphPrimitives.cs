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
        else if (type == typeof(Guid)) WriteBytes(writer, ((Guid)value).ToByteArray());
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
            : type == typeof(byte[]) ? reader.ReadBytes(reader.ReadInt32())
            : type == typeof(DateTime) ? DateTime.FromBinary(reader.ReadInt64())
            : type == typeof(TimeSpan) ? new TimeSpan(reader.ReadInt64())
            : type == typeof(Guid) ? new Guid(reader.ReadBytes(16))
            : type == typeof(decimal) ? ReadDecimal(reader)
            : null;

        return value is not null || type == typeof(string);
    }

    private static void WriteBytes(BinaryWriter writer, byte[] value)
    {
        writer.Write(value.Length);
        writer.Write(value);
    }

    private static void WriteDecimal(BinaryWriter writer, decimal value)
    {
        foreach (var part in decimal.GetBits(value))
        {
            writer.Write(part);
        }
    }

    private static decimal ReadDecimal(BinaryReader reader)
        => new(new[] { reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32() });
}
