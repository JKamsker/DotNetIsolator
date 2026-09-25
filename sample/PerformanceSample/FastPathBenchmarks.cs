using System.Diagnostics;
using System.Runtime.InteropServices;
using DotNetIsolator;

namespace PerformanceSample;

// Fixed workloads for reproducible before/after comparisons. Five samples after warmup;
// allocations are host-thread bytes only (they do not include the guest heap).
internal static class FastPathBenchmarks
{
    public static void Run()
    {
        Console.WriteLine($"{RuntimeInformation.FrameworkDescription}; {RuntimeInformation.OSDescription}; {RuntimeInformation.ProcessArchitecture}");
        using var host = new IsolatedRuntimeHost().WithBinDirectoryAssemblyLoader();
        using var runtime = new IsolatedRuntime(host);
        runtime.RegisterCallback("increment-callback", (int x) => x + 1);
        var target = runtime.CreateObject<BenchmarkTarget>();
        IsolatedMethod Method(string name, int count) => target.FindMethod(name, count);
        var i32 = Method(nameof(BenchmarkTarget.Increment), 1);
        var f64 = Method(nameof(BenchmarkTarget.IncrementDouble), 1);
        var i64 = Method(nameof(BenchmarkTarget.IncrementLong), 1);
        var add2 = Method(nameof(BenchmarkTarget.Add2), 2);
        var add3 = Method(nameof(BenchmarkTarget.Add3), 3);
        var add4 = Method(nameof(BenchmarkTarget.Add4), 4);
        var callback = Method(nameof(BenchmarkTarget.CallIncrementCallback), 1);
        var typed = Method(nameof(BenchmarkTarget.CallTypedIncrementCallback), 1);
        var length = Method(nameof(BenchmarkTarget.ArrayLength), 1);
        var consume = Method(nameof(BenchmarkTarget.ConsumeArray), 1);
        var echo = Method(nameof(BenchmarkTarget.EchoArray), 1);
        var listCount = Method(nameof(BenchmarkTarget.ListCount), 1);
        var nested = Method(nameof(BenchmarkTarget.EchoNested), 1);
        var array = Enumerable.Range(0, 32).Select(x => (double)x).ToArray();
        var list = new List<double>(array);
        var payload = new NestedPayload
        {
            Values = new double[4096],
            Items = Enumerable.Range(0, 8).Select(x => new BenchmarkPayload { Id = x, Name = "nested" }).ToList(),
        };
        var batch = Enumerable.Range(0, 1024).ToArray();
        Measure("int -> int", 200_000, () => i32.Invoke<int, int>(target, 7));
        Measure("double -> double", 200_000, () => (long)f64.Invoke<double, double>(target, 7));
        Measure("long -> long", 200_000, () => i64.Invoke<long, long>(target, 7));
        Measure("scalar arity 2", 20_000, () => add2.Invoke<int, int, int>(target, 3, 4));
        Measure("scalar arity 3", 20_000, () => (long)add3.Invoke<int, double, long, double>(target, 3, 4, 5));
        Measure("scalar arity 4", 20_000, () => add4.Invoke<int, long, short, byte, long>(target, 3, 4, 5, 6));
        Measure("callback params", 50_000, () => callback.Invoke<int, int>(target, 7));
        Measure("callback typed", 50_000, () => typed.Invoke<int, int>(target, 7));
        Measure("double[32] -> int", 10_000, () => length.Invoke<double[], int>(target, array));
        Measure("double[32] -> void", 10_000, () => { consume.InvokeVoid(target, array); return 0; });
        Measure("double[32] -> double[]", 10_000, () => echo.Invoke<double[], double[]>(target, array).Length);
        Measure("List<double>[32] -> int", 10_000, () => listCount.Invoke<List<double>, int>(target, list));
        Measure("nested DTO roundtrip (32 KiB)", 500, () => nested.Invoke<NestedPayload, NestedPayload>(target, payload).Values.Length);
        Measure("batch int[1024] (per element)", 1000, () => i32.InvokeBatch<int, int>(target, batch)[1023], 1024);
        target.ReleaseGCHandle();
        Console.WriteLine($"Sink: {MeasurementSink.Value}");
    }

    private static void Measure(string name, int iterations, Func<long> call, int operations = 1)
    {
        long sum = 0;
        for (var i = 0; i < Math.Min(iterations, 2000); i++) sum += call();
        var times = new double[5];
        var allocations = new double[5];
        for (var sample = 0; sample < times.Length; sample++)
        {
            var bytes = GC.GetAllocatedBytesForCurrentThread();
            var start = Stopwatch.GetTimestamp();
            for (var i = 0; i < iterations; i++) sum += call();
            times[sample] = Stopwatch.GetElapsedTime(start).TotalNanoseconds / iterations / operations;
            allocations[sample] = (double)(GC.GetAllocatedBytesForCurrentThread() - bytes) / iterations / operations;
        }
        Array.Sort(times);
        Array.Sort(allocations);
        MeasurementSink.Consume(sum);
        Console.WriteLine($"{name}: median {times[2]:F1} ns/op; range {times[0]:F1}–{times[4]:F1}; {allocations[2]:F1} host B/op");
    }
}
