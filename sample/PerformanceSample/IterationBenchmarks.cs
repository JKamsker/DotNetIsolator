using DotNetIsolator;
using System.Runtime.InteropServices;

namespace PerformanceSample;

internal static class IterationBenchmarks
{
    public static void Run(bool typedOnly = false, bool batchOnly = false)
    {
        Console.WriteLine($"{RuntimeInformation.FrameworkDescription}; {RuntimeInformation.OSDescription}; {RuntimeInformation.ProcessArchitecture}");
        using var host = new IsolatedRuntimeHost().WithBinDirectoryAssemblyLoader();
        using var runtime = new IsolatedRuntime(host);
        runtime.RegisterCallback("add2", (int a, int b) => a + b);
        runtime.RegisterCallback("add3", (int a, double b, long c) => a + b + c);
        runtime.RegisterCallback("add4", (int a, long b, short c, byte d) => a + b + c + d);
        runtime.RegisterCallback("nested", (NestedPayload payload) => payload);
        runtime.RegisterCallback("consume", (Action<int>)(a => MeasurementSink.Consume(a)));
        using var target = runtime.CreateObject<BenchmarkTarget>();
        var control = target.FindMethod(nameof(BenchmarkTarget.Increment), 1);
        var c2 = target.FindMethod(nameof(BenchmarkTarget.Callback2), 1);
        var c3 = target.FindMethod(nameof(BenchmarkTarget.Callback3), 1);
        var c4 = target.FindMethod(nameof(BenchmarkTarget.Callback4), 1);
        var cv = target.FindMethod(nameof(BenchmarkTarget.CallbackVoid), 1);
        var nested = target.FindMethod(nameof(BenchmarkTarget.CallbackNested), 0);
        var echoText = target.FindMethod(nameof(BenchmarkTarget.EchoText), 1);
        var textLength = target.FindMethod(nameof(BenchmarkTarget.TextLength), 1);
        var consumeText = target.FindMethod(nameof(BenchmarkTarget.ConsumeText), 1);
        var shortText = target.FindMethod(nameof(BenchmarkTarget.ReturnShortString), 0);
        var longText = target.FindMethod(nameof(BenchmarkTarget.ReturnLongString), 0);
        var strings = target.FindMethod(nameof(BenchmarkTarget.EchoStrings), 1);
        var dictionary = target.FindMethod(nameof(BenchmarkTarget.EchoDictionary), 1);
        if (typedOnly)
        {
            var t2 = target.FindMethod(nameof(BenchmarkTarget.TypedCallback2), 1);
            var t3 = target.FindMethod(nameof(BenchmarkTarget.TypedCallback3), 1);
            var t4 = target.FindMethod(nameof(BenchmarkTarget.TypedCallback4), 1);
            FastPathBenchmarks.Measure("int control", 1_000_000, () => control.Invoke<int, int>(target, 7));
            FastPathBenchmarks.Measure("typed callback arity 2", 100_000, () => t2.Invoke<int, int>(target, 7));
            FastPathBenchmarks.Measure("typed callback arity 3", 100_000, () => (long)t3.Invoke<int, double>(target, 7));
            FastPathBenchmarks.Measure("typed callback arity 4", 100_000, () => t4.Invoke<int, long>(target, 7));
            Console.WriteLine($"Sink: {MeasurementSink.Value}");
            return;
        }
        var list = Enumerable.Range(0, 256).Select(x => $"item-{x}").ToList();
        var map = list.ToDictionary(x => x, x => x.Length);
        var batch = Enumerable.Range(0, 1024).ToArray();
        FastPathBenchmarks.Measure("int control", 1_000_000, () => control.Invoke<int, int>(target, 7));
        if (batchOnly)
        {
            FastPathBenchmarks.Measure("batch 1024 per element", 5000, () => control.InvokeBatch<int, int>(target, batch)[1023], 1024);
            if (typeof(IsolatedMethod).GetMethods().Any(m => m.Name == "InvokeBatch" && m.GetParameters().Length == 3))
                MeasureBatchDestination(control, target, batch);
            Console.WriteLine($"Sink: {MeasurementSink.Value}");
            return;
        }
        FastPathBenchmarks.Measure("callback arity 2", 50_000, () => c2.Invoke<int, int>(target, 7));
        FastPathBenchmarks.Measure("callback arity 3", 50_000, () => (long)c3.Invoke<int, double>(target, 7));
        FastPathBenchmarks.Measure("callback arity 4", 50_000, () => c4.Invoke<int, long>(target, 7));
        FastPathBenchmarks.Measure("callback void", 50_000, () => cv.Invoke<int, int>(target, 7));
        FastPathBenchmarks.Measure("batch 1024 per element", 5000, () => control.InvokeBatch<int, int>(target, batch)[1023], 1024);
        FastPathBenchmarks.Measure("nested DTO callback (32 KiB)", 500, () => nested.Invoke<int>(target));
        FastPathBenchmarks.Measure("List<string>[256] roundtrip", 200, () => strings.Invoke<List<string>, List<string>>(target, list).Count);
        FastPathBenchmarks.Measure("Dictionary<string,int>[256] roundtrip", 200, () => dictionary.Invoke<Dictionary<string, int>, Dictionary<string, int>>(target, map).Count);
        FastPathBenchmarks.Measure("string return (32 chars)", 50_000, () => shortText.Invoke<string>(target).Length);
        FastPathBenchmarks.Measure("string return (32 KiB UTF-8)", 1000, () => longText.Invoke<string>(target).Length);
        const string text = "abcdefghijklmnopqrstuvwxyz012345";
        FastPathBenchmarks.Measure("string[32] -> string", 20_000, () => echoText.Invoke<string, string>(target, text).Length);
        FastPathBenchmarks.Measure("string[32] -> int", 20_000, () => textLength.Invoke<string, int>(target, text));
        FastPathBenchmarks.Measure("string[32] -> void", 20_000, () => { consumeText.InvokeVoid(target, text); return 0; });
        var smallList = list.Take(8).ToList();
        var smallMap = smallList.ToDictionary(x => x, x => x.Length);
        FastPathBenchmarks.Measure("List<string>[8] roundtrip", 2000, () => strings.Invoke<List<string>, List<string>>(target, smallList).Count);
        FastPathBenchmarks.Measure("Dictionary<string,int>[8] roundtrip", 2000, () => dictionary.Invoke<Dictionary<string, int>, Dictionary<string, int>>(target, smallMap).Count);
        Console.WriteLine($"Sink: {MeasurementSink.Value}");
    }
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void MeasureBatchDestination(IsolatedMethod method, IsolatedObject target, int[] args)
    {
        var destination = new int[args.Length];
        FastPathBenchmarks.Measure("batch 1024 into span per element", 5000, () =>
        {
            method.InvokeBatch<int, int>(target, args, destination);
            return destination[^1];
        }, args.Length);
    }

}
