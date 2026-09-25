using System.Reflection;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace DotNetIsolator.WasmApp;

// One reflection entry per batch. The inner loop is a closed, strongly typed delegate.
public static class BatchDispatcher
{
    private delegate void Dispatch(object? target, nint args, nint results, int count);
    private static readonly Dictionary<MethodInfo, Dispatch> Dispatchers = new();

    [UnconditionalSuppressMessage("Trimming", "IL2076", Justification = "The native dispatcher validates both generic arguments as primitive scalar types before calling Run.")]
    public static void Run(MethodInfo method, object? target, nint args, nint results, int count)
    {
        if (!Dispatchers.TryGetValue(method, out var dispatch))
        {
            dispatch = (Dispatch)typeof(BatchDispatcher).GetMethod(nameof(Create), BindingFlags.NonPublic | BindingFlags.Static)!
                .MakeGenericMethod(method.GetParameters()[0].ParameterType, method.ReturnType)
                .Invoke(null, new object[] { method })!;
            Dispatchers.Add(method, dispatch);
        }
        dispatch(target, args, results, count);
    }

    private static unsafe Dispatch Create<TArg, TResult>(MethodInfo method)
        where TArg : unmanaged
        where TResult : unmanaged
    {
        // A cached bound delegate must not extend the lifetime of a released guest object.
        var instances = new ConditionalWeakTable<object, Func<TArg, TResult>>();
        var staticCall = method.IsStatic ? method.CreateDelegate<Func<TArg, TResult>>() : null;
        return (target, args, results, count) =>
        {
            var call = staticCall ?? instances.GetValue(target!, key => method.CreateDelegate<Func<TArg, TResult>>(key));
            var input = new ReadOnlySpan<TArg>((void*)args, count);
            var output = new Span<TResult>((void*)results, count);
            for (var i = 0; i < count; i++) output[i] = call(input[i]);
        };
    }
}
