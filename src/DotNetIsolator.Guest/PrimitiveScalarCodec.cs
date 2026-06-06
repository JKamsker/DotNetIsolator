using System.Runtime.InteropServices;

namespace DotNetIsolator.Internal;

// Transports a scalar callback across the boundary through guest memory. Shared by the host and
// guest so both sides agree on the layout.
[StructLayout(LayoutKind.Sequential)]
internal struct ScalarCallInvocation
{
    public long ArgBits;
    public int ArgKind;
    public int ResultKind;
    public long ResultBits;
    public int Error;
}

// Bit-packs primitive scalar values into a 64-bit register, shared by the host and guest sides of
// the scalar callback fast path. Element-kind tags match the native element_class_for_kind switch.
internal static class PrimitiveScalarCodec
{
    public const int None = 0;
    public const int Boolean = 1;
    public const int SByte = 2;
    public const int Byte = 3;
    public const int Int16 = 4;
    public const int UInt16 = 5;
    public const int Char = 6;
    public const int Int32 = 7;
    public const int UInt32 = 8;
    public const int Int64 = 9;
    public const int UInt64 = 10;
    public const int Single = 11;
    public const int Double = 12;

    public static int GetKind(Type type)
        => type == typeof(bool) ? Boolean
        : type == typeof(sbyte) ? SByte
        : type == typeof(byte) ? Byte
        : type == typeof(short) ? Int16
        : type == typeof(ushort) ? UInt16
        : type == typeof(char) ? Char
        : type == typeof(int) ? Int32
        : type == typeof(uint) ? UInt32
        : type == typeof(long) ? Int64
        : type == typeof(ulong) ? UInt64
        : type == typeof(float) ? Single
        : type == typeof(double) ? Double
        : None;

    public static long Pack(object value, int kind)
        => kind switch
        {
            Boolean => (bool)value ? 1L : 0L,
            SByte => (byte)(sbyte)value,
            Byte => (byte)value,
            Int16 => (ushort)(short)value,
            UInt16 => (ushort)value,
            Char => (char)value,
            Int32 => (uint)(int)value,
            UInt32 => (uint)value,
            Int64 => (long)value,
            UInt64 => unchecked((long)(ulong)value),
            Single => BitConverter.SingleToUInt32Bits((float)value),
            Double => BitConverter.DoubleToInt64Bits((double)value),
            _ => throw new InvalidOperationException($"Unknown primitive scalar kind {kind}."),
        };

    public static object Unpack(long bits, int kind)
        => kind switch
        {
            Boolean => bits != 0,
            SByte => unchecked((sbyte)bits),
            Byte => (byte)bits,
            Int16 => (short)bits,
            UInt16 => (ushort)bits,
            Char => (char)bits,
            Int32 => (int)bits,
            UInt32 => (uint)bits,
            Int64 => bits,
            UInt64 => unchecked((ulong)bits),
            Single => BitConverter.UInt32BitsToSingle((uint)bits),
            Double => BitConverter.Int64BitsToDouble(bits),
            _ => throw new InvalidOperationException($"Unknown primitive scalar kind {kind}."),
        };
}
