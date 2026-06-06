using DotNetIsolator.Test;
using Xunit;

namespace DotNetIsolator;

public sealed class PlainObjectSerializationTest : IDisposable
{
    private readonly IsolatedRuntime _runtime = new(SharedHost.Instance);

    [Fact]
    public void RoundTripsPrimitivePropertyObject()
    {
        // Distinct values across differently-named members catch any member-ordering mismatch
        // between the native writer and the positional host reader.
        var result = _runtime.CreateObject<Target>().Invoke<Sample>(nameof(Target.MakeSample));

        Assert.Equal(7, result.Id);
        Assert.Equal(3.5, result.Ratio);
        Assert.Equal(123456789L, result.Big);
        Assert.True(result.Flag);
        Assert.Equal((short)-9, result.Small);
    }

    [Fact]
    public void RoundTripsPrimitiveFieldObject()
    {
        var result = _runtime.CreateObject<Target>().Invoke<FieldSample>(nameof(Target.MakeFieldSample));

        Assert.Equal(11, result.Alpha);
        Assert.Equal(2.25f, result.Beta);
        Assert.Equal(-42L, result.Gamma);
    }

    [Fact]
    public void RoundTripsEdgeValues()
    {
        var result = _runtime.CreateObject<Target>().Invoke<Sample>(nameof(Target.MakeEdge));

        Assert.Equal(int.MinValue, result.Id);
        Assert.Equal(double.MaxValue, result.Ratio);
        Assert.Equal(long.MinValue, result.Big);
        Assert.False(result.Flag);
        Assert.Equal(short.MaxValue, result.Small);
    }

    [Fact]
    public void CharBearingObjectStillRoundTrips()
    {
        // char is written through the text encoding by the managed format, so a char-bearing object
        // bails to the managed serializer.
        var result = _runtime.CreateObject<Target>().Invoke<CharBearing>(nameof(Target.MakeCharBearing));

        Assert.Equal(42, result.Code);
        Assert.Equal('Z', result.Symbol);
    }

    [Fact]
    public void StringBearingObjectStillRoundTrips()
    {
        // Has a string member, so the native fast path bails and the managed path serializes it.
        var result = _runtime.CreateObject<Target>().Invoke<Mixed>(nameof(Target.MakeMixed));

        Assert.Equal(5, result.Number);
        Assert.Equal("hello world", result.Text);
        Assert.Equal(9.5, result.Value);
    }

    public void Dispose()
        => _runtime.Dispose();

    public sealed class Sample
    {
        public int Id { get; set; }
        public double Ratio { get; set; }
        public long Big { get; set; }
        public bool Flag { get; set; }
        public short Small { get; set; }
    }

    public sealed class CharBearing
    {
        public int Code { get; set; }
        public char Symbol { get; set; }
    }

    public sealed class FieldSample
    {
        public int Alpha;
        public float Beta;
        public long Gamma;
    }

    public sealed class Mixed
    {
        public int Number { get; set; }
        public string? Text { get; set; }
        public double Value { get; set; }
    }

    private sealed class Target
    {
        public Sample MakeSample() => new()
        {
            Id = 7,
            Ratio = 3.5,
            Big = 123456789L,
            Flag = true,
            Small = -9,
        };

        public Sample MakeEdge() => new()
        {
            Id = int.MinValue,
            Ratio = double.MaxValue,
            Big = long.MinValue,
            Flag = false,
            Small = short.MaxValue,
        };

        public FieldSample MakeFieldSample() => new() { Alpha = 11, Beta = 2.25f, Gamma = -42L };

        public Mixed MakeMixed() => new() { Number = 5, Text = "hello world", Value = 9.5 };

        public CharBearing MakeCharBearing() => new() { Code = 42, Symbol = 'Z' };
    }
}
