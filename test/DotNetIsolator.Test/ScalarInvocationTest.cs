using DotNetIsolator.Test;
using Xunit;

namespace DotNetIsolator;

public sealed class ScalarInvocationTest : IDisposable
{
    private readonly IsolatedRuntime _runtime = new(SharedHost.Instance);

    [Fact]
    public void CanRoundTripDoubleScalar()
    {
        var obj = _runtime.CreateObject<Target>();
        Assert.Equal(-3.5, obj.Invoke<double, double>(nameof(Target.NegateDouble), 3.5));
        Assert.Equal(-double.MaxValue, obj.Invoke<double, double>(nameof(Target.NegateDouble), double.MaxValue));
        Assert.Equal(double.NegativeInfinity, obj.Invoke<double, double>(nameof(Target.NegateDouble), double.PositiveInfinity));
    }

    [Fact]
    public void CanRoundTripLongScalar()
    {
        var obj = _runtime.CreateObject<Target>();
        Assert.Equal(246L, obj.Invoke<long, long>(nameof(Target.DoubleLong), 123L));
        Assert.Equal(unchecked(long.MinValue + long.MinValue), obj.Invoke<long, long>(nameof(Target.DoubleLong), long.MinValue));
    }

    [Fact]
    public void CanRoundTripFloatScalar()
    {
        var result = _runtime.CreateObject<Target>().Invoke<float, float>(nameof(Target.HalfFloat), 9.0f);
        Assert.Equal(4.5f, result);
    }

    [Fact]
    public void CanRoundTripShortScalar()
    {
        var result = _runtime.CreateObject<Target>().Invoke<short, short>(nameof(Target.NegateShort), (short)1234);
        Assert.Equal((short)-1234, result);
    }

    [Fact]
    public void CanRoundTripBoolScalar()
    {
        var obj = _runtime.CreateObject<Target>();
        Assert.True(obj.Invoke<bool, bool>(nameof(Target.Not), false));
        Assert.False(obj.Invoke<bool, bool>(nameof(Target.Not), true));
    }

    [Fact]
    public void CanRoundTripCharScalar()
    {
        var result = _runtime.CreateObject<Target>().Invoke<char, char>(nameof(Target.NextChar), 'a');
        Assert.Equal('b', result);
    }

    [Fact]
    public void CanReturnDoubleScalarWithNoArgs()
    {
        var result = _runtime.CreateObject<Target>().Invoke<double>(nameof(Target.GetPi));
        Assert.Equal(System.Math.PI, result);
    }

    [Fact]
    public void CanReturnLongScalarWithNoArgs()
    {
        var result = _runtime.CreateObject<Target>().Invoke<long>(nameof(Target.GetBigLong));
        Assert.Equal(long.MaxValue, result);
    }

    [Fact]
    public void CanConvertIntArgToDoubleResult()
    {
        var result = _runtime.CreateObject<Target>().Invoke<int, double>(nameof(Target.IntToDouble), 7);
        Assert.Equal(7.5, result);
    }

    [Fact]
    public void CanConvertDoubleArgToIntResult()
    {
        var result = _runtime.CreateObject<Target>().Invoke<double, int>(nameof(Target.DoubleToInt), 42.9);
        Assert.Equal(42, result);
    }

    [Fact]
    public void CanBatchInvokeIntToInt()
    {
        var obj = _runtime.CreateObject<Target>();
        var method = obj.FindMethod(nameof(Target.Triple), 1);
        var results = method.InvokeBatch<int, int>(obj, new[] { 1, 2, 3, 4, 5 });
        Assert.Equal(new[] { 3, 6, 9, 12, 15 }, results);
    }

    [Fact]
    public void CanBatchInvokeDoubleToDouble()
    {
        var obj = _runtime.CreateObject<Target>();
        var method = obj.FindMethod(nameof(Target.Scale), 1);
        var results = method.InvokeBatch<double, double>(obj, new[] { 1.0, 2.0, 4.0 });
        Assert.Equal(new[] { 2.5, 5.0, 10.0 }, results);
    }

    [Fact]
    public void CanBatchInvokeLargeLosslessly()
    {
        var obj = _runtime.CreateObject<Target>();
        var method = obj.FindMethod(nameof(Target.Triple), 1);
        var args = new int[2048];
        for (var i = 0; i < args.Length; i++)
        {
            args[i] = i;
        }

        var results = method.InvokeBatch<int, int>(obj, args);
        Assert.Equal(2048, results.Length);
        for (var i = 0; i < results.Length; i++)
        {
            Assert.Equal(i * 3, results[i]);
        }
    }

    [Fact]
    public void EmptyBatchReturnsEmpty()
    {
        var obj = _runtime.CreateObject<Target>();
        var method = obj.FindMethod(nameof(Target.Triple), 1);
        Assert.Empty(method.InvokeBatch<int, int>(obj, ReadOnlySpan<int>.Empty));
    }

    [Fact]
    public void ThrowsWhenScalarSignatureMismatches()
    {
        // The guest validates the requested kinds against the real signature: NegateDouble is
        // (double) -> double, so requesting (long) -> long must fail rather than reinterpret bits.
        var obj = _runtime.CreateObject<Target>();
        Assert.Throws<IsolatedException>(() => obj.Invoke<long, long>(nameof(Target.NegateDouble), 1L));
    }

    public void Dispose()
        => _runtime.Dispose();

    private sealed class Target
    {
        public double NegateDouble(double value) => -value;

        public long DoubleLong(long value) => unchecked(value + value);

        public float HalfFloat(float value) => value / 2f;

        public short NegateShort(short value) => (short)-value;

        public bool Not(bool value) => !value;

        public char NextChar(char value) => (char)(value + 1);

        public double GetPi() => System.Math.PI;

        public long GetBigLong() => long.MaxValue;

        public double IntToDouble(int value) => value + 0.5;

        public int DoubleToInt(double value) => (int)value;

        public int Triple(int value) => value * 3;

        public double Scale(double value) => value * 2.5;
    }
}
