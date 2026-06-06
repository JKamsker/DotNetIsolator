using System.Runtime.CompilerServices;
using DotNetIsolator;

namespace PerformanceSample;

public sealed class BenchmarkTarget
{
    public const int BlittableArrayLength = 32_768;

    private readonly byte[] _buffer = CreateBuffer(4096);
    private readonly byte[] _largeBuffer = CreateBuffer(64 * 1024);
    private readonly List<int> _numbers = CreateNumbers();
    private readonly double[] _doubles = CreateDoubles(BlittableArrayLength);
    private readonly long[] _longs = CreateLongs(BlittableArrayLength);
    private readonly List<double> _doubleList = new(CreateDoubles(BlittableArrayLength));
    private int _consumedValue;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public int Increment(int value)
        => unchecked((value * 31) + 7);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public int ReturnFixedValue()
        => 42;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public void Noop()
    {
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public void ConsumeInt(int value)
    {
        _consumedValue = value;
    }

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
    public double[] ReturnDoubles()
        => _doubles;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public long[] ReturnLongs()
        => _longs;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public List<double> ReturnDoubleList()
        => _doubleList;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public double SumDoubles(double[] values)
    {
        var sum = 0.0;
        foreach (var value in values)
        {
            sum += value;
        }

        return sum;
    }

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

    private static double[] CreateDoubles(int length)
    {
        var values = new double[length];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = i * 1.5;
        }

        return values;
    }

    private static long[] CreateLongs(int length)
    {
        var values = new long[length];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = (long)i * 1_000_003;
        }

        return values;
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
