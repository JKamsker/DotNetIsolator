using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;

namespace DotNetIsolator.Internal;

internal static class ObjectGraphCollections
{
    // Counts arrive in serialized data. Limit eager allocation before any elements are read.
    private const int MaxInitialCapacity = 4096;
    private static readonly ConcurrentDictionary<Type, Func<int, object>> CollectionFactories = new();

    private static object CreateWithCapacity(Type type, int count)
        => CollectionFactories.GetOrAdd(type, static collectionType =>
        {
            var name = collectionType.GetGenericTypeDefinition() == typeof(List<>) ? nameof(CreateList) : nameof(CreateDictionary);
            return typeof(ObjectGraphCollections).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!
                .MakeGenericMethod(collectionType.GetGenericArguments())
                .CreateDelegate<Func<int, object>>();
        })(Math.Min(count, MaxInitialCapacity));

    private static object CreateList<T>(int capacity) => new List<T>(capacity);
    private static object CreateDictionary<TKey, TValue>(int capacity) where TKey : notnull => new Dictionary<TKey, TValue>(capacity);

    private static readonly ConcurrentDictionary<(Type Key, Type Value), Action<BinaryWriter, object, int>> DictionaryWriters = new();

    public static void WriteDictionary(BinaryWriter writer, object value, Type keyType, Type valueType, int depth)
        => DictionaryWriters.GetOrAdd((keyType, valueType), static types =>
            typeof(ObjectGraphCollections).GetMethod(nameof(WriteDictionaryTyped), BindingFlags.NonPublic | BindingFlags.Static)!
                .MakeGenericMethod(types.Key, types.Value)
                .CreateDelegate<Action<BinaryWriter, object, int>>())(writer, value, depth);

    private static void WriteDictionaryTyped<TKey, TValue>(BinaryWriter writer, object value, int depth)
    {
        // The shape resolver accepts IDictionary<TKey,TValue> and IReadOnlyDictionary<TKey,TValue>;
        // both expose this typed enumeration. Avoid boxed DictionaryEntry/KeyValuePair objects.
        var countPosition = BeginCountPatch(writer);
        var count = 0;
        foreach (var entry in (IEnumerable<KeyValuePair<TKey, TValue>>)value)
        {
            ObjectGraphSerializer.WriteValue(writer, typeof(TKey), entry.Key, depth + 1);
            ObjectGraphSerializer.WriteValue(writer, typeof(TValue), entry.Value, depth + 1);
            count = checked(count + 1);
        }
        EndCountPatch(writer, countPosition, count);
    }

    public static object ReadDictionary(BinaryReader reader, Type keyType, Type valueType, int depth)
    {
        var count = reader.ReadInt32();
        ObjectGraphPrimitiveCollections.EnsureCount(count);
        var dictionary = (IDictionary)CreateWithCapacity(typeof(Dictionary<,>).MakeGenericType(keyType, valueType), count);
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
        if (elementType == typeof(string))
        {
            WriteStrings(writer, (IEnumerable<string?>)value, depth);
            return;
        }
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
        if (elementType == typeof(string)) return ReadStrings(reader, type, count, depth);
        if (type.IsArray)
        {
            var values = Array.CreateInstance(elementType, count);
            for (var i = 0; i < count; i++)
            {
                values.SetValue(ObjectGraphSerializer.ReadValue(reader, elementType, depth + 1), i);
            }

            return values;
        }

        var list = (IList)CreateWithCapacity(typeof(List<>).MakeGenericType(elementType), count);
        for (var i = 0; i < count; i++)
        {
            list.Add(ObjectGraphSerializer.ReadValue(reader, elementType, depth + 1));
        }

        return list;
    }

    // Strings retain the ordinary count / presence / UTF-8 wire format. Their sealed type
    // lets us skip per-element nullable, runtime-type and primitive-shape reflection.
    private static void WriteStrings(BinaryWriter writer, IEnumerable<string?> values, int depth)
    {
        var countPosition = BeginCountPatch(writer);
        var count = 0;
        foreach (var value in values)
        {
            if (count == 0) ObjectGraphSerializer.EnsureDepth(depth + 1);
            writer.Write(value is not null);
            if (value is not null) writer.Write(value);
            count = checked(count + 1);
        }
        EndCountPatch(writer, countPosition, count);
    }

    private static object ReadStrings(BinaryReader reader, Type type, int count, int depth)
    {
        if (count != 0) ObjectGraphSerializer.EnsureDepth(depth + 1);
        if (type.IsArray)
        {
            var result = count == 0 ? Array.Empty<string?>() : new string?[count];
            for (var i = 0; i < count; i++) result[i] = reader.ReadBoolean() ? reader.ReadString() : null;
            return result;
        }
        var list = new List<string?>(Math.Min(count, MaxInitialCapacity));
        for (var i = 0; i < count; i++) list.Add(reader.ReadBoolean() ? reader.ReadString() : null);
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
