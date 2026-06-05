using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using DotNetIsolator;

namespace PerformanceSample;

internal static class Program
{
    public static int Main(string[] args)
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;

        BenchmarkOptions options;
        try
        {
            options = BenchmarkOptions.Parse(args);
        }
        catch (HelpRequestedException)
        {
            Console.WriteLine(BenchmarkOptions.Usage);
            return 0;
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine(BenchmarkOptions.Usage);
            return 2;
        }

        if (options.ClearCache)
        {
            ClearDirectory(options.CacheDirectory);
        }

        PrintEnvironment(options);

        var target = new BenchmarkTarget();
        var hostResult = WarmCallBenchmarks.MeasureDirectHost(target, options.HostIterations);
        var isolatedResult = WarmCallBenchmarks.MeasureIsolatedCalls(options, useModuleCache: true);
        var publicInvokeResult = WarmCallBenchmarks.MeasureIsolatedPublicInvokeCalls(options, useModuleCache: true);
        var zeroArgResult = WarmCallBenchmarks.MeasureIsolatedZeroArgIntCalls(options, useModuleCache: true);
        var payloadResult = WarmCallBenchmarks.MeasureIsolatedPayloadCalls(options, useModuleCache: true);
        var objectPayloadResult = WarmCallBenchmarks.MeasureIsolatedObjectPayloadCalls(options, useModuleCache: true);
        var listPayloadResult = WarmCallBenchmarks.MeasureIsolatedListPayloadCalls(options, useModuleCache: true);
        var noCacheStartup = MeasureStartup(options, useModuleCache: false, clearCacheBeforeEachSample: false);

        if (options.ClearCacheBetweenScenarios)
        {
            ClearDirectory(options.CacheDirectory);
        }

        var coldCacheStartup = MeasureStartup(options, useModuleCache: true, clearCacheBeforeEachSample: true);

        if (options.ClearCacheBetweenScenarios)
        {
            ClearDirectory(options.CacheDirectory);
        }

        PrimeModuleCache(options);
        var warmCacheStartup = MeasureStartup(options, useModuleCache: true, clearCacheBeforeEachSample: false);
        var warmHostStartup = MeasureWarmHostStartup(options, useRuntimeMemorySnapshot: false);
        var snapshotPreload = MeasureRuntimeMemorySnapshotPreload(options);
        var snapshotStartup = MeasureWarmHostStartup(options, useRuntimeMemorySnapshot: true);

        Console.WriteLine();
        Console.WriteLine("Steady-state call overhead");
        PrintResult(hostResult);
        PrintResult(isolatedResult);
        PrintResult(publicInvokeResult);
        Console.WriteLine($"Isolated/direct mean ratio: {isolatedResult.MeanNanoseconds / hostResult.MeanNanoseconds:N0}x");

        Console.WriteLine();
        Console.WriteLine("Additional warm-call overhead");
        PrintResult(zeroArgResult);
        PrintResult(payloadResult);
        PrintResult(objectPayloadResult);
        PrintResult(listPayloadResult);

        Console.WriteLine();
        Console.WriteLine("Startup medians");
        PrintStartup("No module cache", noCacheStartup);
        PrintStartup("Cold module cache", coldCacheStartup);
        PrintStartup("Warm module cache", warmCacheStartup);
        PrintRuntimeStartup("Warm host", warmHostStartup);
        Console.WriteLine($"Runtime memory snapshot preload: {FormatDuration(snapshotPreload)}");
        PrintRuntimeStartup("Warm runtime memory snapshot", snapshotStartup);

        Console.WriteLine();
        Console.WriteLine($"Sink: {MeasurementSink.Value}");
        return 0;
    }

    private static StartupMeasurement MeasureStartup(
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

    private static StartupMeasurement MeasureWarmHostStartup(BenchmarkOptions options, bool useRuntimeMemorySnapshot)
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

    private static TimeSpan MeasureRuntimeMemorySnapshotPreload(BenchmarkOptions options)
    {
        using var host = CreateHost(options, useModuleCache: true, useRuntimeMemorySnapshot: true);
        return Time(host.PreloadRuntimeMemorySnapshot);
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

    private static void PrimeModuleCache(BenchmarkOptions options)
    {
        using var host = CreateHost(options, useModuleCache: true);
    }

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

    private static void PrintEnvironment(BenchmarkOptions options)
    {
        Console.WriteLine("DotNetIsolator performance sample");
        Console.WriteLine($"Runtime: {RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"OS: {RuntimeInformation.OSDescription}");
        Console.WriteLine($"RID: {RuntimeInformation.RuntimeIdentifier}");
        Console.WriteLine($"Process architecture: {RuntimeInformation.ProcessArchitecture}");
        Console.WriteLine($"Host iterations: {options.HostIterations:N0}");
        Console.WriteLine($"Isolated iterations: {options.IsolatedIterations:N0}");
        Console.WriteLine($"Zero-arg iterations: {options.ZeroArgIterations:N0}");
        Console.WriteLine($"Payload iterations: {options.PayloadIterations:N0}");
        Console.WriteLine($"Startup iterations: {options.StartupIterations:N0}");
        Console.WriteLine($"Module cache directory: {options.CacheDirectory}");
    }

    private static void PrintResult(Measurement measurement)
    {
        Console.WriteLine(
            $"{measurement.Name}: total {measurement.Elapsed.TotalMilliseconds:N3} ms, mean {FormatDuration(measurement.MeanNanoseconds)}");
    }

    private static void PrintStartup(string name, StartupMeasurement measurement)
    {
        Console.WriteLine(
            $"{name}: host {FormatDuration(measurement.Host)}, runtime {FormatDuration(measurement.Runtime)}, " +
            $"object {FormatDuration(measurement.Object)}, method {FormatDuration(measurement.Method)}, first call {FormatDuration(measurement.FirstCall)}");
    }

    private static void PrintRuntimeStartup(string name, StartupMeasurement measurement)
    {
        Console.WriteLine(
            $"{name}: runtime {FormatDuration(measurement.Runtime)}, object {FormatDuration(measurement.Object)}, " +
            $"method {FormatDuration(measurement.Method)}, first call {FormatDuration(measurement.FirstCall)}");
    }

    private static string FormatDuration(TimeSpan value)
        => FormatDuration(value.TotalNanoseconds);

    private static string FormatDuration(double totalNanoseconds)
    {
        if (totalNanoseconds >= 1_000_000)
        {
            return $"{totalNanoseconds / 1_000_000:N3} ms";
        }

        if (totalNanoseconds >= 1_000)
        {
            return $"{totalNanoseconds / 1_000:N3} us";
        }

        return $"{totalNanoseconds:N3} ns";
    }

    private static void ClearDirectory(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        DeleteMatchingFiles(directory, "*.cwasm");
        DeleteMatchingFiles(directory, "*.tmp");
    }

    private static void DeleteMatchingFiles(string directory, string pattern)
    {
        foreach (var file in Directory.EnumerateFiles(directory, pattern))
        {
            File.Delete(file);
        }
    }

}
