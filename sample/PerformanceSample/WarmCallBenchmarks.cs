using System.Diagnostics;
using DotNetIsolator;

namespace PerformanceSample;

internal static class WarmCallBenchmarks
{
    public static Measurement MeasureDirectHost(BenchmarkTarget target, int iterations)
    {
        long sum = 0;
        for (var i = 0; i < 1_000; i++)
        {
            sum += target.Increment(i);
        }

        var elapsed = Time(() =>
        {
            for (var i = 0; i < iterations; i++)
            {
                sum += target.Increment(i);
            }
        });

        MeasurementSink.Consume(sum);
        return new Measurement("Direct host Increment", iterations, elapsed);
    }

    public static Measurement MeasureIsolatedCalls(BenchmarkOptions options, bool useModuleCache)
    {
        using var host = CreateHost(options, useModuleCache);
        using var runtime = new IsolatedRuntime(host);
        var target = runtime.CreateObject<BenchmarkTarget>();
        var method = target.FindMethod(nameof(BenchmarkTarget.Increment), 1);

        long sum = 0;
        for (var i = 0; i < 10; i++)
        {
            sum += method.Invoke<int, int>(target, i);
        }

        var elapsed = Time(() =>
        {
            for (var i = 0; i < options.IsolatedIterations; i++)
            {
                sum += method.Invoke<int, int>(target, i);
            }
        });

        target.ReleaseGCHandle();
        MeasurementSink.Consume(sum);
        return new Measurement("Isolated warm-runtime Increment", options.IsolatedIterations, elapsed);
    }

    public static Measurement MeasureIsolatedZeroArgIntCalls(BenchmarkOptions options, bool useModuleCache)
    {
        using var host = CreateHost(options, useModuleCache);
        using var runtime = new IsolatedRuntime(host);
        var target = runtime.CreateObject<BenchmarkTarget>();
        var method = target.FindMethod(nameof(BenchmarkTarget.ReturnFixedValue), 0);

        long sum = 0;
        for (var i = 0; i < 10; i++)
        {
            sum += method.Invoke<int>(target);
        }

        var elapsed = Time(() =>
        {
            for (var i = 0; i < options.ZeroArgIterations; i++)
            {
                sum += method.Invoke<int>(target);
            }
        });

        target.ReleaseGCHandle();
        MeasurementSink.Consume(sum);
        return new Measurement("Isolated warm-runtime zero-arg int return", options.ZeroArgIterations, elapsed);
    }

    public static Measurement MeasureIsolatedPayloadCalls(BenchmarkOptions options, bool useModuleCache)
    {
        using var host = CreateHost(options, useModuleCache);
        using var runtime = new IsolatedRuntime(host);
        var target = runtime.CreateObject<BenchmarkTarget>();
        var method = target.FindMethod(nameof(BenchmarkTarget.ReturnBuffer), 0);

        long sum = 0;
        for (var i = 0; i < 3; i++)
        {
            sum += method.Invoke<byte[]>(target).Length;
        }

        var elapsed = Time(() =>
        {
            for (var i = 0; i < options.PayloadIterations; i++)
            {
                sum += method.Invoke<byte[]>(target).Length;
            }
        });

        target.ReleaseGCHandle();
        MeasurementSink.Consume(sum);
        return new Measurement("Isolated warm-runtime generic byte[4096] return", options.PayloadIterations, elapsed);
    }

    public static Measurement MeasureIsolatedObjectPayloadCalls(BenchmarkOptions options, bool useModuleCache)
    {
        using var host = CreateHost(options, useModuleCache);
        using var runtime = new IsolatedRuntime(host);
        var target = runtime.CreateObject<BenchmarkTarget>();
        var method = target.FindMethod(nameof(BenchmarkTarget.ReturnPayload), 0);

        long sum = 0;
        for (var i = 0; i < 3; i++)
        {
            sum += method.Invoke<BenchmarkPayload>(target).Checksum;
        }

        var elapsed = Time(() =>
        {
            for (var i = 0; i < options.PayloadIterations; i++)
            {
                sum += method.Invoke<BenchmarkPayload>(target).Checksum;
            }
        });

        target.ReleaseGCHandle();
        MeasurementSink.Consume(sum);
        return new Measurement("Isolated warm-runtime generic object return", options.PayloadIterations, elapsed);
    }

    private static IsolatedRuntimeHost CreateHost(BenchmarkOptions options, bool useModuleCache)
        => new IsolatedRuntimeHost(new IsolatedRuntimeHostOptions
        {
            UsePrecompiledModuleCache = useModuleCache,
            PrecompiledModuleCacheDirectory = options.CacheDirectory,
        }).WithBinDirectoryAssemblyLoader();

    private static TimeSpan Time(Action action)
    {
        var stopwatch = Stopwatch.StartNew();
        action();
        return stopwatch.Elapsed;
    }
}
