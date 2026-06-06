using System.Collections;

namespace DotNetIsolator.Internal;

internal static class ObjectGraphCollections
{
    public static void WriteDictionary(BinaryWriter writer, object value, Type keyType, Type valueType, int depth)
    {
        if (value is IDictionary dictionary)
        {
            writer.Write(dictionary.Count);
            foreach (DictionaryEntry entry in dictionary)
            {
                ObjectGraphSerializer.WriteValue(writer, keyType, entry.Key, depth + 1);
                ObjectGraphSerializer.WriteValue(writer, valueType, entry.Value, depth + 1);
            }

            return;
        }

        // Fallback for dictionary implementations that only expose generic KeyValuePair entries.
        var countPosition = BeginCountPatch(writer);
        var count = 0;
        foreach (var entry in (IEnumerable)value)
        {
            if (entry is null)
            {
                throw new InvalidOperationException("Dictionary enumeration returned a null entry.");
            }

            var accessors = ObjectGraphTypes.GetDictionaryEntryAccessors(entry.GetType());
            ObjectGraphSerializer.WriteValue(writer, keyType, accessors.GetKey(entry), depth + 1);
            ObjectGraphSerializer.WriteValue(writer, valueType, accessors.GetValue(entry), depth + 1);
            count = checked(count + 1);
        }

        EndCountPatch(writer, countPosition, count);
    }

    public static object ReadDictionary(BinaryReader reader, Type keyType, Type valueType, int depth)
    {
        var dictionary = (IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(keyType, valueType))!;
        var count = reader.ReadInt32();
        for (var i = 0; i < count; i++)
        {
            dictionary.Add(
                ObjectGraphSerializer.ReadValue(reader, keyType, depth + 1)!,
                ObjectGraphSerializer.ReadValue(reader, valueType, depth + 1));
        }

        return dictionary;
    }

    public static void WriteCollection(BinaryWriter writer, object value, Type elementType, int depth)
    {
        if (ObjectGraphPrimitiveCollections.TryWrite(writer, value, elementType))
        {
            return;
        }

        if (value is ICollection collection)
        {
            writer.Write(collection.Count);
            foreach (var item in collection)
            {
                ObjectGraphSerializer.WriteValue(writer, elementType, item, depth + 1);
            }

            return;
        }

        var countPosition = BeginCountPatch(writer);
        var count = 0;
        foreach (var item in (IEnumerable)value)
        {
            ObjectGraphSerializer.WriteValue(writer, elementType, item, depth + 1);
            count = checked(count + 1);
        }

        EndCountPatch(writer, countPosition, count);
    }

    public static object ReadCollection(BinaryReader reader, Type type, Type elementType, int depth)
    {
        var count = reader.ReadInt32();
        if (ObjectGraphPrimitiveCollections.TryRead(reader, type, elementType, count, out var primitiveCollection))
        {
            return primitiveCollection!;
        }

        ObjectGraphPrimitiveCollections.EnsureCount(count);
        if (type.IsArray)
        {
            var values = Array.CreateInstance(elementType, count);
            for (var i = 0; i < count; i++)
            {
                values.SetValue(ObjectGraphSerializer.ReadValue(reader, elementType, depth + 1), i);
            }

            return values;
        }

        var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(elementType))!;
        for (var i = 0; i < count; i++)
        {
            list.Add(ObjectGraphSerializer.ReadValue(reader, elementType, depth + 1));
        }

        return list;
    }

    private static long BeginCountPatch(BinaryWriter writer)
    {
        var stream = writer.BaseStream;
        if (!stream.CanSeek)
        {
            throw new InvalidOperationException("Collection serialization requires a seekable stream.");
        }

        var position = stream.Position;
        writer.Write(0);
        return position;
    }

    private static void EndCountPatch(BinaryWriter writer, long countPosition, int count)
    {
        var stream = writer.BaseStream;
        var endPosition = stream.Position;
        stream.Position = countPosition;
        writer.Write(count);
        stream.Position = endPosition;
    }
}
