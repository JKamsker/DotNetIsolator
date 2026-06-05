using Xunit;

namespace DotNetIsolator;

public class StartupTest
{
    [Fact]
    public void CanStartWithDefaultConfig()
    {
        using var host = new IsolatedRuntimeHost();
        using var runtime = new IsolatedRuntime(host);
    }

    [Fact]
    public void CanStartWithPoolingAllocator()
    {
        using var host = new IsolatedRuntimeHost(new IsolatedRuntimeHostOptions
        {
            UsePoolingAllocator = true,
            PoolingInstanceCapacity = 4,
            PoolingMemoryCapacity = 4,
            PoolingTableCapacity = 4,
        });

        using var runtime = new IsolatedRuntime(host);
    }

    [Fact]
    public void CanStartWithPrecompiledModuleCache()
    {
        var cacheDirectory = Path.Combine(Path.GetTempPath(), "DotNetIsolator.Test", Guid.NewGuid().ToString("N"));
        try
        {
            var options = new IsolatedRuntimeHostOptions
            {
                PrecompiledModuleCacheDirectory = cacheDirectory,
            };

            using (var host = new IsolatedRuntimeHost(options))
            using (var runtime = new IsolatedRuntime(host))
            {
            }

            Assert.Contains(Directory.EnumerateFiles(cacheDirectory), path => Path.GetExtension(path) == ".cwasm");

            using var cachedHost = new IsolatedRuntimeHost(options);
            using var cachedRuntime = new IsolatedRuntime(cachedHost);
        }
        finally
        {
            if (Directory.Exists(cacheDirectory))
            {
                Directory.Delete(cacheDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public void CanStartWithRuntimeMemorySnapshot()
    {
        var cacheDirectory = Path.Combine(Path.GetTempPath(), "DotNetIsolator.Test", Guid.NewGuid().ToString("N"));
        try
        {
            using var host = new IsolatedRuntimeHost(new IsolatedRuntimeHostOptions
            {
                PrecompiledModuleCacheDirectory = cacheDirectory,
                UseRuntimeMemorySnapshot = true,
            }).WithBinDirectoryAssemblyLoader();

            host.PreloadRuntimeMemorySnapshot();

            using (var runtime = new IsolatedRuntime(host))
            {
                var target = runtime.CreateObject<StartupTest>();
                Assert.NotNull(target.FindMethod(nameof(ReturnsInput), 1));
            }

            using (var runtime = new IsolatedRuntime(host))
            {
                runtime.RegisterCallback("snapshot-callback", (int value) => value + 1);
                var result = runtime.Invoke(() => DotNetIsolatorHost.Invoke<int>("snapshot-callback", 41));
                Assert.Equal(42, result);
            }
        }
        finally
        {
            if (Directory.Exists(cacheDirectory))
            {
                Directory.Delete(cacheDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public void CanLoadTypesFromBclAssembliesWithoutAnyLoader()
    {
        using var host = new IsolatedRuntimeHost();
        using var runtime = new IsolatedRuntime(host);

        // Can instantiate System.Object
        var p = runtime.CreateObject<object>();

        // Can find a method on it
        var toString = p.FindMethod(nameof(object.ToString));
        Assert.NotNull(toString);

        // Can invoke a method on it
        Assert.Equal(typeof(object).FullName, toString.Invoke<string>(p));
    }

    [Fact]
    public void CannotLoadTypesFromOtherAssembliesWithoutLoader()
    {
        using var host = new IsolatedRuntimeHost();
        using var runtime = new IsolatedRuntime(host);

        var ex = Assert.Throws<IsolatedException>(runtime.CreateObject<StartupTest>);
        Assert.Equal($"Could not load assembly '{typeof(StartupTest).Assembly.GetName().Name}'", ex.Message);
    }

    [Fact]
    public void CanLoadTypesFromBinDirIfConfigured()
    {
        using var host = new IsolatedRuntimeHost().WithBinDirectoryAssemblyLoader();
        using var runtime = new IsolatedRuntime(host);
        Assert.NotNull(runtime.CreateObject<StartupTest>());
    }

    public int ReturnsInput(int value)
        => value;
}
