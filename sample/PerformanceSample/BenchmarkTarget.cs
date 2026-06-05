using System.Runtime.CompilerServices;
using DotNetIsolator;

namespace PerformanceSample;

public sealed class BenchmarkTarget
{
    private readonly byte[] _buffer = CreateBuffer(4096);
    private readonly byte[] _largeBuffer = CreateBuffer(64 * 1024);
    private readonly List<int> _numbers = CreateNumbers();

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

    [MethodImpl(MethodImplOptions.NoInlining)]
    public List<int> ReturnNumbers()
        => _numbers;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public int CallIncrementCallback(int value)
        => DotNetIsolatorHost.Invoke<int>("increment-callback", value);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public int CallRawBufferCallback()
        => DotNetIsolatorHost.InvokeRaw("raw-buffer-callback", _largeBuffer).Length;

    private static byte[] CreateBuffer(int length)
    {
        var buffer = new byte[length];
        for (var i = 0; i < buffer.Length; i++)
        {
            buffer[i] = (byte)i;
        }

        return buffer;
    }

    private static List<int> CreateNumbers()
    {
        var numbers = new List<int>(1024);
        for (var i = 0; i < numbers.Capacity; i++)
        {
            numbers.Add(i);
        }

        return numbers;
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
