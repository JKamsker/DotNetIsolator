using DotNetIsolator.Test;
using Xunit;

namespace DotNetIsolator;

public sealed class ByteArrayFastPathTest : IDisposable
{
    private readonly IsolatedRuntime _runtime = new(SharedHost.Instance);

    [Fact]
    public void CanReturnByteArray()
    {
        var returnValue = _runtime.CreateObject<Target>()
            .Invoke<byte[]>(nameof(Target.BufferMethod));

        Assert.Equal(new byte[] { 1, 2, 3, 4 }, returnValue);
    }

    [Fact]
    public void CanReturnEmptyByteArray()
    {
        var returnValue = _runtime.CreateObject<Target>()
            .Invoke<byte[]>(nameof(Target.EmptyBufferMethod));

        Assert.Empty(returnValue);
    }

    [Fact]
    public void CanReturnNullByteArray()
    {
        var returnValue = _runtime.CreateObject<Target>()
            .Invoke<byte[]>(nameof(Target.NullBufferMethod));

        Assert.Null(returnValue);
    }

    [Fact]
    public void ThrowsIfByteArrayFastPathMethodThrows()
    {
        var ex = Assert.Throws<IsolatedException>(() => _runtime.CreateObject<Target>()
            .Invoke<byte[]>(nameof(Target.ThrowingBufferMethod)));

        Assert.Contains("System.InvalidOperationException: byte array failure", ex.Message);
    }

    [Fact]
    public void ThrowsIfByteArrayFastPathSignatureDoesNotMatch()
    {
        var ex = Assert.Throws<IsolatedException>(() => _runtime.CreateObject<Target>()
            .Invoke<byte[]>(nameof(Target.StringMethod)));

        Assert.Equal("The method does not have the required () -> byte[] signature.", ex.Message);
    }

    public void Dispose()
        => _runtime.Dispose();

    private sealed class Target
    {
        public byte[] BufferMethod()
            => [1, 2, 3, 4];

        public byte[] EmptyBufferMethod()
            => [];

        public byte[]? NullBufferMethod()
            => null;

        public byte[] ThrowingBufferMethod()
            => throw new InvalidOperationException("byte array failure");

        public string StringMethod()
            => "not bytes";
    }
}
