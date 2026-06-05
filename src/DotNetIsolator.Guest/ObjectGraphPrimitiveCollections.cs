using System.Runtime.InteropServices;

namespace DotNetIsolator.Internal;

internal static class ObjectGraphPrimitiveCollections
{
    private const int Int32CollectionMarker = -1;

    public static bool TryWrite(BinaryWriter writer, object value, Type elementType)
    {
        if (elementType != typeof(int))
        {
            return false;
        }

        if (value is int[] array)
        {
            WriteInt32Collection(writer, array);
            return true;
        }

        if (value is List<int> list)
        {
            WriteInt32Collection(writer, CollectionsMarshal.AsSpan(list));
            return true;
        }

        if (value is ICollection<int> collection)
        {
            writer.Write(Int32CollectionMarker);
            writer.Write(collection.Count);
            foreach (var item in collection)
            {
                writer.Write(item);
            }

            return true;
        }

        return false;
    }

    public static bool TryRead(BinaryReader reader, Type collectionType, Type elementType, int markerOrCount, out object? value)
    {
        value = null;
        if (markerOrCount != Int32CollectionMarker)
        {
            return false;
        }

        if (elementType != typeof(int))
        {
            throw new InvalidOperationException("Primitive collection marker was used for a non-int collection.");
        }

        var values = ReadInt32Array(reader);
        value = collectionType.IsArray ? values : new List<int>(values);
        return true;
    }

    private static void WriteInt32Collection(BinaryWriter writer, ReadOnlySpan<int> values)
    {
        writer.Write(Int32CollectionMarker);
        writer.Write(values.Length);

        if (!BitConverter.IsLittleEndian)
        {
            foreach (var value in values)
            {
                writer.Write(value);
            }

            return;
        }

        writer.Write(MemoryMarshal.AsBytes(values));
    }

    private static int[] ReadInt32Array(BinaryReader reader)
    {
        var count = reader.ReadInt32();
        EnsureCount(count);
        if (count == 0)
        {
            return [];
        }

        if (!BitConverter.IsLittleEndian)
        {
            var nonLittleEndianValues = new int[count];
            for (var i = 0; i < nonLittleEndianValues.Length; i++)
            {
                nonLittleEndianValues[i] = reader.ReadInt32();
            }

            return nonLittleEndianValues;
        }

        var values = new int[count];
        var byteCount = checked(count * sizeof(int));
        var bytes = reader.ReadBytes(byteCount);
        if (bytes.Length != byteCount)
        {
            throw new EndOfStreamException("Primitive collection payload ended before all elements were read.");
        }

        Buffer.BlockCopy(bytes, 0, values, 0, byteCount);
        return values;
    }

    public static void EnsureCount(int count)
    {
        if (count < 0)
        {
            throw new InvalidOperationException("Collection payload contained a negative element count.");
        }
    }
}
