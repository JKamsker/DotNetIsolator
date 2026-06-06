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

    [Fact]
    public async Task CanInvokeTaskRunMethod()
    {
        var result = await _runtime.CreateObject<Target>()
            .InvokeAsync<int>(nameof(Target.RunThenReturn))
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(44, result);
    }

    [Fact]
    public async Task CanInvokeThreadPoolWorkItemMethod()
    {
        var result = await _runtime.CreateObject<Target>()
            .InvokeAsync<int>(nameof(Target.ThreadPoolThenReturn))
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(45, result);
    }

    [Fact]
    public async Task CanInvokeTaskDelayMethod()
    {
        var result = await _runtime.CreateObject<Target>()
            .InvokeAsync<int>(nameof(Target.DelayThenReturn))
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(46, result);
    }

    [Fact]
    public async Task CanInvokeMixedAsyncWorkMethod()
    {
        var result = await _runtime.CreateObject<Target>()
            .InvokeAsync<int>(nameof(Target.WhenAllDelayAndThreadPool))
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(60, result);
    }

    [Fact]
    public async Task PropagatesTaskRunException()
    {
        var target = _runtime.CreateObject<Target>();

        var exception = await Assert.ThrowsAsync<IsolatedException>(async () =>
            await target.InvokeAsync<int>(nameof(Target.ThrowFromTaskRun)).AsTask().WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Contains("task run boom", exception.Message, StringComparison.Ordinal);
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

        public Task<int> RunThenReturn()
            => Task.Run(() => 44);

        public Task<int> ThreadPoolThenReturn()
        {
            var completion = new TaskCompletionSource<int>();
            ThreadPool.QueueUserWorkItem(static state =>
            {
                var source = (TaskCompletionSource<int>)state!;
                source.SetResult(45);
            }, completion);
            return completion.Task;
        }

        public async Task<int> DelayThenReturn()
        {
            await Task.Delay(1);
            return 46;
        }

        public async Task<int> WhenAllDelayAndThreadPool()
        {
            var results = await Task.WhenAll(
                DelayValue(10),
                Task.Run(() => 20),
                ThreadPoolValue(30));

            return results[0] + results[1] + results[2];
        }

        public Task<int> ThrowFromTaskRun()
            => Task.Run((Func<int>)(() => throw new InvalidOperationException("task run boom")));

        private static async Task<int> DelayValue(int value)
        {
            await Task.Delay(1);
            return value;
        }

        private static Task<int> ThreadPoolValue(int value)
        {
            var completion = new TaskCompletionSource<int>();
            ThreadPool.QueueUserWorkItem(static state =>
            {
                var (source, result) = ((TaskCompletionSource<int>, int))state!;
                source.SetResult(result);
            }, (completion, value));
            return completion.Task;
        }
    }
}
