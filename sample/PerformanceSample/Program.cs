using System.Globalization;
using System.Runtime.InteropServices;

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
            StartupBenchmarks.ClearDirectory(options.CacheDirectory);
        }

        PrintEnvironment(options);

        var target = new BenchmarkTarget();
        var hostResult = WarmCallBenchmarks.MeasureDirectHost(target, options.HostIterations);
        var isolatedResult = WarmCallBenchmarks.MeasureIsolatedCalls(options, useModuleCache: true);
        var publicInvokeResult = WarmCallBenchmarks.MeasureIsolatedPublicInvokeCalls(options, useModuleCache: true);
        var batchResult = WarmCallBenchmarks.MeasureIsolatedBatchCalls(options, useModuleCache: true);
        var doubleScalarResult = WarmCallBenchmarks.MeasureIsolatedDoubleScalarCalls(options, useModuleCache: true);
        var longScalarResult = WarmCallBenchmarks.MeasureIsolatedLongScalarCalls(options, useModuleCache: true);
        var zeroArgResult = WarmCallBenchmarks.MeasureIsolatedZeroArgIntCalls(options, useModuleCache: true);
        var voidResult = WarmCallBenchmarks.MeasureIsolatedVoidCalls(options, useModuleCache: true);
        var intVoidResult = WarmCallBenchmarks.MeasureIsolatedIntVoidCalls(options, useModuleCache: true);
        var payloadResult = WarmCallBenchmarks.MeasureIsolatedPayloadCalls(options, useModuleCache: true);
        var objectPayloadResult = WarmCallBenchmarks.MeasureIsolatedObjectPayloadCalls(options, useModuleCache: true);
        var listPayloadResult = WarmCallBenchmarks.MeasureIsolatedListPayloadCalls(options, useModuleCache: true);
        var doubleArrayResult = WarmCallBenchmarks.MeasureIsolatedDoubleArrayPayloadCalls(options, useModuleCache: true);
        var longArrayResult = WarmCallBenchmarks.MeasureIsolatedLongArrayPayloadCalls(options, useModuleCache: true);
        var doubleArrayArgResult = WarmCallBenchmarks.MeasureIsolatedDoubleArrayArgCalls(options, useModuleCache: true);
        var typedCallbackResult = WarmCallBenchmarks.MeasureIsolatedTypedCallbackCalls(options, useModuleCache: true);
        var rawCallbackResult = WarmCallBenchmarks.MeasureIsolatedRawCallbackCalls(options, useModuleCache: true);
        var noCacheStartup = StartupBenchmarks.MeasureStartup(options, useModuleCache: false, clearCacheBeforeEachSample: false);

        if (options.ClearCacheBetweenScenarios)
        {
            StartupBenchmarks.ClearDirectory(options.CacheDirectory);
        }

        var coldCacheStartup = StartupBenchmarks.MeasureStartup(options, useModuleCache: true, clearCacheBeforeEachSample: true);

        if (options.ClearCacheBetweenScenarios)
        {
            StartupBenchmarks.ClearDirectory(options.CacheDirectory);
        }

        StartupBenchmarks.PrimeModuleCache(options);
        var warmCacheStartup = StartupBenchmarks.MeasureStartup(options, useModuleCache: true, clearCacheBeforeEachSample: false);
        var concurrentHostStartup = ConcurrentHostBenchmarks.MeasureWarmModuleCacheHostConstruction(options);
        var warmHostStartup = StartupBenchmarks.MeasureWarmHostStartup(options, useRuntimeMemorySnapshot: false);
        var snapshotPreload = StartupBenchmarks.MeasureRuntimeMemorySnapshotPreload(options);
        var snapshotStartup = StartupBenchmarks.MeasureWarmHostStartup(options, useRuntimeMemorySnapshot: true);
        var pooledStartup = StartupBenchmarks.MeasurePooledRuntimeStartup(options);

        Console.WriteLine();
        Console.WriteLine("Steady-state call overhead");
        PrintResult(hostResult);
        PrintResult(isolatedResult);
        PrintResult(publicInvokeResult);
        Console.WriteLine($"Isolated/direct mean ratio: {isolatedResult.MeanNanoseconds / hostResult.MeanNanoseconds:N0}x");

        Console.WriteLine();
        Console.WriteLine("Additional warm-call overhead");
        PrintResult(batchResult);
        PrintResult(doubleScalarResult);
        PrintResult(longScalarResult);
        PrintResult(zeroArgResult);
        PrintResult(voidResult);
        PrintResult(intVoidResult);
        PrintResult(payloadResult);
        PrintResult(objectPayloadResult);
        PrintResult(listPayloadResult);
        PrintResult(doubleArrayResult);
        PrintResult(longArrayResult);
        PrintResult(doubleArrayArgResult);
        PrintResult(typedCallbackResult);
        PrintResult(rawCallbackResult);

        Console.WriteLine();
        Console.WriteLine("Startup medians");
        PrintStartup("No module cache", noCacheStartup);
        PrintStartup("Cold module cache", coldCacheStartup);
        PrintStartup("Warm module cache", warmCacheStartup);
        PrintRuntimeStartup("Warm host", warmHostStartup);
        Console.WriteLine($"Runtime memory snapshot preload: {FormatDuration(snapshotPreload)}");
        PrintRuntimeStartup("Warm runtime memory snapshot", snapshotStartup);
        PrintRuntimeStartup("Warm instance pool", pooledStartup);

        Console.WriteLine();
        Console.WriteLine("Concurrent host construction");
        PrintResult(concurrentHostStartup);

        Console.WriteLine();
        Console.WriteLine($"Sink: {MeasurementSink.Value}");
        return 0;
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
        Console.WriteLine($"Concurrent hosts: {options.ConcurrentHosts:N0}");
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

}
