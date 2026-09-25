using DotNetIsolator.Test;
using Xunit;

namespace DotNetIsolator;

public sealed class MultiScalarCallbackTest : IDisposable
{
    private readonly IsolatedRuntime _runtime = new(SharedHost.Instance);

    [Fact]
    public void TypedAndParamsOverloadsPreserveMixedPrimitiveArguments()
    {
        _runtime.RegisterCallback("two", (int a, long b) => a + b);
        _runtime.RegisterCallback("three", (short a, double b, byte c) => a + b + c);
        _runtime.RegisterCallback("four", (bool a, char b, float c, ulong d) =>
        {
            Assert.True(a); Assert.Equal('\ud800', b); Assert.True(float.IsNaN(c)); return d;
        });
        Assert.Equal(-7L, _runtime.Invoke(() => DotNetIsolatorHost.Invoke<int, long, long>("two", -10, 3)));
        Assert.Equal(-7L, _runtime.Invoke(() => DotNetIsolatorHost.Invoke<long>("two", -10, 3L)));
        Assert.Equal(4.5, _runtime.Invoke(() => DotNetIsolatorHost.Invoke<short, double, byte, double>("three", -3, 5.5, 2)));
        Assert.Equal(4.5, _runtime.Invoke(() => DotNetIsolatorHost.Invoke<double>("three", (short)-3, 5.5, (byte)2)));
        Assert.Equal(ulong.MaxValue, _runtime.Invoke(() => DotNetIsolatorHost.Invoke<bool, char, float, ulong, ulong>("four", true, '\ud800', float.NaN, ulong.MaxValue)));
        Assert.Equal(ulong.MaxValue, _runtime.Invoke(() => DotNetIsolatorHost.Invoke<ulong>("four", true, '\ud800', float.NaN, ulong.MaxValue)));
    }

    [Fact]
    public void WideBitsAndRemainingKindsSurviveTheTransport()
    {
        _runtime.RegisterCallback("bits", (double a, long b) => { Assert.Equal(long.MinValue, b); return BitConverter.DoubleToInt64Bits(a); });
        _runtime.RegisterCallback("small", (sbyte a, ushort b, uint c) => (long)a + b + c);
        Assert.Equal(0x7ff8000000001234L, _runtime.Invoke(() => DotNetIsolatorHost.Invoke<double, long, long>("bits", BitConverter.Int64BitsToDouble(0x7ff8000000001234L), long.MinValue)));
        Assert.Equal((long)-128 + 65535 + uint.MaxValue, _runtime.Invoke(() => DotNetIsolatorHost.Invoke<sbyte, ushort, uint, long>("small", -128, 65535, uint.MaxValue)));
    }

    [Fact]
    public void VoidCallsSupportAllAritiesAndDiscardedResults()
    {
        var total = 0;
        _runtime.RegisterCallback("zero", (Action)(() => total++));
        _runtime.RegisterCallback("one", (Action<int>)(a => total += a));
        _runtime.RegisterCallback("two", (Action<int, long>)((a, b) => total += a + (int)b));
        _runtime.RegisterCallback("three", (Action<int, long, short>)((a, b, c) => total += a + (int)b + c));
        _runtime.RegisterCallback("four", (Action<int, long, short, byte>)((a, b, c, d) => total += a + (int)b + c + d));
        _runtime.RegisterCallback("discard", (int a, int b) => { total += a + b; return "ignored"; });
        _runtime.Invoke(() =>
        {
            DotNetIsolatorHost.Invoke("zero");
            DotNetIsolatorHost.Invoke("one", 2);
            DotNetIsolatorHost.Invoke("two", 3, 4L);
            DotNetIsolatorHost.Invoke("three", 5, 6L, (short)7);
            DotNetIsolatorHost.Invoke("four", 8, 9L, (short)10, (byte)11);
            DotNetIsolatorHost.Invoke("discard", 12, 13);
        });
        Assert.Equal(91, total);
    }

    [Fact]
    public void NonScalarNullAndLargerAritiesRetainSerializationFallback()
    {
        _runtime.RegisterCallback("text", (string? a, int b) => a is null ? b.ToString() : a + b);
        _runtime.RegisterCallback("five", (int a, int b, int c, int d, int e) => a + b + c + d + e);
        Assert.Equal("2", _runtime.Invoke(() => DotNetIsolatorHost.Invoke<string?, int, string>("text", null, 2)));
        Assert.Equal("ok2", _runtime.Invoke(() => DotNetIsolatorHost.Invoke<string, int, string>("text", "ok", 2)));
        Assert.Equal(15, _runtime.Invoke(() => DotNetIsolatorHost.Invoke<int>("five", 1, 2, 3, 4, 5)));
    }

    [Fact]
    public void SignatureErrorsDoNotInvokeCallbacksAndHostErrorsRemainOpaque()
    {
        var calls = 0;
        _runtime.RegisterCallback("two", (int a, long b) => { calls++; return a + b; });
        Assert.Throws<IsolatedException>(() => _runtime.Invoke(() => DotNetIsolatorHost.Invoke<int, int, long>("two", 1, 2)));
        Assert.Throws<IsolatedException>(() => _runtime.Invoke(() => DotNetIsolatorHost.Invoke<int, long, double>("two", 1, 2)));
        Assert.Throws<IsolatedException>(() => _runtime.Invoke(() => DotNetIsolatorHost.Invoke<long>("two", 1, 2L, 3)));
        Assert.Equal(0, calls);
        _runtime.RegisterCallback("throw", (Func<int, int, int>)((a, b) => throw new Exception("host secret")));
        Assert.DoesNotContain("host secret", Assert.Throws<IsolatedException>(() => _runtime.Invoke(() => DotNetIsolatorHost.Invoke<int, int, int>("throw", 1, 2))).Message);
        Assert.Equal(3L, _runtime.Invoke(() => DotNetIsolatorHost.Invoke<int, long, long>("two", 1, 2)));
        Assert.Contains("later", Assert.Throws<IsolatedException>(() => _runtime.Invoke(() => DotNetIsolatorHost.Invoke<int>("later", 1, 2))).Message);
        _runtime.RegisterCallback("later", (int a, int b) => a + b);
        Assert.Equal(3, _runtime.Invoke(() => DotNetIsolatorHost.Invoke<int>("later", 1, 2)));
    }

    [Fact]
    public void CustomMulticastAndClosedStaticDelegatesKeepTheirInvocationSemantics()
    {
        var calls = 0;
        CustomAdd first = (a, b) => { calls++; return a + b; };
        CustomAdd last = (a, b) => { calls++; return a - b; };
        _runtime.RegisterCallback("custom", first + last);
        Assert.Equal(5, _runtime.Invoke(() => DotNetIsolatorHost.Invoke<int, int, int>("custom", 7, 2)));
        Assert.Equal(2, calls);
        var closed = typeof(MultiScalarCallbackTest).GetMethod(nameof(ClosedStatic))!.CreateDelegate<Func<int, int, int>>("prefix");
        _runtime.RegisterCallback("closed", closed);
        Assert.Equal(9, _runtime.Invoke(() => DotNetIsolatorHost.Invoke<int, int, int>("closed", 1, 2)));
    }

    [Fact]
    public void NestedCallbacksRetainOuterArgumentsAndResults()
    {
        _runtime.RegisterCallback("inner", (int a, long b) => a + b);
        _runtime.RegisterCallback("outer", (int a, long b) =>
        {
            _runtime.Invoke(() => GC.Collect());
            return a + b + _runtime.Invoke(() => DotNetIsolatorHost.Invoke<int, long, long>("inner", 3, 4));
        });
        Assert.Equal(10, _runtime.Invoke(() => DotNetIsolatorHost.Invoke<int, long, long>("outer", 1, 2)));
    }

    public delegate int CustomAdd(int a, int b);
    public static int ClosedStatic(string prefix, int a, int b) => prefix.Length + a + b;
    public void Dispose() => _runtime.Dispose();
}
