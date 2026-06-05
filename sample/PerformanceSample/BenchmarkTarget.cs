using System.Runtime.CompilerServices;

namespace PerformanceSample;

public sealed class BenchmarkTarget
{
    private readonly byte[] _buffer = CreateBuffer();

    [MethodImpl(MethodImplOptions.NoInlining)]
    public int Increment(int value)
        => unchecked((value * 31) + 7);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public int ReturnFixedValue()
        => 42;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public byte[] ReturnBuffer()
        => _buffer;

    private static byte[] CreateBuffer()
    {
        var buffer = new byte[4096];
        for (var i = 0; i < buffer.Length; i++)
        {
            buffer[i] = (byte)i;
        }

        return buffer;
    }
}
