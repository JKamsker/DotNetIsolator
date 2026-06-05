using System.Runtime.CompilerServices;

namespace PerformanceSample;

public sealed class BenchmarkTarget
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public int Increment(int value)
        => unchecked((value * 31) + 7);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public int ReturnFixedValue()
        => 42;
}
