using System.Runtime.CompilerServices;

namespace PerformanceSample;

internal static class MeasurementSink
{
    public static long Value { get; private set; }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Consume(long value)
        => Value = value;
}
