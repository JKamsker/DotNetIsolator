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

    public static Measurement MeasureIsolatedPublicInvokeCalls(BenchmarkOptions options, bool useModuleCache)
    {
        using var host = CreateHost(options, useModuleCache);
        using var runtime = new IsolatedRuntime(host);
        var target = runtime.CreateObject<BenchmarkTarget>();

        long sum = 0;
        for (var i = 0; i < 10; i++)
        {
            sum += target.Invoke<int, int>(nameof(BenchmarkTarget.Increment), i);
        }

        var elapsed = Time(() =>
        {
            for (var i = 0; i < options.IsolatedIterations; i++)
            {
                sum += target.Invoke<int, int>(nameof(BenchmarkTarget.Increment), i);
            }
        });

        target.ReleaseGCHandle();
        MeasurementSink.Consume(sum);
        return new Measurement("Isolated warm-runtime public Invoke Increment", options.IsolatedIterations, elapsed);
    }

    public static Measurement MeasureIsolatedDoubleScalarCalls(BenchmarkOptions options, bool useModuleCache)
    {
        using var host = CreateHost(options, useModuleCache);
        using var runtime = new IsolatedRuntime(host);
        var target = runtime.CreateObject<BenchmarkTarget>();
        var method = target.FindMethod(nameof(BenchmarkTarget.IncrementDouble), 1);

        var sum = 0.0;
        for (var i = 0; i < 10; i++)
        {
            sum += method.Invoke<double, double>(target, i);
        }

        var elapsed = Time(() =>
        {
            for (var i = 0; i < options.IsolatedIterations; i++)
            {
                sum += method.Invoke<double, double>(target, i);
            }
        });

        target.ReleaseGCHandle();
        MeasurementSink.Consume((long)sum);
        return new Measurement("Isolated warm-runtime double scalar call", options.IsolatedIterations, elapsed);
    }

    public static Measurement MeasureIsolatedLongScalarCalls(BenchmarkOptions options, bool useModuleCache)
    {
        using var host = CreateHost(options, useModuleCache);
        using var runtime = new IsolatedRuntime(host);
        var target = runtime.CreateObject<BenchmarkTarget>();
        var method = target.FindMethod(nameof(BenchmarkTarget.IncrementLong), 1);

        long sum = 0;
        for (var i = 0; i < 10; i++)
        {
            sum += method.Invoke<long, long>(target, i);
        }

        var elapsed = Time(() =>
        {
            for (var i = 0; i < options.IsolatedIterations; i++)
            {
                sum += method.Invoke<long, long>(target, i);
            }
        });

        target.ReleaseGCHandle();
        MeasurementSink.Consume(sum);
        return new Measurement("Isolated warm-runtime long scalar call", options.IsolatedIterations, elapsed);
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

    public static Measurement MeasureIsolatedVoidCalls(BenchmarkOptions options, bool useModuleCache)
    {
        using var host = CreateHost(options, useModuleCache);
        using var runtime = new IsolatedRuntime(host);
        var target = runtime.CreateObject<BenchmarkTarget>();
        var method = target.FindMethod(nameof(BenchmarkTarget.Noop), 0);

        for (var i = 0; i < 10; i++)
        {
            method.InvokeVoid(target);
        }
        var elapsed = Time(() =>
        {
            for (var i = 0; i < options.ZeroArgIterations; i++)
            {
                method.InvokeVoid(target);
            }
        });

        target.ReleaseGCHandle();
        return new Measurement("Isolated warm-runtime void call", options.ZeroArgIterations, elapsed);
    }

    public static Measurement MeasureIsolatedIntVoidCalls(BenchmarkOptions options, bool useModuleCache)
    {
        using var host = CreateHost(options, useModuleCache);
        using var runtime = new IsolatedRuntime(host);
        var target = runtime.CreateObject<BenchmarkTarget>();
        var method = target.FindMethod(nameof(BenchmarkTarget.ConsumeInt), 1);

        for (var i = 0; i < 10; i++)
        {
            method.InvokeVoid<int>(target, i);
        }
        var elapsed = Time(() =>
        {
            for (var i = 0; i < options.IsolatedIterations; i++)
            {
                method.InvokeVoid<int>(target, i);
            }
        });

        target.ReleaseGCHandle();
        return new Measurement("Isolated warm-runtime int void call", options.IsolatedIterations, elapsed);
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

    public static Measurement MeasureIsolatedListPayloadCalls(BenchmarkOptions options, bool useModuleCache)
    {
        using var host = CreateHost(options, useModuleCache);
        using var runtime = new IsolatedRuntime(host);
        var target = runtime.CreateObject<BenchmarkTarget>();
        var method = target.FindMethod(nameof(BenchmarkTarget.ReturnNumbers), 0);

        long sum = 0;
        for (var i = 0; i < 3; i++)
        {
            var numbers = method.Invoke<List<int>>(target);
            sum += numbers.Count + numbers[0] + numbers[^1];
        }

        var elapsed = Time(() =>
        {
            for (var i = 0; i < options.PayloadIterations; i++)
            {
                var numbers = method.Invoke<List<int>>(target);
                sum += numbers.Count + numbers[0] + numbers[^1];
            }
        });

        target.ReleaseGCHandle();
        MeasurementSink.Consume(sum);
        return new Measurement("Isolated warm-runtime generic List<int>[1024] return", options.PayloadIterations, elapsed);
    }

    public static Measurement MeasureIsolatedDoubleArrayPayloadCalls(BenchmarkOptions options, bool useModuleCache)
    {
        using var host = CreateHost(options, useModuleCache);
        using var runtime = new IsolatedRuntime(host);
        var target = runtime.CreateObject<BenchmarkTarget>();
        var method = target.FindMethod(nameof(BenchmarkTarget.ReturnDoubles), 0);

        var sum = 0.0;
        for (var i = 0; i < 3; i++)
        {
            var values = method.Invoke<double[]>(target);
            sum += values.Length + values[^1];
        }

        var elapsed = Time(() =>
        {
            for (var i = 0; i < options.PayloadIterations; i++)
            {
                var values = method.Invoke<double[]>(target);
                sum += values.Length + values[^1];
            }
        });

        target.ReleaseGCHandle();
        MeasurementSink.Consume((long)sum);
        return new Measurement(
            $"Isolated warm-runtime generic double[{BenchmarkTarget.BlittableArrayLength}] return",
            options.PayloadIterations,
            elapsed);
    }

    public static Measurement MeasureIsolatedLongArrayPayloadCalls(BenchmarkOptions options, bool useModuleCache)
    {
        using var host = CreateHost(options, useModuleCache);
        using var runtime = new IsolatedRuntime(host);
        var target = runtime.CreateObject<BenchmarkTarget>();
        var method = target.FindMethod(nameof(BenchmarkTarget.ReturnLongs), 0);

        long sum = 0;
        for (var i = 0; i < 3; i++)
        {
            var values = method.Invoke<long[]>(target);
            sum += values.Length + values[^1];
        }

        var elapsed = Time(() =>
        {
            for (var i = 0; i < options.PayloadIterations; i++)
            {
                var values = method.Invoke<long[]>(target);
                sum += values.Length + values[^1];
            }
        });

        target.ReleaseGCHandle();
        MeasurementSink.Consume(sum);
        return new Measurement(
            $"Isolated warm-runtime generic long[{BenchmarkTarget.BlittableArrayLength}] return",
            options.PayloadIterations,
            elapsed);
    }

    public static Measurement MeasureIsolatedDoubleArrayArgCalls(BenchmarkOptions options, bool useModuleCache)
    {
        using var host = CreateHost(options, useModuleCache);
        using var runtime = new IsolatedRuntime(host);
        var target = runtime.CreateObject<BenchmarkTarget>();
        var method = target.FindMethod(nameof(BenchmarkTarget.SumDoubles), 1);
        var payload = new double[BenchmarkTarget.BlittableArrayLength];
        for (var i = 0; i < payload.Length; i++)
        {
            payload[i] = i * 0.25;
        }

        var sum = 0.0;
        for (var i = 0; i < 3; i++)
        {
            sum += method.Invoke<double[], double>(target, payload);
        }

        var elapsed = Time(() =>
        {
            for (var i = 0; i < options.PayloadIterations; i++)
            {
                sum += method.Invoke<double[], double>(target, payload);
            }
        });

        target.ReleaseGCHandle();
        MeasurementSink.Consume((long)sum);
        return new Measurement(
            $"Isolated warm-runtime double[{BenchmarkTarget.BlittableArrayLength}] argument",
            options.PayloadIterations,
            elapsed);
    }

    public static Measurement MeasureIsolatedTypedCallbackCalls(BenchmarkOptions options, bool useModuleCache)
    {
        using var host = CreateHost(options, useModuleCache);
        using var runtime = new IsolatedRuntime(host);
        runtime.RegisterCallback("increment-callback", (int value) => value + 1);
        var target = runtime.CreateObject<BenchmarkTarget>();
        var method = target.FindMethod(nameof(BenchmarkTarget.CallIncrementCallback), 1);

        long sum = 0;
        for (var i = 0; i < 3; i++)
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
        return new Measurement("Isolated warm-runtime typed host callback", options.IsolatedIterations, elapsed);
    }

    public static Measurement MeasureIsolatedRawCallbackCalls(BenchmarkOptions options, bool useModuleCache)
    {
        using var host = CreateHost(options, useModuleCache);
        using var runtime = new IsolatedRuntime(host);
        runtime.RegisterCallback("raw-buffer-callback", (byte[] buffer) => buffer);
        var target = runtime.CreateObject<BenchmarkTarget>();
        var method = target.FindMethod(nameof(BenchmarkTarget.CallRawBufferCallback), 0);

        long sum = 0;
        for (var i = 0; i < 3; i++)
        {
            sum += method.Invoke<int>(target);
        }

        var elapsed = Time(() =>
        {
            for (var i = 0; i < options.PayloadIterations; i++)
            {
                sum += method.Invoke<int>(target);
            }
        });

        target.ReleaseGCHandle();
        MeasurementSink.Consume(sum);
        return new Measurement("Isolated warm-runtime raw byte[65536] host callback", options.PayloadIterations, elapsed);
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
