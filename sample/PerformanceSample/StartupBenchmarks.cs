using System.Diagnostics;
using DotNetIsolator;

namespace PerformanceSample;

internal static class StartupBenchmarks
{
    public static StartupMeasurement MeasureStartup(
        BenchmarkOptions options,
        bool useModuleCache,
        bool clearCacheBeforeEachSample)
    {
        var hostTimes = new List<TimeSpan>();
        var runtimeTimes = new List<TimeSpan>();
        var objectTimes = new List<TimeSpan>();
        var methodTimes = new List<TimeSpan>();
        var firstCallTimes = new List<TimeSpan>();

        for (var i = 0; i < options.StartupIterations; i++)
        {
            if (clearCacheBeforeEachSample)
            {
                ClearDirectory(options.CacheDirectory);
            }

            var stopwatch = Stopwatch.StartNew();
            using var host = CreateHost(options, useModuleCache);
            hostTimes.Add(stopwatch.Elapsed);

            stopwatch.Restart();
            using var runtime = new IsolatedRuntime(host);
            runtimeTimes.Add(stopwatch.Elapsed);

            stopwatch.Restart();
            var target = runtime.CreateObject<BenchmarkTarget>();
            objectTimes.Add(stopwatch.Elapsed);

            stopwatch.Restart();
            var method = target.FindMethod(nameof(BenchmarkTarget.Increment), 1);
            methodTimes.Add(stopwatch.Elapsed);

            stopwatch.Restart();
            MeasurementSink.Consume(method.Invoke<int, int>(target, i));
            firstCallTimes.Add(stopwatch.Elapsed);

            target.ReleaseGCHandle();
        }

        return new StartupMeasurement(
            Median(hostTimes),
            Median(runtimeTimes),
            Median(objectTimes),
            Median(methodTimes),
            Median(firstCallTimes));
    }

    public static StartupMeasurement MeasureWarmHostStartup(BenchmarkOptions options, bool useRuntimeMemorySnapshot)
    {
        var runtimeTimes = new List<TimeSpan>();
        var objectTimes = new List<TimeSpan>();
        var methodTimes = new List<TimeSpan>();
        var firstCallTimes = new List<TimeSpan>();

        using var host = CreateHost(options, useModuleCache: true, useRuntimeMemorySnapshot);
        if (useRuntimeMemorySnapshot)
        {
            host.PreloadRuntimeMemorySnapshot();
        }

        for (var i = 0; i < options.StartupIterations; i++)
        {
            MeasureRuntimeStartupOnHost(host, i, runtimeTimes, objectTimes, methodTimes, firstCallTimes);
        }

        return new StartupMeasurement(
            Host: TimeSpan.Zero,
            Median(runtimeTimes),
            Median(objectTimes),
            Median(methodTimes),
            Median(firstCallTimes));
    }

    public static StartupMeasurement MeasurePooledRuntimeStartup(BenchmarkOptions options)
    {
        var runtimeTimes = new List<TimeSpan>();
        var objectTimes = new List<TimeSpan>();
        var methodTimes = new List<TimeSpan>();
        var firstCallTimes = new List<TimeSpan>();

        using var host = new IsolatedRuntimeHost(new IsolatedRuntimeHostOptions
        {
            UsePrecompiledModuleCache = true,
            PrecompiledModuleCacheDirectory = options.CacheDirectory,
            UseRuntimeMemorySnapshot = true,
            UseInstancePool = true,
        }).WithBinDirectoryAssemblyLoader();
        host.PreloadRuntimeMemorySnapshot();

        // Warm the pool so the measured iterations exercise instance reuse, not first instantiation.
        using (var _ = new IsolatedRuntime(host))
        {
        }

        for (var i = 0; i < options.StartupIterations; i++)
        {
            MeasureRuntimeStartupOnHost(host, i, runtimeTimes, objectTimes, methodTimes, firstCallTimes);
        }

        return new StartupMeasurement(
            Host: TimeSpan.Zero,
            Median(runtimeTimes),
            Median(objectTimes),
            Median(methodTimes),
            Median(firstCallTimes));
    }

    public static TimeSpan MeasureRuntimeMemorySnapshotPreload(BenchmarkOptions options)
    {
        using var host = CreateHost(options, useModuleCache: true, useRuntimeMemorySnapshot: true);
        return Time(host.PreloadRuntimeMemorySnapshot);
    }

    public static void PrimeModuleCache(BenchmarkOptions options)
    {
        using var host = CreateHost(options, useModuleCache: true);
    }

    public static void ClearDirectory(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        DeleteMatchingFiles(directory, "*.cwasm");
        DeleteMatchingFiles(directory, "*.tmp");
    }

    private static void MeasureRuntimeStartupOnHost(
        IsolatedRuntimeHost host,
        int sample,
        List<TimeSpan> runtimeTimes,
        List<TimeSpan> objectTimes,
        List<TimeSpan> methodTimes,
        List<TimeSpan> firstCallTimes)
    {
        var stopwatch = Stopwatch.StartNew();
        using var runtime = new IsolatedRuntime(host);
        runtimeTimes.Add(stopwatch.Elapsed);

        stopwatch.Restart();
        var target = runtime.CreateObject<BenchmarkTarget>();
        objectTimes.Add(stopwatch.Elapsed);

        stopwatch.Restart();
        var method = target.FindMethod(nameof(BenchmarkTarget.Increment), 1);
        methodTimes.Add(stopwatch.Elapsed);

        stopwatch.Restart();
        MeasurementSink.Consume(method.Invoke<int, int>(target, sample));
        firstCallTimes.Add(stopwatch.Elapsed);

        target.ReleaseGCHandle();
    }

    private static IsolatedRuntimeHost CreateHost(
        BenchmarkOptions options,
        bool useModuleCache,
        bool useRuntimeMemorySnapshot = false)
        => new IsolatedRuntimeHost(new IsolatedRuntimeHostOptions
        {
            UsePrecompiledModuleCache = useModuleCache,
            PrecompiledModuleCacheDirectory = options.CacheDirectory,
            UseRuntimeMemorySnapshot = useRuntimeMemorySnapshot,
        }).WithBinDirectoryAssemblyLoader();

    private static TimeSpan Time(Action action)
    {
        var stopwatch = Stopwatch.StartNew();
        action();
        return stopwatch.Elapsed;
    }

    private static TimeSpan Median(List<TimeSpan> values)
    {
        values.Sort();
        return values[values.Count / 2];
    }

    private static void DeleteMatchingFiles(string directory, string pattern)
    {
        foreach (var file in Directory.EnumerateFiles(directory, pattern))
        {
            File.Delete(file);
        }
    }
}
