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

    [MethodImpl(MethodImplOptions.NoInlining)]
    public BenchmarkPayload ReturnPayload()
        => new()
        {
            Id = 42,
            Name = "payload",
            Count = _buffer.Length,
            IsValid = true,
            Checksum = 522_240,
        };

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

public sealed class BenchmarkPayload
{
    public int Id { get; set; }

    public string? Name { get; set; }

    public int Count { get; set; }

    public bool IsValid { get; set; }

    public long Checksum { get; set; }
}
