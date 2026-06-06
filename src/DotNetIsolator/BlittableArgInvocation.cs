using System.Runtime.InteropServices;

namespace DotNetIsolator;

[StructLayout(LayoutKind.Sequential)]
internal struct BlittableArgInvocation
{
    public int Target;
    public int MethodPtr;
    public int ArgData;
    public int ArgLength;
    public int ArgElementSize;
    public int ArgElementKind;
    public int ResultException;
    public int ResultSerialized;
    public int ResultSerializedLength;
    public int ResultSerializedGCHandle;
}
