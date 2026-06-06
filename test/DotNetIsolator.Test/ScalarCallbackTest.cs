using DotNetIsolator.Test;
using Xunit;

namespace DotNetIsolator;

public sealed class ScalarCallbackTest : IDisposable
{
    private readonly IsolatedRuntime _runtime = new(SharedHost.Instance);

    [Fact]
    public void IntToIntCallback()
    {
        _runtime.RegisterCallback("inc", (int x) => x + 1);
        var result = _runtime.Invoke(() => DotNetIsolatorHost.Invoke<int>("inc", 41));
        Assert.Equal(42, result);
    }

    [Fact]
    public void DoubleToDoubleCallback()
    {
        _runtime.RegisterCallback("half", (double x) => x / 2.0);
        var result = _runtime.Invoke(() => DotNetIsolatorHost.Invoke<double>("half", 9.0));
        Assert.Equal(4.5, result);
    }

    [Fact]
    public void LongToLongCallback()
    {
        _runtime.RegisterCallback("dbl", (long x) => x * 2);
        var result = _runtime.Invoke(() => DotNetIsolatorHost.Invoke<long>("dbl", long.MaxValue / 2));
        Assert.Equal((long.MaxValue / 2) * 2, result);
    }

    [Fact]
    public void ZeroArgLongCallback()
    {
        _runtime.RegisterCallback("big", () => long.MaxValue);
        var result = _runtime.Invoke(() => DotNetIsolatorHost.Invoke<long>("big"));
        Assert.Equal(long.MaxValue, result);
    }

    [Fact]
    public void BoolToBoolCallback()
    {
        _runtime.RegisterCallback("not", (bool b) => !b);
        var result = _runtime.Invoke(() => DotNetIsolatorHost.Invoke<bool>("not", false));
        Assert.True(result);
    }

    [Fact]
    public void CrossTypeScalarCallback()
    {
        _runtime.RegisterCallback("toDouble", (int x) => x + 0.5);
        var result = _runtime.Invoke(() => DotNetIsolatorHost.Invoke<double>("toDouble", 7));
        Assert.Equal(7.5, result);
    }

    [Fact]
    public void UnknownScalarCallbackThrows()
    {
        var message = _runtime.Invoke(() =>
        {
            try
            {
                DotNetIsolatorHost.Invoke<int>("missing", 1);
                return "no throw";
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        });

        Assert.Contains("failed", message);
    }

    [Fact]
    public void ScalarCallbackErrorIsOpaque()
    {
        _runtime.RegisterCallback("throws", (Func<int, int>)(x => throw new InvalidTimeZoneException("secret")));
        var message = _runtime.Invoke(() =>
        {
            try
            {
                DotNetIsolatorHost.Invoke<int>("throws", 1);
                return "no throw";
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        });

        Assert.DoesNotContain("secret", message);
        Assert.Contains("failed", message);
    }

    public void Dispose()
        => _runtime.Dispose();
}
