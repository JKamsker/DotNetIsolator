using DotNetIsolator.Test;
using Xunit;

namespace DotNetIsolator;

public sealed class PrimitiveSerializationTest : IDisposable
{
    private readonly IsolatedRuntime _runtime = new(SharedHost.Instance);

    [Fact]
    public void CanRoundTripGuid()
    {
        var value = Guid.Parse("4d9f1bb0-7300-45c2-9f32-b8d34fce2406");

        var result = _runtime.CreateObject<Target>()
            .Invoke<Guid, Guid>(nameof(Target.EchoGuid), value);

        Assert.Equal(value, result);
    }

    [Fact]
    public void CanRoundTripDecimal()
    {
        const decimal value = -123456789.987654321m;

        var result = _runtime.CreateObject<Target>()
            .Invoke<decimal, decimal>(nameof(Target.EchoDecimal), value);

        Assert.Equal(value, result);
    }

    [Fact]
    public void CanRoundTripDateTime()
    {
        var value = new DateTime(2026, 6, 6, 12, 34, 56, DateTimeKind.Utc).AddTicks(789);

        var result = _runtime.CreateObject<Target>()
            .Invoke<DateTime, DateTime>(nameof(Target.EchoDateTime), value);

        Assert.Equal(value, result);
        Assert.Equal(value.Kind, result.Kind);
    }

    [Fact]
    public void CanRoundTripTimeSpan()
    {
        var value = TimeSpan.FromDays(-3) + TimeSpan.FromSeconds(42) + TimeSpan.FromTicks(1234);

        var result = _runtime.CreateObject<Target>()
            .Invoke<TimeSpan, TimeSpan>(nameof(Target.EchoTimeSpan), value);

        Assert.Equal(value, result);
    }

    public void Dispose()
        => _runtime.Dispose();

    private sealed class Target
    {
        public Guid EchoGuid(Guid value)
            => value;

        public decimal EchoDecimal(decimal value)
            => value;

        public DateTime EchoDateTime(DateTime value)
            => value;

        public TimeSpan EchoTimeSpan(TimeSpan value)
            => value;
    }
}
