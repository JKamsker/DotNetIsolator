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

    public static byte[] Serialize(object value)
    {
        // TODO: Should we really be pinning the result value here, or is it safe to return
        // a MonoObject* to unmanaged code and then use mono_gchandle_new(..., true) from there?
        return MessagePackCompatibility.SerializeObject(value.GetType(), value);
    }
}
