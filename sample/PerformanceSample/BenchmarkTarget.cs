using System.Runtime.CompilerServices;
using DotNetIsolator;

namespace PerformanceSample;

public sealed class BenchmarkTarget
{
    public const int BlittableArrayLength = 32_768;

    private readonly byte[] _smallBuffer = CreateBuffer(32);
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
    public double IncrementDouble(double value)
        => (value * 1.5) + 1.0;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public long IncrementLong(long value)
        => unchecked((value * 31) + 7);

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
    public PrimitivePayload ReturnPrimitivePayload()
        => new()
        {
            Id = 42,
            Ratio = 3.5,
            Total = 522_240,
            IsValid = true,
            Code = 7,
        };

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
    public int SumBytes(byte[] values)
    {
        var sum = 0;
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

    public int CallSmallRawCallback() => DotNetIsolatorHost.InvokeRaw("raw-buffer-callback", _smallBuffer).Length;
    public double CallDoubleCallback(double value) => DotNetIsolatorHost.Invoke<double, double>("double-callback", value);

    private readonly NestedPayload _nestedCallbackPayload = new() { Values = new double[4096], Items = Enumerable.Range(0, 8).Select(x => new BenchmarkPayload { Id = x, Name = "nested" }).ToList() };
    public int CallbackNested() => DotNetIsolatorHost.Invoke<NestedPayload>("nested", _nestedCallbackPayload).Values.Length;

    public int TypedCallback2(int value) => DotNetIsolatorHost.Invoke<int, int, int>("add2", value, 3);
    public double TypedCallback3(int value) => DotNetIsolatorHost.Invoke<int, double, long, double>("add3", value, 3.5, 4L);
    public long TypedCallback4(int value) => DotNetIsolatorHost.Invoke<int, long, short, byte, long>("add4", value, 3L, (short)4, (byte)5);
    private readonly string _longText = new('λ', 16384);
    public string EchoText(string value) => value;
    public int TextLength(string value) => value.Length;
    public void ConsumeText(string value) => _consumedValue = value.Length;
    public string ReturnShortString() => "abcdefghijklmnopqrstuvwxyz012345";
    public string ReturnLongString() => _longText;
    public int Callback2(int value) => DotNetIsolatorHost.Invoke<int>("add2", value, 3);
    public double Callback3(int value) => DotNetIsolatorHost.Invoke<double>("add3", value, 3.5, 4L);
    public long Callback4(int value) => DotNetIsolatorHost.Invoke<long>("add4", value, 3L, (short)4, (byte)5);
    public int CallbackVoid(int value) { DotNetIsolatorHost.Invoke("consume", value); return value; }
    public List<string> EchoStrings(List<string> values) => values;
    public Dictionary<string, int> EchoDictionary(Dictionary<string, int> values) => values;

    public int Add2(int a, int b) => a + b;
    public double Add3(int a, double b, long c) => a + b + c;
    public long Add4(int a, long b, short c, byte d) => a + b + c + d;
    public int ArrayLength(double[] values) => values.Length;
    public void ConsumeArray(double[] values) => _consumedValue = values.Length;
    public double[] EchoArray(double[] values) => values;
    public int ListCount(List<double> values) => values.Count;
    public NestedPayload EchoNested(NestedPayload value) => value;
    public int CallTypedIncrementCallback(int value)
        => DotNetIsolatorHost.Invoke<int, int>("increment-callback", value);

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

public sealed class PrimitivePayload
{
    public int Id { get; set; }

    public double Ratio { get; set; }

    public long Total { get; set; }

    public bool IsValid { get; set; }

    public short Code { get; set; }
}

public sealed class BenchmarkPayload
{
    public int Id { get; set; }

    public string? Name { get; set; }

    public int Count { get; set; }

    public bool IsValid { get; set; }

    public long Checksum { get; set; }
}

public sealed class NestedPayload
{
    public List<BenchmarkPayload> Items { get; set; } = new();
    public double[] Values { get; set; } = Array.Empty<double>();
}
