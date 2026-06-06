using DotNetIsolator.Test;
using Xunit;

namespace DotNetIsolator;

public sealed class AsyncInvocationTest : IDisposable
{
    private readonly IsolatedRuntime _runtime = new(SharedHost.Instance);

    [Fact]
    public async Task CanInvokeTaskReturningMethodAfterYield()
    {
        var result = await _runtime.CreateObject<Target>()
            .InvokeAsync<int>(nameof(Target.YieldThenReturn));

        Assert.Equal(42, result);
    }

    [Fact]
    public async Task CanInvokeValueTaskReturningMethodAfterYield()
    {
        var result = await _runtime.CreateObject<Target>()
            .InvokeAsync<string>(nameof(Target.YieldThenReturnString));

        Assert.Equal("done", result);
    }

    [Fact]
    public async Task CanInvokeNonGenericTaskMethodAfterYield()
    {
        var target = _runtime.CreateObject<Target>();

        await target.InvokeAsync<int>(nameof(Target.SetAfterYield), 123);

        Assert.Equal(123, target.Invoke<int>(nameof(Target.GetValue)));
    }

    [Fact]
    public async Task PropagatesAsyncException()
    {
        var target = _runtime.CreateObject<Target>();

        var exception = await Assert.ThrowsAsync<IsolatedException>(async () =>
            await target.InvokeAsync<int>(nameof(Target.ThrowAfterYield)));

        Assert.Contains("async boom", exception.Message, StringComparison.Ordinal);
    }

    public void Dispose()
        => _runtime.Dispose();

    private sealed class Target
    {
        private int _value;

        public async Task<int> YieldThenReturn()
        {
            await Task.Yield();
            return 42;
        }

        public async ValueTask<string> YieldThenReturnString()
        {
            await Task.Yield();
            return "done";
        }

        public async Task SetAfterYield(int value)
        {
            await Task.Yield();
            _value = value;
        }

        public int GetValue()
            => _value;

        public async Task<int> ThrowAfterYield()
        {
            await Task.Yield();
            throw new InvalidOperationException("async boom");
        }
    }
}
