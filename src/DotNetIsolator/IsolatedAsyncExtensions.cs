using DotNetIsolator.Internal;

namespace DotNetIsolator;

public static class IsolatedAsyncExtensions
{
    public static ValueTask<TRes> InvokeAsync<TRes>(this IsolatedObject instance, string methodName)
    {
        ArgumentNullException.ThrowIfNull(instance);
        return instance.FindMethod(methodName, 0).InvokeAsync<TRes>(instance);
    }

    public static ValueTask<TRes> InvokeAsync<T0, TRes>(this IsolatedObject instance, string methodName, T0 param0)
    {
        ArgumentNullException.ThrowIfNull(instance);
        return instance.FindMethod(methodName, 1).InvokeAsync<T0, TRes>(instance, param0);
    }

    public static ValueTask<TRes> InvokeAsync<T0, T1, TRes>(
        this IsolatedObject instance,
        string methodName,
        T0 param0,
        T1 param1)
    {
        ArgumentNullException.ThrowIfNull(instance);
        return instance.FindMethod(methodName, 2).InvokeAsync<T0, T1, TRes>(instance, param0, param1);
    }

    public static ValueTask<TRes> InvokeAsync<T0, T1, T2, TRes>(
        this IsolatedObject instance,
        string methodName,
        T0 param0,
        T1 param1,
        T2 param2)
    {
        ArgumentNullException.ThrowIfNull(instance);
        return instance.FindMethod(methodName, 3).InvokeAsync<T0, T1, T2, TRes>(instance, param0, param1, param2);
    }

    public static ValueTask<TRes> InvokeAsync<T0, T1, T2, T3, TRes>(
        this IsolatedObject instance,
        string methodName,
        T0 param0,
        T1 param1,
        T2 param2,
        T3 param3)
    {
        ArgumentNullException.ThrowIfNull(instance);
        return instance.FindMethod(methodName, 4).InvokeAsync<T0, T1, T2, T3, TRes>(
            instance,
            param0,
            param1,
            param2,
            param3);
    }

    public static ValueTask<TRes> InvokeAsync<T0, T1, T2, T3, T4, TRes>(
        this IsolatedObject instance,
        string methodName,
        T0 param0,
        T1 param1,
        T2 param2,
        T3 param3,
        T4 param4)
    {
        ArgumentNullException.ThrowIfNull(instance);
        return instance.FindMethod(methodName, 5).InvokeAsync<T0, T1, T2, T3, T4, TRes>(
            instance,
            param0,
            param1,
            param2,
            param3,
            param4);
    }

    public static ValueTask InvokeAsync(this IsolatedObject instance, string methodName)
    {
        _ = InvokeAsync<object?>(instance, methodName).GetAwaiter().GetResult();
        return ValueTask.CompletedTask;
    }

    public static ValueTask InvokeAsync<T0>(this IsolatedObject instance, string methodName, T0 param0)
    {
        _ = InvokeAsync<T0, object?>(instance, methodName, param0).GetAwaiter().GetResult();
        return ValueTask.CompletedTask;
    }

    public static ValueTask InvokeAsync<T0, T1>(this IsolatedObject instance, string methodName, T0 param0, T1 param1)
    {
        _ = InvokeAsync<T0, T1, object?>(instance, methodName, param0, param1).GetAwaiter().GetResult();
        return ValueTask.CompletedTask;
    }

    public static ValueTask InvokeAsync<T0, T1, T2>(
        this IsolatedObject instance,
        string methodName,
        T0 param0,
        T1 param1,
        T2 param2)
    {
        _ = InvokeAsync<T0, T1, T2, object?>(instance, methodName, param0, param1, param2).GetAwaiter().GetResult();
        return ValueTask.CompletedTask;
    }

    public static ValueTask InvokeAsync<T0, T1, T2, T3>(
        this IsolatedObject instance,
        string methodName,
        T0 param0,
        T1 param1,
        T2 param2,
        T3 param3)
    {
        _ = InvokeAsync<T0, T1, T2, T3, object?>(instance, methodName, param0, param1, param2, param3)
            .GetAwaiter()
            .GetResult();
        return ValueTask.CompletedTask;
    }

    public static ValueTask InvokeAsync<T0, T1, T2, T3, T4>(
        this IsolatedObject instance,
        string methodName,
        T0 param0,
        T1 param1,
        T2 param2,
        T3 param3,
        T4 param4)
    {
        _ = InvokeAsync<T0, T1, T2, T3, T4, object?>(instance, methodName, param0, param1, param2, param3, param4)
            .GetAwaiter()
            .GetResult();
        return ValueTask.CompletedTask;
    }

    public static ValueTask<TRes> InvokeAsync<TRes>(this IsolatedMethod method, IsolatedObject? instance)
    {
        ArgumentNullException.ThrowIfNull(method);
        return new ValueTask<TRes>(method.Runtime.InvokeDotNetMethod<TRes>(
            method.MethodPointer,
            instance,
            Span<int>.Empty));
    }

    public static ValueTask<TRes> InvokeAsync<T0, TRes>(this IsolatedMethod method, IsolatedObject? instance, T0 param0)
    {
        ArgumentNullException.ThrowIfNull(method);
        Span<int> argAddresses = stackalloc int[1];
        argAddresses[0] = CopyArgument(method, param0);
        try
        {
            return new ValueTask<TRes>(method.Runtime.InvokeDotNetMethod<TRes>(
                method.MethodPointer,
                instance,
                argAddresses));
        }
        finally
        {
            method.Runtime.Free(argAddresses[0]);
        }
    }

    public static ValueTask<TRes> InvokeAsync<T0, T1, TRes>(
        this IsolatedMethod method,
        IsolatedObject? instance,
        T0 param0,
        T1 param1)
    {
        ArgumentNullException.ThrowIfNull(method);
        Span<int> argAddresses = stackalloc int[2];
        argAddresses[0] = CopyArgument(method, param0);
        argAddresses[1] = CopyArgument(method, param1);
        try
        {
            return new ValueTask<TRes>(method.Runtime.InvokeDotNetMethod<TRes>(
                method.MethodPointer,
                instance,
                argAddresses));
        }
        finally
        {
            method.Runtime.Free(argAddresses[0]);
            method.Runtime.Free(argAddresses[1]);
        }
    }

    public static ValueTask<TRes> InvokeAsync<T0, T1, T2, TRes>(
        this IsolatedMethod method,
        IsolatedObject? instance,
        T0 param0,
        T1 param1,
        T2 param2)
    {
        ArgumentNullException.ThrowIfNull(method);
        Span<int> argAddresses = stackalloc int[3];
        argAddresses[0] = CopyArgument(method, param0);
        argAddresses[1] = CopyArgument(method, param1);
        argAddresses[2] = CopyArgument(method, param2);
        try
        {
            return new ValueTask<TRes>(method.Runtime.InvokeDotNetMethod<TRes>(
                method.MethodPointer,
                instance,
                argAddresses));
        }
        finally
        {
            method.Runtime.Free(argAddresses[0]);
            method.Runtime.Free(argAddresses[1]);
            method.Runtime.Free(argAddresses[2]);
        }
    }

    public static ValueTask<TRes> InvokeAsync<T0, T1, T2, T3, TRes>(
        this IsolatedMethod method,
        IsolatedObject? instance,
        T0 param0,
        T1 param1,
        T2 param2,
        T3 param3)
    {
        ArgumentNullException.ThrowIfNull(method);
        Span<int> argAddresses = stackalloc int[4];
        argAddresses[0] = CopyArgument(method, param0);
        argAddresses[1] = CopyArgument(method, param1);
        argAddresses[2] = CopyArgument(method, param2);
        argAddresses[3] = CopyArgument(method, param3);
        try
        {
            return new ValueTask<TRes>(method.Runtime.InvokeDotNetMethod<TRes>(
                method.MethodPointer,
                instance,
                argAddresses));
        }
        finally
        {
            method.Runtime.Free(argAddresses[0]);
            method.Runtime.Free(argAddresses[1]);
            method.Runtime.Free(argAddresses[2]);
            method.Runtime.Free(argAddresses[3]);
        }
    }

    public static ValueTask<TRes> InvokeAsync<T0, T1, T2, T3, T4, TRes>(
        this IsolatedMethod method,
        IsolatedObject? instance,
        T0 param0,
        T1 param1,
        T2 param2,
        T3 param3,
        T4 param4)
    {
        ArgumentNullException.ThrowIfNull(method);
        Span<int> argAddresses = stackalloc int[5];
        argAddresses[0] = CopyArgument(method, param0);
        argAddresses[1] = CopyArgument(method, param1);
        argAddresses[2] = CopyArgument(method, param2);
        argAddresses[3] = CopyArgument(method, param3);
        argAddresses[4] = CopyArgument(method, param4);
        try
        {
            return new ValueTask<TRes>(method.Runtime.InvokeDotNetMethod<TRes>(
                method.MethodPointer,
                instance,
                argAddresses));
        }
        finally
        {
            method.Runtime.Free(argAddresses[0]);
            method.Runtime.Free(argAddresses[1]);
            method.Runtime.Free(argAddresses[2]);
            method.Runtime.Free(argAddresses[3]);
            method.Runtime.Free(argAddresses[4]);
        }
    }

    private static int CopyArgument<T>(IsolatedMethod method, T value)
        => method.Runtime.CopyValueLengthPrefixed(MessagePackCompatibility.SerializeTypeless(value));
}
