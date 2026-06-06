using DotNetIsolator.Test;
using Xunit;

namespace DotNetIsolator;

public sealed class BlittableCollectionInvocationTest : IDisposable
{
    private readonly IsolatedRuntime _runtime = new(SharedHost.Instance);

    [Fact]
    public void CanReturnDoubleArray()
    {
        var returnValue = _runtime.CreateObject<Target>()
            .Invoke<double[]>(nameof(Target.DoubleArray));

        Assert.Equal(new[] { 1.5, -2.25, 3.0, double.MaxValue, double.MinValue }, returnValue);
    }

    [Fact]
    public void CanReturnLongArray()
    {
        var returnValue = _runtime.CreateObject<Target>()
            .Invoke<long[]>(nameof(Target.LongArray));

        Assert.Equal(new[] { 0L, -1L, long.MaxValue, long.MinValue, 1_000_003L }, returnValue);
    }

    [Fact]
    public void CanReturnFloatList()
    {
        var returnValue = _runtime.CreateObject<Target>()
            .Invoke<List<float>>(nameof(Target.FloatList));

        Assert.Equal(new[] { 0.5f, 1.25f, -3.5f }, returnValue);
    }

    [Fact]
    public void CanReturnDoubleList()
    {
        var returnValue = _runtime.CreateObject<Target>()
            .Invoke<List<double>>(nameof(Target.DoubleList));

        Assert.Equal(new[] { 1.5, -2.25, double.MaxValue }, returnValue);
    }

    [Fact]
    public void CanReturnLongList()
    {
        var returnValue = _runtime.CreateObject<Target>()
            .Invoke<List<long>>(nameof(Target.LongList));

        Assert.Equal(new[] { 0L, long.MaxValue, long.MinValue }, returnValue);
    }

    [Fact]
    public void CanReturnEmptyDoubleList()
    {
        var returnValue = _runtime.CreateObject<Target>()
            .Invoke<List<double>>(nameof(Target.EmptyDoubleList));

        Assert.Empty(returnValue);
    }

    [Fact]
    public void CanReturnLargeLongListLosslessly()
    {
        var returnValue = _runtime.CreateObject<Target>()
            .Invoke<List<long>>(nameof(Target.LargeLongList));

        Assert.Equal(4096, returnValue.Count);
        for (var i = 0; i < returnValue.Count; i++)
        {
            Assert.Equal((long)i * 1_000_003, returnValue[i]);
        }
    }

    [Fact]
    public void CanReturnShortArray()
    {
        var returnValue = _runtime.CreateObject<Target>()
            .Invoke<short[]>(nameof(Target.ShortArray));

        Assert.Equal(new short[] { -1, 0, 1, short.MaxValue, short.MinValue }, returnValue);
    }

    [Fact]
    public void CanReturnCharArray()
    {
        var returnValue = _runtime.CreateObject<Target>()
            .Invoke<char[]>(nameof(Target.CharArray));

        Assert.Equal(new[] { 'a', 'Z', '0', '☃' }, returnValue);
    }

    [Fact]
    public void CanReturnBoolArray()
    {
        var returnValue = _runtime.CreateObject<Target>()
            .Invoke<bool[]>(nameof(Target.BoolArray));

        Assert.Equal(new[] { true, false, true, true, false }, returnValue);
    }

    [Fact]
    public void CanReturnEmptyDoubleArray()
    {
        var returnValue = _runtime.CreateObject<Target>()
            .Invoke<double[]>(nameof(Target.EmptyDoubleArray));

        Assert.Empty(returnValue);
    }

    [Fact]
    public void CanReturnLargeDoubleArrayLosslessly()
    {
        var returnValue = _runtime.CreateObject<Target>()
            .Invoke<double[]>(nameof(Target.LargeDoubleArray));

        Assert.Equal(4096, returnValue.Length);
        for (var i = 0; i < returnValue.Length; i++)
        {
            Assert.Equal(i * 0.125, returnValue[i]);
        }
    }

    [Fact]
    public void CanRoundTripDoubleArrayArgument()
    {
        var payload = new[] { 1.5, 2.5, 3.0, -4.25 };
        var sum = _runtime.CreateObject<Target>()
            .Invoke<double[], double>(nameof(Target.SumDoubles), payload);

        Assert.Equal(2.75, sum);
    }

    [Fact]
    public void CanRoundTripLongArrayArgument()
    {
        var payload = new[] { long.MaxValue, long.MinValue, 7L, -7L };
        var roundTripped = _runtime.CreateObject<Target>()
            .Invoke<long[], long[]>(nameof(Target.EchoLongs), payload);

        Assert.Equal(payload, roundTripped);
    }

    [Fact]
    public void CanRoundTripIntArrayArgument()
    {
        var payload = new[] { 1, 2, 3, 4, 5 };
        var sum = _runtime.CreateObject<Target>()
            .Invoke<int[], int>(nameof(Target.SumInts), payload);

        Assert.Equal(15, sum);
    }

    [Fact]
    public void CanRoundTripByteArrayArgument()
    {
        var payload = new byte[] { 0, 1, 2, 127, 255 };
        var sum = _runtime.CreateObject<Target>()
            .Invoke<byte[], int>(nameof(Target.SumBytes), payload);

        Assert.Equal(385, sum);
    }

    [Fact]
    public void CanEchoByteArrayArgument()
    {
        var payload = new byte[] { 4, 3, 2, 1 };
        var roundTripped = _runtime.CreateObject<Target>()
            .Invoke<byte[], byte[]>(nameof(Target.EchoBytes), payload);

        Assert.Equal(payload, roundTripped);
    }

    [Fact]
    public void NullByteArrayArgumentIsPreserved()
    {
        var length = _runtime.CreateObject<Target>()
            .Invoke<byte[], int>(nameof(Target.CountBytesOrMinusOne), null!);

        Assert.Equal(-1, length);
    }

    [Fact]
    public void ByteArrayArgumentValidatesGuestSignature()
    {
        Assert.Throws<IsolatedException>(() => _runtime.CreateObject<Target>()
            .Invoke<byte[], int>(nameof(Target.SumInts), new byte[] { 1, 2, 3 }));
    }

    [Fact]
    public void CanPassEmptyArrayArgument()
    {
        var sum = _runtime.CreateObject<Target>()
            .Invoke<double[], double>(nameof(Target.SumDoubles), Array.Empty<double>());

        Assert.Equal(0.0, sum);
    }

    [Fact]
    public void NullArrayArgumentIsPreserved()
    {
        var length = _runtime.CreateObject<Target>()
            .Invoke<double[], int>(nameof(Target.CountOrMinusOne), null!);

        Assert.Equal(-1, length);
    }

    [Fact]
    public void CanRoundTripLargeDoubleArrayArgument()
    {
        var payload = new double[4096];
        var expected = 0.0;
        for (var i = 0; i < payload.Length; i++)
        {
            payload[i] = i * 0.5;
            expected += payload[i];
        }

        var sum = _runtime.CreateObject<Target>()
            .Invoke<double[], double>(nameof(Target.SumDoubles), payload);

        Assert.Equal(expected, sum);
    }

    public void Dispose()
        => _runtime.Dispose();

    private sealed class Target
    {
        public double[] DoubleArray()
            => [1.5, -2.25, 3.0, double.MaxValue, double.MinValue];

        public long[] LongArray()
            => [0L, -1L, long.MaxValue, long.MinValue, 1_000_003L];

        public List<float> FloatList()
            => [0.5f, 1.25f, -3.5f];

        public List<double> DoubleList()
            => [1.5, -2.25, double.MaxValue];

        public List<long> LongList()
            => [0L, long.MaxValue, long.MinValue];

        public List<double> EmptyDoubleList()
            => [];

        public List<long> LargeLongList()
        {
            var values = new List<long>(4096);
            for (var i = 0; i < 4096; i++)
            {
                values.Add((long)i * 1_000_003);
            }

            return values;
        }

        public short[] ShortArray()
            => [-1, 0, 1, short.MaxValue, short.MinValue];

        public char[] CharArray()
            => ['a', 'Z', '0', '☃'];

        public bool[] BoolArray()
            => [true, false, true, true, false];

        public double[] EmptyDoubleArray()
            => [];

        public double[] LargeDoubleArray()
        {
            var values = new double[4096];
            for (var i = 0; i < values.Length; i++)
            {
                values[i] = i * 0.125;
            }

            return values;
        }

        public double SumDoubles(double[] values)
        {
            var sum = 0.0;
            foreach (var value in values)
            {
                sum += value;
            }

            return sum;
        }

        public long[] EchoLongs(long[] values)
            => values;

        public int SumInts(int[] values)
        {
            var sum = 0;
            foreach (var value in values)
            {
                sum += value;
            }

            return sum;
        }

        public int SumBytes(byte[] values)
        {
            var sum = 0;
            foreach (var value in values)
            {
                sum += value;
            }

            return sum;
        }

        public byte[] EchoBytes(byte[] values)
            => values;

        public int CountBytesOrMinusOne(byte[]? values)
            => values?.Length ?? -1;

        public int CountOrMinusOne(double[]? values)
            => values?.Length ?? -1;
    }
}
