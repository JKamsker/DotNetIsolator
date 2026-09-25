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
    public int ArgumentIsList;
    public int ResultKind; // 0 serialized, -1 void, -2 array, positive scalar kind
    public int ResultElementKind;
    public int Reserved; // align ResultBits to eight bytes on both sides
    public long ResultBits;
}
