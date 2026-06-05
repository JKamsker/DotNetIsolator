using System.Diagnostics;
using DotNetIsolator;

namespace PerformanceSample;

internal static class ConcurrentHostBenchmarks
{
    public static Measurement MeasureWarmModuleCacheHostConstruction(BenchmarkOptions options)
    {
        var elapsed = Time(() =>
        {
            Parallel.For(
                0,
                options.ConcurrentHosts,
                new ParallelOptions { MaxDegreeOfParallelism = options.ConcurrentHosts },
                _ =>
                {
                    using var host = new IsolatedRuntimeHost(new IsolatedRuntimeHostOptions
                    {
                        UsePrecompiledModuleCache = true,
                        PrecompiledModuleCacheDirectory = options.CacheDirectory,
                    });
                });
        });

        return new Measurement(
            $"Warm module cache parallel host construction ({options.ConcurrentHosts} hosts)",
            options.ConcurrentHosts,
            elapsed);
    }

    private static TimeSpan Time(Action action)
    {
        var stopwatch = Stopwatch.StartNew();
        action();
        return stopwatch.Elapsed;
    }
}
