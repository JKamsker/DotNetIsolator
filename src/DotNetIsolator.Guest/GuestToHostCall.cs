namespace DotNetIsolator.Internal;

#pragma warning disable CS0649

public struct GuestToHostCall
{
    public string CallbackName;
    public byte[]?[] Args;
    public bool IsRawCall; // Means the args are not serialized - they are raw byte arrays.
    // Args may be an oversized rented buffer; ArgsLength is the logical arity.
    public int ArgsLength;
}

#pragma warning restore CS0649
