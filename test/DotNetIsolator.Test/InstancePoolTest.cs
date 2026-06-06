using DotNetIsolator.Test;
using Xunit;

namespace DotNetIsolator;

public sealed class InstancePoolTest
{
    private static IsolatedRuntimeHost CreatePooledHost()
        => new IsolatedRuntimeHost(new IsolatedRuntimeHostOptions
        {
            UseRuntimeMemorySnapshot = true,
            UseInstancePool = true,
        }).WithBinDirectoryAssemblyLoader();

    [Fact]
    public void RequiresSnapshot()
    {
        Assert.Throws<ArgumentException>(() => new IsolatedRuntimeHost(new IsolatedRuntimeHostOptions
        {
            UseInstancePool = true,
            UseRuntimeMemorySnapshot = false,
        }));
    }

    [Fact]
    public void ReusedRuntimesDoNotLeakStaticState()
    {
        using var host = CreatePooledHost();

        // Sequential create/dispose means iterations after the first reuse a parked instance.
        // If the reset were incomplete, the static counter would accumulate across tenants.
        for (var i = 0; i < 30; i++)
        {
            using var runtime = new IsolatedRuntime(host);
            var obj = runtime.CreateObject<Target>();

            Assert.Equal(1, obj.Invoke<int>(nameof(Target.IncrementCounter)));
            Assert.Equal(2, obj.Invoke<int>(nameof(Target.IncrementCounter)));
            Assert.Equal(3, obj.Invoke<int>(nameof(Target.IncrementCounter)));

            obj.ReleaseGCHandle();
        }
    }

    [Fact]
    public void ReusedRuntimesComputeCorrectly()
    {
        using var host = CreatePooledHost();

        for (var i = 0; i < 30; i++)
        {
            using var runtime = new IsolatedRuntime(host);
            var obj = runtime.CreateObject<Target>();

            Assert.Equal((i * 2) + 1, obj.Invoke<int, int>(nameof(Target.Compute), i));
            Assert.Equal(7.0, obj.Invoke<double[], double>(nameof(Target.SumDoubles), new[] { 1.5, 2.5, 3.0 }));
            Assert.Equal(new[] { 10, 20, 30 }, obj.Invoke<int[]>(nameof(Target.MakeArray)));

            var payload = obj.Invoke<int, Payload>(nameof(Target.MakePayload), i);
            Assert.Equal("hello", payload.Name);
            Assert.Equal(i, payload.Seed);

            obj.ReleaseGCHandle();
        }
    }

    [Fact]
    public void ReusedRuntimesSupportCallbacks()
    {
        using var host = CreatePooledHost();

        for (var i = 0; i < 15; i++)
        {
            using var runtime = new IsolatedRuntime(host);
            runtime.RegisterCallback("multiply", (int a, int b) => a * b);
            var result = runtime.Invoke(() => DotNetIsolatorHost.Invoke<int>("multiply", 6, 7));
            Assert.Equal(42, result);
        }
    }

    private sealed class Target
    {
        public static int Counter;

        public int IncrementCounter() => ++Counter;

        public int Compute(int x) => (x * 2) + 1;

        public double SumDoubles(double[] values)
        {
            var sum = 0.0;
            foreach (var value in values)
            {
                sum += value;
            }

            return sum;
        }

        public int[] MakeArray() => [10, 20, 30];

        public Payload MakePayload(int seed) => new() { Name = "hello", Seed = seed };
    }

    public sealed class Payload
    {
        public string? Name { get; set; }

        public int Seed { get; set; }
    }
}
