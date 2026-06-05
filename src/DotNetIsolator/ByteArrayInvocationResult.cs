using System.Runtime.InteropServices;

namespace DotNetIsolator;

[StructLayout(LayoutKind.Sequential)]
internal struct ByteArrayInvocationResult
{
    public int Data;
    public int Length;
    public int ResultGCHandle;
    public int ErrorMessage;
}
