using Xunit;

namespace DotNetIsolator;

public sealed class AssemblyLookupTest
{
    [Fact]
    public void AlreadyLoadedAssembliesDoNotRepeatHostProbes()
    {
        var coreLibRequests = 0;
        using var host = new IsolatedRuntimeHost().WithAssemblyLoader(name =>
        {
            if (name == "System.Private.CoreLib") coreLibRequests++;
            return null;
        }).WithBinDirectoryAssemblyLoader();
        using var runtime = new IsolatedRuntime(host);
        using var target = runtime.CreateObject<Target>();
        var values = new List<string> { "one", "two" };
        Assert.Equal(values, target.Invoke<List<string>, List<string>>(nameof(Target.Echo), values));
        var afterWarmup = coreLibRequests;
        for (var i = 0; i < 10; i++)
            Assert.Equal(values, target.Invoke<List<string>, List<string>>(nameof(Target.Echo), values));
        Assert.Equal(afterWarmup, coreLibRequests);
    }

    [Fact]
    public void CustomAssembliesStillLoadIndependentlyForEachRuntime()
    {
        var assembly = typeof(Target).Assembly;
        var bytes = File.ReadAllBytes(assembly.Location);
        var count = 0;
        using var host = new IsolatedRuntimeHost().WithAssemblyLoader(name =>
        {
            if (name != assembly.GetName().Name) return null;
            count++;
            return bytes;
        }).WithBinDirectoryAssemblyLoader();
        for (var i = 0; i < 2; i++)
        {
            using var runtime = new IsolatedRuntime(host);
            using var target = runtime.CreateObject<Target>();
            Assert.Equal(7, target.Invoke<int>(nameof(Target.Value)));
            Assert.Equal(7, target.Invoke<int>(nameof(Target.Value)));
            Assert.Equal(i + 1, count);
        }
    }

    public sealed class Target
    {
        public List<string> Echo(List<string> values) => values;
        public int Value() => 7;
    }
}
