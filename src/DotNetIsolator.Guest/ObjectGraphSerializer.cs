using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace DotNetIsolator.Internal;

internal static class ObjectGraphSerializer
{
    private const int MaxDepth = 128;
    private static readonly Encoding Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    // A writer is removed from the cache while borrowed, including during user getters.
    // Reentrant serialization therefore gets independent storage.
    [ThreadStatic] private static WriterState? _availableWriter;

    private sealed class WriterState
    {
        public readonly MemoryStream Stream = new(256);
        public readonly BinaryWriter Writer;
        public WriterState() => Writer = new BinaryWriter(Stream, Encoding, leaveOpen: true);
    }

    internal sealed class BufferLease : IDisposable
    {
        private WriterState? _state;
        public byte[] Buffer { get; }
        public int Length { get; }
        public ReadOnlySpan<byte> Span => Buffer.AsSpan(0, Length);

        private BufferLease(WriterState state)
        {
            _state = state;
            Buffer = state.Stream.GetBuffer();
            Length = checked((int)state.Stream.Length);
        }

        public void Dispose()
        {
            var state = _state;
            _state = null;
            if (state is not null && _availableWriter is null) _availableWriter = state;
        }

        internal static BufferLease Write(Type? type, object? value, bool includeType)
        {
            var state = _availableWriter ?? new WriterState();
            _availableWriter = null;
            state.Stream.SetLength(0);
            try
            {
                if (includeType) WriteTypedValue(state.Writer, value, 0);
                else WriteValue(state.Writer, type!, value, 0);
                state.Writer.Flush();
                return new BufferLease(state);
            }
            catch
            {
                if (_availableWriter is null) _availableWriter = state;
                throw;
            }
        }
    }

    public static BufferLease SerializeWithTypeBuffer(object? value) => BufferLease.Write(null, value, true);
    public static BufferLease SerializeBuffer(Type type, object? value) => BufferLease.Write(type, value, false);

    public static byte[] SerializeWithType(object? value)
    {
        using var buffer = SerializeWithTypeBuffer(value);
        return buffer.Span.ToArray();
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
        using var buffer = SerializeBuffer(declaredType, value);
        return buffer.Span.ToArray();
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

    internal static void EnsureDepth(int depth)
    {
        if (depth > MaxDepth)
        {
            throw new InvalidOperationException($"Object graph exceeds the maximum supported depth of {MaxDepth}.");
        }
    }
}
