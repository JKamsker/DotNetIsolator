using DotNetIsolator.Test;
using Xunit;

namespace DotNetIsolator;

public sealed class CollectionInvocationTest : IDisposable
{
    private readonly IsolatedRuntime _runtime = new(SharedHost.Instance);

    [Fact]
    public void CanReturnList()
    {
        var returnValue = _runtime.CreateObject<Target>()
            .Invoke<List<int>>(nameof(Target.ListMethod));

        Assert.Equal(new[] { 1, 2, 3 }, returnValue);
    }

    [Fact]
    public void CanReturnEmptyList()
    {
        var returnValue = _runtime.CreateObject<Target>()
            .Invoke<List<int>>(nameof(Target.EmptyListMethod));

        Assert.Empty(returnValue);
    }

    [Fact]
    public void CanReturnIntArray()
    {
        var returnValue = _runtime.CreateObject<Target>()
            .Invoke<int[]>(nameof(Target.IntArrayMethod));

        Assert.Equal(new[] { 4, 5, 6 }, returnValue);
    }

    public void Dispose()
        => _runtime.Dispose();

    private sealed class Target
    {
        public List<int> ListMethod()
            => [1, 2, 3];

        public List<int> EmptyListMethod()
            => [];

        public int[] IntArrayMethod()
            => [4, 5, 6];
    }
}
