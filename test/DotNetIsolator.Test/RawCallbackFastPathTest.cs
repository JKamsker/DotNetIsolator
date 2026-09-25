using DotNetIsolator.Test;
using Xunit;

namespace DotNetIsolator;

public sealed class RawCallbackFastPathTest : IDisposable
{
    private readonly IsolatedRuntime _runtime = new(SharedHost.Instance);

    [Fact]
    public void ArgumentsAndResultsHaveIndependentOwnership()
    {
        byte[]? retained = null;
        _runtime.RegisterCallback("mutate", (byte[] value) =>
        {
            retained = value;
            value[0] = 9;
            return value;
        });
        Assert.Equal(109, _runtime.Invoke(() =>
        {
            var original = new byte[] { 1, 2 };
            State.Result = DotNetIsolatorHost.InvokeRaw("mutate", original);
            return original[0] * 100 + State.Result[0];
        }));
        retained![0] = 77;
        Assert.Equal(9, _runtime.Invoke(() => (int)State.Result![0]));
    }

    [Fact]
    public void NullEmptyAndZeroArgumentsRemainDistinct()
    {
        _runtime.RegisterCallback("null", (byte[]? value) => { Assert.Null(value); return (byte[]?)null; });
        _runtime.RegisterCallback("empty", (byte[] value) => { Assert.Empty(value); return Array.Empty<byte>(); });
        _runtime.RegisterCallback("zero", () => new byte[] { 42 });
        Assert.Null(_runtime.Invoke(() => DotNetIsolatorHost.InvokeRaw("null", (byte[]?)null)));
        Assert.Empty(_runtime.Invoke(() => DotNetIsolatorHost.InvokeRaw("empty", Array.Empty<byte>())));
        Assert.Equal(new byte[] { 42 }, _runtime.Invoke(() => DotNetIsolatorHost.InvokeRaw("zero")));
    }

    [Fact]
    public void VoidAndObjectTypedCallbacksRetainTheirBehavior()
    {
        byte[]? received = null;
        _runtime.RegisterCallback("void", (Action<byte[]>)(bytes => received = bytes));
        _runtime.RegisterCallback("object", (object value) => (object)value);
        Assert.Null(_runtime.Invoke(() => DotNetIsolatorHost.InvokeRaw("void", new byte[] { 3 })));
        Assert.Equal(new byte[] { 3 }, received);
        Assert.Equal(new byte[] { 4 }, _runtime.Invoke(() => DotNetIsolatorHost.InvokeRaw<object>("object", new byte[] { 4 })));
    }

    [Fact]
    public void LargerAritiesUseTheSameNullAndCopySemantics()
    {
        _runtime.RegisterCallback("nine", (byte[]? a, byte[] b, byte[] c, byte[] d, byte[] e, byte[] f, byte[] g, byte[] h, byte[] i) =>
        {
            Assert.Null(a);
            Assert.Empty(b);
            return new[] { c[0], d[0], e[0], f[0], g[0], h[0], i[0] };
        });
        Assert.Equal(new byte[] { 3, 4, 5, 6, 7, 8, 9 }, _runtime.Invoke(() =>
            DotNetIsolatorHost.InvokeRaw("nine", null, Array.Empty<byte>(), new byte[] { 3 }, new byte[] { 4 },
                new byte[] { 5 }, new byte[] { 6 }, new byte[] { 7 }, new byte[] { 8 }, new byte[] { 9 })));
    }

    [Fact]
    public void ErrorsRemainOpaqueAndLaterCallsStillWork()
    {
        _runtime.RegisterCallback("throw", (Func<byte[], byte[]>)(_ => throw new Exception("host secret")));
        _runtime.RegisterCallback("identity", (byte[] bytes) => bytes);
        var failure = Assert.Throws<IsolatedException>(() => _runtime.Invoke(() => DotNetIsolatorHost.InvokeRaw("throw", new byte[] { 1 })));
        Assert.DoesNotContain("host secret", failure.Message);
        Assert.Contains("failed", failure.Message);
        Assert.Throws<IsolatedException>(() => _runtime.Invoke(() => DotNetIsolatorHost.InvokeRaw("identity")));
        Assert.Equal(new byte[] { 2 }, _runtime.Invoke(() => DotNetIsolatorHost.InvokeRaw("identity", new byte[] { 2 })));
        Assert.Contains("later", Assert.Throws<IsolatedException>(() => _runtime.Invoke(() => DotNetIsolatorHost.InvokeRaw("later"))).Message);
        _runtime.RegisterCallback("later", () => new byte[] { 3 });
        Assert.Equal(new byte[] { 3 }, _runtime.Invoke(() => DotNetIsolatorHost.InvokeRaw("later")));
    }

    [Fact]
    public void RawCallbackSupportsReentryAndGuestCollection()
    {
        _runtime.RegisterCallback("nested", (byte[] value) =>
        {
            _runtime.Invoke(() => GC.Collect());
            Assert.Equal(new byte[] { 5, 6 }, value);
            return _runtime.Invoke(() => DotNetIsolatorHost.InvokeRaw("inner", new byte[] { 7, 8 }));
        });
        _runtime.RegisterCallback("inner", (byte[] value) => value);
        Assert.Equal(new byte[] { 7, 8 }, _runtime.Invoke(() => DotNetIsolatorHost.InvokeRaw("nested", new byte[] { 5, 6 })));
        Assert.Equal(new byte[] { 7, 8 }, _runtime.Invoke(() => DotNetIsolatorHost.InvokeRaw("nested", new byte[] { 5, 6 })));
    }

    public void Dispose() => _runtime.Dispose();

    public static class State
    {
        public static byte[]? Result;
    }
}
