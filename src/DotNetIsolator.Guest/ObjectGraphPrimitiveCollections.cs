using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DotNetIsolator.Internal;

// Bulk codec for collections whose elements are blittable primitive value types.
//
// Such collections are transferred as a single contiguous little-endian memory block
// instead of being written and read element by element through the recursive
// object-graph path. For large arrays this turns thousands of per-element virtual
// reader/writer calls (each very expensive inside the interpreted guest runtime) into
// one bounded memory copy on each side.
//
// This file is compiled into both the host and the guest, so both sides always agree
// on the wire format.
internal static class ObjectGraphPrimitiveCollections
{
    private const int StackBufferByteThreshold = 1024;

    // Written in place of a normal element count to mark a bulk primitive blob. A real
    // element count is always >= 0, so a negative sentinel is unambiguous.
    private const int PrimitiveBlobMarker = -1;

    // Stable element-kind tags. These are part of the wire format; do not renumber.
    private enum Kind : byte
    {
        Boolean = 1,
        SByte = 2,
        Byte = 3,
        Int16 = 4,
        UInt16 = 5,
        Char = 6,
        Int32 = 7,
        UInt32 = 8,
        Int64 = 9,
        UInt64 = 10,
        Single = 11,
        Double = 12,
    }

    public static bool TryWrite(BinaryWriter writer, object value, Type elementType)
    {
        // The bulk format stores raw, host-order element bytes. On a (currently unsupported)
        // big-endian host, fall back to the per-element path so the existing byte-swapping
        // behavior is preserved. The guest runtime is always little-endian.
        if (!BitConverter.IsLittleEndian)
        {
            return false;
        }

        return elementType == typeof(int) ? TryWriteBlittable<int>(writer, value, Kind.Int32)
            : elementType == typeof(double) ? TryWriteBlittable<double>(writer, value, Kind.Double)
            : elementType == typeof(long) ? TryWriteBlittable<long>(writer, value, Kind.Int64)
            : elementType == typeof(float) ? TryWriteBlittable<float>(writer, value, Kind.Single)
            : elementType == typeof(short) ? TryWriteBlittable<short>(writer, value, Kind.Int16)
            : elementType == typeof(ushort) ? TryWriteBlittable<ushort>(writer, value, Kind.UInt16)
            : elementType == typeof(char) ? TryWriteBlittable<char>(writer, value, Kind.Char)
            : elementType == typeof(uint) ? TryWriteBlittable<uint>(writer, value, Kind.UInt32)
            : elementType == typeof(ulong) ? TryWriteBlittable<ulong>(writer, value, Kind.UInt64)
            : elementType == typeof(byte) ? TryWriteBlittable<byte>(writer, value, Kind.Byte)
            : elementType == typeof(sbyte) ? TryWriteBlittable<sbyte>(writer, value, Kind.SByte)
            : elementType == typeof(bool) && TryWriteBlittable<bool>(writer, value, Kind.Boolean);
    }

    public static bool TryRead(BinaryReader reader, Type collectionType, Type elementType, int markerOrCount, out object? value)
    {
        value = null;
        if (markerOrCount != PrimitiveBlobMarker)
        {
            return false;
        }

        var kind = (Kind)reader.ReadByte();
        var count = reader.ReadInt32();
        EnsureCount(count);

        // Dispatch on the trusted expected element type; the wire kind is only a
        // consistency check so a corrupt or malicious payload cannot reinterpret the
        // declared element type.
        value = elementType == typeof(int) ? ReadBlittable<int>(reader, collectionType, count, kind, Kind.Int32)
            : elementType == typeof(double) ? ReadBlittable<double>(reader, collectionType, count, kind, Kind.Double)
            : elementType == typeof(long) ? ReadBlittable<long>(reader, collectionType, count, kind, Kind.Int64)
            : elementType == typeof(float) ? ReadBlittable<float>(reader, collectionType, count, kind, Kind.Single)
            : elementType == typeof(short) ? ReadBlittable<short>(reader, collectionType, count, kind, Kind.Int16)
            : elementType == typeof(ushort) ? ReadBlittable<ushort>(reader, collectionType, count, kind, Kind.UInt16)
            : elementType == typeof(char) ? ReadBlittable<char>(reader, collectionType, count, kind, Kind.Char)
            : elementType == typeof(uint) ? ReadBlittable<uint>(reader, collectionType, count, kind, Kind.UInt32)
            : elementType == typeof(ulong) ? ReadBlittable<ulong>(reader, collectionType, count, kind, Kind.UInt64)
            : elementType == typeof(byte) ? ReadBlittable<byte>(reader, collectionType, count, kind, Kind.Byte)
            : elementType == typeof(sbyte) ? ReadBlittable<sbyte>(reader, collectionType, count, kind, Kind.SByte)
            : elementType == typeof(bool) ? ReadBlittable<bool>(reader, collectionType, count, kind, Kind.Boolean)
            : throw new InvalidOperationException($"A primitive collection marker was used for the unsupported element type '{elementType}'.");

        return true;
    }

    private static bool TryWriteBlittable<T>(BinaryWriter writer, object value, Kind kind) where T : unmanaged
    {
        if (value is T[] array)
        {
            WriteBlob(writer, kind, array);
            return true;
        }

        if (value is List<T> list)
        {
            WriteBlob(writer, kind, CollectionsMarshal.AsSpan(list));
            return true;
        }

        if (value is ICollection<T> collection)
        {
            WriteCollectionBlob(writer, kind, collection);
            return true;
        }

        return false;
    }

    private static void WriteCollectionBlob<T>(BinaryWriter writer, Kind kind, ICollection<T> collection) where T : unmanaged
    {
        var count = collection.Count;
        EnsureCount(count);
        if (count == 0)
        {
            WriteBlob(writer, kind, ReadOnlySpan<T>.Empty);
            return;
        }

        var byteCount = checked(count * Unsafe.SizeOf<T>());
        if (byteCount <= StackBufferByteThreshold)
        {
            Span<T> buffer = stackalloc T[count];
            CopyCollectionTo(collection, buffer);
            WriteBlob(writer, kind, buffer);
            return;
        }

        var rented = ArrayPool<T>.Shared.Rent(count);
        try
        {
            collection.CopyTo(rented, 0);
            WriteBlob(writer, kind, rented.AsSpan(0, count));
        }
        finally
        {
            ArrayPool<T>.Shared.Return(rented);
        }
    }

    private static void CopyCollectionTo<T>(IEnumerable<T> collection, Span<T> destination)
    {
        var index = 0;
        foreach (var item in collection)
        {
            if ((uint)index >= (uint)destination.Length)
            {
                throw new InvalidOperationException("Collection count changed during serialization.");
            }

            destination[index++] = item;
        }

        if (index != destination.Length)
        {
            throw new InvalidOperationException("Collection count changed during serialization.");
        }
    }

    private static void WriteBlob<T>(BinaryWriter writer, Kind kind, ReadOnlySpan<T> values) where T : unmanaged
    {
        writer.Write(PrimitiveBlobMarker);
        writer.Write((byte)kind);
        writer.Write(values.Length);
        writer.Write(MemoryMarshal.AsBytes(values));
    }

    private static object ReadBlittable<T>(BinaryReader reader, Type collectionType, int count, Kind actualKind, Kind expectedKind) where T : unmanaged
    {
        if (actualKind != expectedKind)
        {
            throw new InvalidOperationException(
                $"Primitive collection element kind {actualKind} does not match the expected element type '{typeof(T)}'.");
        }

        if (collectionType.IsArray)
        {
            var values = count == 0 ? Array.Empty<T>() : new T[count];
            ReadBlittableValues(reader, values);
            return values;
        }

        var list = new List<T>(count);
        if (count > 0)
        {
            CollectionsMarshal.SetCount(list, count);
            ReadBlittableValues(reader, CollectionsMarshal.AsSpan(list));
        }

        return list;
    }

    private static void ReadBlittableValues<T>(BinaryReader reader, Span<T> values) where T : unmanaged
    {
        if (values.Length == 0)
        {
            return;
        }

        var destination = MemoryMarshal.AsBytes(values);
        reader.BaseStream.ReadExactly(destination);

        if (!BitConverter.IsLittleEndian && Unsafe.SizeOf<T>() > 1)
        {
            ReverseElementBytes(values);
        }
    }

    private static void ReverseElementBytes<T>(Span<T> values) where T : unmanaged
    {
        var size = Unsafe.SizeOf<T>();
        var bytes = MemoryMarshal.AsBytes(values);
        for (var offset = 0; offset < bytes.Length; offset += size)
        {
            bytes.Slice(offset, size).Reverse();
        }
    }

    public static void EnsureCount(int count)
    {
        if (count < 0)
        {
            throw new InvalidOperationException("Collection payload contained a negative element count.");
        }
    }
}
