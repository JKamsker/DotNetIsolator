using DotNetIsolator.Internal;

namespace DotNetIsolator.WasmApp;

public static class Serialization
{
    public static unsafe object? Deserialize(byte* value, int valueLength)
    {
        // Read directly from guest memory instead of copying the whole argument into a managed array.
        using var stream = new UnmanagedMemoryStream(value, valueLength);
        return MessagePackCompatibility.DeserializeTypeless(stream);
    }

    internal static unsafe object? Deserialize(Memory<byte> value)
    {
        // TODO: Instead of using Typeless, consider making this a generic method and having the
        // C code produce the closed type based on the declared parameter types of the method
        var result = MessagePackCompatibility.DeserializeTypeless(value);

        // Console.WriteLine($"Deserialized value of type {result?.GetType().FullName} with value {result}");
        return result;
    }

    // Native code pins Buffer and holds this lease until the host releases its result handle.
    // Logical length is separate from capacity; nested calls cannot reuse an outstanding buffer.
    private static readonly Dictionary<byte[], ObjectGraphSerializer.BufferLease> Results = new();

    public static byte[] Serialize(object value, out int length)
    {
        var lease = ObjectGraphSerializer.SerializeBuffer(value.GetType(), value);
        try { Results.Add(lease.Buffer, lease); }
        catch { lease.Dispose(); throw; }
        length = lease.Length;
        return lease.Buffer;
    }

    public static void Release(byte[] buffer)
    {
        if (Results.Remove(buffer, out var lease)) lease.Dispose();
    }
}
