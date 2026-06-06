using DotNetIsolator.Internal;

namespace DotNetIsolator;

public class IsolatedMethod
{
    private readonly IsolatedRuntime _runtimeInstance;
    private readonly int _monoMethodPtr;

    public string Name { get; }

    internal IsolatedMethod(IsolatedRuntime runtimeInstance, string name, int monoMethodPtr)
    {
        Name = name;
        _runtimeInstance = runtimeInstance;
        _monoMethodPtr = monoMethodPtr;
    }

    public TRes Invoke<TRes>(IsolatedObject? instance)
    {
        if (typeof(TRes) == typeof(int))
        {
            var result = _runtimeInstance.InvokeInt32Method(_monoMethodPtr, instance);
            return (TRes)(object)result;
        }

        if (typeof(TRes) == typeof(byte[]))
        {
            var result = _runtimeInstance.InvokeByteArrayMethod(_monoMethodPtr, instance);
            return (TRes)(object)result!;
        }

        return _runtimeInstance.InvokeDotNetMethod<TRes>(_monoMethodPtr, instance, Span<int>.Empty);
    }

    public TRes Invoke<T0, TRes>(IsolatedObject? instance, T0 param0)
    {
        if (typeof(T0) == typeof(int) && typeof(TRes) == typeof(int))
        {
            var result = _runtimeInstance.InvokeInt32Method(
                _monoMethodPtr,
                instance,
                (int)(object)param0!);
            return (TRes)(object)result;
        }

        // Ideally we'd serialize directly into guest memory but that probably involves implementing
        // an IBufferWriter<byte> that knows how to allocate chunks of guest memory
        // We might also want to special-case some basic known parameter types and skip MessagePack
        // for them, instead using ShadowStack and the raw bytes
        Span<int> argAddresses = stackalloc int[1];
        argAddresses[0] = _runtimeInstance.CopyValueLengthPrefixed(
            MessagePackCompatibility.SerializeTypeless(param0));

        try
        {
            return _runtimeInstance.InvokeDotNetMethod<TRes>(_monoMethodPtr, instance, argAddresses);
        }
        finally
        {
            _runtimeInstance.Free(argAddresses[0]);
        }
    }

    public TRes Invoke<T0, T1, TRes>(IsolatedObject? instance, T0 param0, T1 param1)
    {
        Span<int> argAddresses = stackalloc int[2];
        argAddresses[0] = _runtimeInstance.CopyValueLengthPrefixed(
            MessagePackCompatibility.SerializeTypeless(param0));
        argAddresses[1] = _runtimeInstance.CopyValueLengthPrefixed(
            MessagePackCompatibility.SerializeTypeless(param1));

        try
        {
            return _runtimeInstance.InvokeDotNetMethod<TRes>(_monoMethodPtr, instance, argAddresses);
        }
        finally
        {
            _runtimeInstance.Free(argAddresses[0]);
            _runtimeInstance.Free(argAddresses[1]);
        }
    }

    public TRes Invoke<T0, T1, T2, TRes>(IsolatedObject? instance, T0 param0, T1 param1, T2 param2)
    {
        Span<int> argAddresses = stackalloc int[3];
        argAddresses[0] = _runtimeInstance.CopyValueLengthPrefixed(
            MessagePackCompatibility.SerializeTypeless(param0));
        argAddresses[1] = _runtimeInstance.CopyValueLengthPrefixed(
            MessagePackCompatibility.SerializeTypeless(param1));
        argAddresses[2] = _runtimeInstance.CopyValueLengthPrefixed(
            MessagePackCompatibility.SerializeTypeless(param2));

        try
        {
            return _runtimeInstance.InvokeDotNetMethod<TRes>(_monoMethodPtr, instance, argAddresses);
        }
        finally
        {
            _runtimeInstance.Free(argAddresses[0]);
            _runtimeInstance.Free(argAddresses[1]);
            _runtimeInstance.Free(argAddresses[2]);
        }
    }

    public TRes Invoke<T0, T1, T2, T3, TRes>(IsolatedObject? instance, T0 param0, T1 param1, T2 param2, T3 param3)
    {
        Span<int> argAddresses = stackalloc int[4];
        argAddresses[0] = _runtimeInstance.CopyValueLengthPrefixed(
            MessagePackCompatibility.SerializeTypeless(param0));
        argAddresses[1] = _runtimeInstance.CopyValueLengthPrefixed(
            MessagePackCompatibility.SerializeTypeless(param1));
        argAddresses[2] = _runtimeInstance.CopyValueLengthPrefixed(
            MessagePackCompatibility.SerializeTypeless(param2));
        argAddresses[3] = _runtimeInstance.CopyValueLengthPrefixed(
            MessagePackCompatibility.SerializeTypeless(param3));

        try
        {
            return _runtimeInstance.InvokeDotNetMethod<TRes>(_monoMethodPtr, instance, argAddresses);
        }
        finally
        {
            _runtimeInstance.Free(argAddresses[0]);
            _runtimeInstance.Free(argAddresses[1]);
            _runtimeInstance.Free(argAddresses[2]);
            _runtimeInstance.Free(argAddresses[3]);
        }
    }

    public TRes Invoke<T0, T1, T2, T3, T4, TRes>(IsolatedObject? instance, T0 param0, T1 param1, T2 param2, T3 param3, T4 param4)
    {
        Span<int> argAddresses = stackalloc int[5];
        argAddresses[0] = _runtimeInstance.CopyValueLengthPrefixed(
            MessagePackCompatibility.SerializeTypeless(param0));
        argAddresses[1] = _runtimeInstance.CopyValueLengthPrefixed(
            MessagePackCompatibility.SerializeTypeless(param1));
        argAddresses[2] = _runtimeInstance.CopyValueLengthPrefixed(
            MessagePackCompatibility.SerializeTypeless(param2));
        argAddresses[3] = _runtimeInstance.CopyValueLengthPrefixed(
            MessagePackCompatibility.SerializeTypeless(param3));
        argAddresses[4] = _runtimeInstance.CopyValueLengthPrefixed(
            MessagePackCompatibility.SerializeTypeless(param4));

        try
        {
            return _runtimeInstance.InvokeDotNetMethod<TRes>(_monoMethodPtr, instance, argAddresses);
        }
        finally
        {
            _runtimeInstance.Free(argAddresses[0]);
            _runtimeInstance.Free(argAddresses[1]);
            _runtimeInstance.Free(argAddresses[2]);
            _runtimeInstance.Free(argAddresses[3]);
            _runtimeInstance.Free(argAddresses[4]);
        }
    }

    public void InvokeVoid(IsolatedObject? instance)
        => _runtimeInstance.InvokeVoidMethod(_monoMethodPtr, instance);

    public void InvokeVoid<T0>(IsolatedObject? instance, T0 param0)
    {
        if (typeof(T0) == typeof(int))
        {
            _runtimeInstance.InvokeVoidMethod(_monoMethodPtr, instance, (int)(object)param0!);
            return;
        }

        Invoke<T0, object>(instance, param0);
    }

    public void InvokeVoid<T0, T1>(IsolatedObject? instance, T0 param0, T1 param1)
        => Invoke<T0, T1, object>(instance, param0, param1);

    public void InvokeVoid<T0, T1, T2>(IsolatedObject? instance, T0 param0, T1 param1, T2 param2)
        => Invoke<T0, T1, T2, object>(instance, param0, param1, param2);

    public void InvokeVoid<T0, T1, T2, T3>(IsolatedObject? instance, T0 param0, T1 param1, T2 param2, T3 param3)
        => Invoke<T0, T1, T2, T3, object>(instance, param0, param1, param2, param3);

    public void InvokeVoid<T0, T1, T2, T3, T4>(IsolatedObject? instance, T0 param0, T1 param1, T2 param2, T3 param3, T4 param4)
        => Invoke<T0, T1, T2, T3, T4, object>(instance, param0, param1, param2, param3, param4);
}
