using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace DotNetIsolator.Internal;

internal static class ObjectGraphSerializer
{
    private const int MaxDepth = 128;
    private static readonly Encoding Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    // Serialization is synchronous and non-reentrant on a given thread, so reuse one writer per
    // thread to avoid allocating a MemoryStream + BinaryWriter and regrowing the buffer on every
    // call. The guest is single-threaded; the host gets one writer per worker thread.
    [ThreadStatic] private static MemoryStream? _writeStream;
    [ThreadStatic] private static BinaryWriter? _writeWriter;

    public static byte[] SerializeWithType(object? value)
    {
        var (stream, writer) = GetThreadWriter();
        WriteTypedValue(writer, value, depth: 0);
        writer.Flush();
        return stream.ToArray();
    }

    public static object? DeserializeWithType(ReadOnlyMemory<byte> value)
    {
        using var stream = CreateReadStream(value);
        using var reader = new BinaryReader(stream, Encoding);
        return ReadTypedValue(reader, depth: 0);
    }

    public static object? DeserializeWithType(Stream stream)
    {
        using var reader = new BinaryReader(stream, Encoding, leaveOpen: true);
        return ReadTypedValue(reader, depth: 0);
    }

    public static byte[] Serialize(Type declaredType, object? value)
    {
        var (stream, writer) = GetThreadWriter();
        WriteValue(writer, declaredType, value, depth: 0);
        writer.Flush();
        return stream.ToArray();
    }

    private static (MemoryStream Stream, BinaryWriter Writer) GetThreadWriter()
    {
        var stream = _writeStream;
        if (stream is null)
        {
            stream = new MemoryStream(256);
            _writeStream = stream;
            _writeWriter = new BinaryWriter(stream, Encoding);
        }

        stream.SetLength(0);
        return (stream, _writeWriter!);
    }

    public static object? Deserialize(Type declaredType, ReadOnlyMemory<byte> value)
    {
        using var stream = CreateReadStream(value);
        using var reader = new BinaryReader(stream, Encoding);
        return ReadValue(reader, declaredType, depth: 0);
    }

    public static object? Deserialize(Type declaredType, Stream stream)
    {
        using var reader = new BinaryReader(stream, Encoding, leaveOpen: true);
        return ReadValue(reader, declaredType, depth: 0);
    }

    private static MemoryStream CreateReadStream(ReadOnlyMemory<byte> value)
    {
        if (MemoryMarshal.TryGetArray(value, out var segment) && segment.Array is not null)
        {
            return new MemoryStream(segment.Array, segment.Offset, segment.Count, writable: false);
        }

        return new MemoryStream(value.ToArray());
    }

    internal static void WriteValue(BinaryWriter writer, Type declaredType, object? value, int depth)
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

    internal static object? ReadValue(BinaryReader reader, Type declaredType, int depth)
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
            ObjectGraphCollections.WriteDictionary(writer, value, keyType, valueType, depth);
        }
        else if (ObjectGraphTypes.TryGetCollectionElementType(type, out var elementType))
        {
            ObjectGraphCollections.WriteCollection(writer, value, elementType, depth);
        }
        else
        {
            ObjectGraphObjectSerializer.Write(writer, type, value, depth);
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
            return ObjectGraphCollections.ReadDictionary(reader, keyType, valueType, depth);
        }

        if (ObjectGraphTypes.TryGetCollectionElementType(type, out var elementType))
        {
            return ObjectGraphCollections.ReadCollection(reader, type, elementType, depth);
        }

        return ObjectGraphObjectSerializer.Read(reader, type, depth);
    }

    private static void EnsureDepth(int depth)
    {
        if (depth > MaxDepth)
        {
            throw new InvalidOperationException($"Object graph exceeds the maximum supported depth of {MaxDepth}.");
        }
    }
}
