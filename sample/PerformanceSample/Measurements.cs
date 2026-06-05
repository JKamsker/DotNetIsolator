namespace PerformanceSample;

internal sealed record Measurement(string Name, int Iterations, TimeSpan Elapsed)
{
    public double MeanNanoseconds => Elapsed.TotalNanoseconds / Iterations;
}

internal sealed record StartupMeasurement(
    TimeSpan Host,
    TimeSpan Runtime,
    TimeSpan Object,
    TimeSpan Method,
    TimeSpan FirstCall);
