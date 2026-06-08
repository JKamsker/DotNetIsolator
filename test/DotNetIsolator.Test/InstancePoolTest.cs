using DotNetIsolator.Test;
using System.Reflection;
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
    public void RejectsNegativeMaxPoolSize()
    {
        Assert.Throws<ArgumentException>(() => new IsolatedRuntimeHost(new IsolatedRuntimeHostOptions
        {
            MaxInstancePoolSize = -1,
        }));
    }

    [Fact]
    public void RejectsInvalidResetMode()
    {
        Assert.Throws<ArgumentException>(() => new IsolatedRuntimeHost(new IsolatedRuntimeHostOptions
        {
            InstancePoolResetMode = (InstancePoolResetMode)42,
        }));
    }

    [Fact]
    public void DoesNotParkMoreThanMaxPoolSize()
    {
        using var host = new IsolatedRuntimeHost(new IsolatedRuntimeHostOptions
        {
            UseRuntimeMemorySnapshot = true,
            UseInstancePool = true,
            MaxInstancePoolSize = 1,
        }).WithBinDirectoryAssemblyLoader();

        var runtime1 = new IsolatedRuntime(host);
        var runtime2 = new IsolatedRuntime(host);

        runtime1.Dispose();
        runtime2.Dispose();

        Assert.Equal(1, GetParkedInstanceCount(host));
    }

    [Fact]
    public void RuntimeDisposeReturnsPooledInstanceOnlyOnce()
    {
        using var host = CreatePooledHost();
        var runtime = new IsolatedRuntime(host);

        runtime.Dispose();
        runtime.Dispose();

        Assert.Equal(1, GetParkedInstanceCount(host));
    }

    [Fact]
    public void ReturnedPooledInstanceDoesNotRetainRuntimeStoreData()
    {
        using var host = CreatePooledHost();
        var runtime = new IsolatedRuntime(host);

        runtime.Dispose();

        var parkedStoreData = GetParkedStoreData(host);
        Assert.NotSame(runtime, parkedStoreData);
        Assert.IsNotType<IsolatedRuntime>(parkedStoreData);
    }

    [Fact]
    public void RuntimeDisposedAfterHostDisposeIsNotReturnedToPool()
    {
        var host = CreatePooledHost();
        var runtime = new IsolatedRuntime(host);

        host.Dispose();
        runtime.Dispose();

        Assert.Equal(0, GetParkedInstanceCount(host));
    }

    [Fact]
    public void FullResetModeCapturesFullSnapshotPages()
    {
        using var host = new IsolatedRuntimeHost(new IsolatedRuntimeHostOptions
        {
            UseRuntimeMemorySnapshot = true,
            UseInstancePool = true,
            InstancePoolResetMode = InstancePoolResetMode.FullSnapshotRestore,
        }).WithBinDirectoryAssemblyLoader();

        host.PreloadRuntimeMemorySnapshot();

        var snapshot = GetRuntimeMemorySnapshot(host);
        var allPages = GetSnapshotAllPages(snapshot);
        Assert.NotNull(allPages);
        Assert.True(allPages.Length > 0);
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

    private static int GetParkedInstanceCount(IsolatedRuntimeHost host)
        => (int)typeof(IsolatedRuntimeHost)
            .GetField("_parkedInstanceCount", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(host)!;

    private static object GetParkedStoreData(IsolatedRuntimeHost host)
    {
        var parkedInstances = typeof(IsolatedRuntimeHost)
            .GetField("_parkedInstances", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(host)!;
        var args = new object?[] { null };
        var found = (bool)parkedInstances.GetType().GetMethod("TryPeek")!.Invoke(parkedInstances, args)!;
        Assert.True(found);

        var lease = args[0]!;
        var store = lease.GetType().GetProperty("Store")!.GetValue(lease)!;
        return store.GetType().GetMethod("GetData")!.Invoke(store, null)!;
    }

    private static object GetRuntimeMemorySnapshot(IsolatedRuntimeHost host)
        => typeof(IsolatedRuntimeHost)
            .GetField("_runtimeMemorySnapshot", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(host)!;

    private static Array? GetSnapshotAllPages(object snapshot)
        => (Array?)snapshot
            .GetType()
            .GetField("_allPages", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(snapshot);

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
