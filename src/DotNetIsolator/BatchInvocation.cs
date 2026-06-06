using System.Runtime.InteropServices;

namespace DotNetIsolator;

[StructLayout(LayoutKind.Sequential)]
internal struct BatchInvocation
{
    public int Target;
    public int MethodPtr;
    public int Args;
    public int Count;
    public int ArgKind;
    public int ResultKind;
    public int Results;
    public int ErrorMessage;
}
