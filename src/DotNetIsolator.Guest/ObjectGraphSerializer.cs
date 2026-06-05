using System.Collections;
using System.Globalization;
using System.Text;

namespace DotNetIsolator.Internal;

internal static class ObjectGraphSerializer
{
    private const int MaxDepth = 128;
    private static readonly Encoding Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public static byte[] SerializeWithType(object? value)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding);
        WriteTypedValue(writer, value, depth: 0);
        return stream.ToArray();
    }

    public static object? DeserializeWithType(ReadOnlyMemory<byte> value)
    {
        using var stream = new MemoryStream(value.ToArray());
        using var reader = new BinaryReader(stream, Encoding);
        return ReadTypedValue(reader, depth: 0);
    }

    public static byte[] Serialize(Type declaredType, object? value)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding);
        WriteValue(writer, declaredType, value, depth: 0);
        return stream.ToArray();
    }

    public static object? Deserialize(Type declaredType, ReadOnlyMemory<byte> value)
    {
        using var stream = new MemoryStream(value.ToArray());
        using var reader = new BinaryReader(stream, Encoding);
        return ReadValue(reader, declaredType, depth: 0);
    }

    private static void WriteValue(BinaryWriter writer, Type declaredType, object? value, int depth)
    {
        EnsureDepth(depth);
        writer.Write(value is not null);
        if (value is null)
        {
            return;
        }

        var type = ObjectGraphTypes.UnwrapNullable(declaredType);
        if (ObjectGraphTypes.NeedsRuntimeType(type))
        {
            WriteTypedValue(writer, value, depth + 1);
            return;
        }

        WriteNonNullValue(writer, type, value, depth + 1);
    }

    private static object? ReadValue(BinaryReader reader, Type declaredType, int depth)
    {
        EnsureDepth(depth);
        if (!reader.ReadBoolean())
        {
            return null;
        }

        var type = ObjectGraphTypes.UnwrapNullable(declaredType);
        return ObjectGraphTypes.NeedsRuntimeType(type)
            ? ReadTypedValue(reader, depth + 1)
            : ReadNonNullValue(reader, type, depth + 1);
    }

    private static void WriteTypedValue(BinaryWriter writer, object? value, int depth)
    {
        writer.Write(value is not null);
        if (value is null)
        {
            return;
        }

        var type = value.GetType();
        ObjectGraphTypes.WriteType(writer, type);
        WriteNonNullValue(writer, type, value, depth + 1);
    }

    private static object? ReadTypedValue(BinaryReader reader, int depth)
    {
        if (!reader.ReadBoolean())
        {
            return null;
        }

        return ReadNonNullValue(reader, ObjectGraphTypes.ReadType(reader), depth + 1);
    }

    private static void WriteNonNullValue(BinaryWriter writer, Type type, object value, int depth)
    {
        type = ObjectGraphTypes.UnwrapNullable(type);
        if (ObjectGraphPrimitives.TryWrite(writer, type, value))
        {
            return;
        }

        if (type.IsEnum)
        {
            WriteNonNullValue(writer, Enum.GetUnderlyingType(type), Convert.ChangeType(value, Enum.GetUnderlyingType(type), CultureInfo.InvariantCulture)!, depth);
        }
        else if (ObjectGraphTypes.TryGetDictionaryTypes(type, out var keyType, out var valueType))
        {
            WriteDictionary(writer, value, keyType, valueType, depth);
        }
        else if (ObjectGraphTypes.TryGetCollectionElementType(type, out var elementType))
        {
            WriteCollection(writer, value, elementType, depth);
        }
        else
        {
            WriteObject(writer, type, value, depth);
        }
    }

    private static object ReadNonNullValue(BinaryReader reader, Type type, int depth)
    {
        type = ObjectGraphTypes.UnwrapNullable(type);
        if (ObjectGraphPrimitives.TryRead(reader, type, out var primitive))
        {
            return primitive!;
        }

        if (type.IsEnum)
        {
            return Enum.ToObject(type, ReadNonNullValue(reader, Enum.GetUnderlyingType(type), depth));
        }

        if (ObjectGraphTypes.TryGetDictionaryTypes(type, out var keyType, out var valueType))
        {
            return ReadDictionary(reader, keyType, valueType, depth);
        }

        if (ObjectGraphTypes.TryGetCollectionElementType(type, out var elementType))
        {
            return ReadCollection(reader, type, elementType, depth);
        }

        return ReadObject(reader, type, depth);
    }

    private static void WriteDictionary(BinaryWriter writer, object value, Type keyType, Type valueType, int depth)
    {
        if (value is IDictionary dictionary)
        {
            writer.Write(dictionary.Count);
            foreach (DictionaryEntry entry in dictionary)
            {
                WriteValue(writer, keyType, entry.Key, depth + 1);
                WriteValue(writer, valueType, entry.Value, depth + 1);
            }

            return;
        }

#pragma warning disable IL2075
        // Fallback for dictionary implementations that only expose generic KeyValuePair entries.
        var entries = ((IEnumerable)value).Cast<object>().ToArray();
        writer.Write(entries.Length);
        foreach (var entry in entries)
        {
            var entryType = entry.GetType();
            WriteValue(writer, keyType, entryType.GetProperty("Key")!.GetValue(entry), depth + 1);
            WriteValue(writer, valueType, entryType.GetProperty("Value")!.GetValue(entry), depth + 1);
        }
#pragma warning restore IL2075
    }

    private static object ReadDictionary(BinaryReader reader, Type keyType, Type valueType, int depth)
    {
        var dictionary = (IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(keyType, valueType))!;
        var count = reader.ReadInt32();
        for (var i = 0; i < count; i++)
        {
            dictionary.Add(ReadValue(reader, keyType, depth + 1)!, ReadValue(reader, valueType, depth + 1));
        }

        return dictionary;
    }

    private static void WriteCollection(BinaryWriter writer, object value, Type elementType, int depth)
    {
        var values = ((IEnumerable)value).Cast<object?>().ToArray();
        writer.Write(values.Length);
        foreach (var item in values)
        {
            WriteValue(writer, elementType, item, depth + 1);
        }
    }

    private static object ReadCollection(BinaryReader reader, Type type, Type elementType, int depth)
    {
        var count = reader.ReadInt32();
        var values = Array.CreateInstance(elementType, count);
        for (var i = 0; i < count; i++)
        {
            values.SetValue(ReadValue(reader, elementType, depth + 1), i);
        }

        if (type.IsArray)
        {
            return values;
        }

        var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(elementType))!;
        foreach (var item in values)
        {
            list.Add(item);
        }

        return list;
    }

    private static void WriteObject(BinaryWriter writer, Type type, object value, int depth)
    {
        var members = ObjectGraphTypes.GetSerializableMembers(type);
        writer.Write(members.Length);
        foreach (var member in members)
        {
            writer.Write(member.Name);
            ObjectGraphTypes.WriteType(writer, member.Type);
            WriteValue(writer, member.Type, member.GetValue(value), depth + 1);
        }
    }

    private static object ReadObject(BinaryReader reader, Type type, int depth)
    {
        var result = ObjectGraphTypes.CreateObject(type);
        var members = ObjectGraphTypes.GetSerializableMembers(type).ToDictionary(m => m.Name, StringComparer.Ordinal);
        var count = reader.ReadInt32();
        for (var i = 0; i < count; i++)
        {
            var memberName = reader.ReadString();
            var serializedType = ObjectGraphTypes.ReadType(reader);
            var value = ReadValue(reader, serializedType, depth + 1);
            if (members.TryGetValue(memberName, out var member) && IsAssignable(member.Type, value))
            {
                member.SetValue(result, value);
            }
        }

        return result;
    }

    private static bool IsAssignable(Type type, object? value)
        => value is null || ObjectGraphTypes.UnwrapNullable(type).IsInstanceOfType(value);

    private static void EnsureDepth(int depth)
    {
        if (depth > MaxDepth)
        {
            throw new InvalidOperationException($"Object graph exceeds the maximum supported depth of {MaxDepth}.");
        }
    }
}
