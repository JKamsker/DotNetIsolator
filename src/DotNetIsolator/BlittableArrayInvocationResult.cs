using System.Runtime.InteropServices;

namespace DotNetIsolator;

[StructLayout(LayoutKind.Sequential)]
internal struct BlittableArrayInvocationResult
{
    public int Data;
    public int Length;
    public int ElementSize;
    public int ResultGCHandle;
    public int ErrorMessage;
}
