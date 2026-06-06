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

        if (TryInvokeBlittableArray<TRes>(instance, out var blittableArray))
        {
            return blittableArray;
        }

        return _runtimeInstance.InvokeDotNetMethod<TRes>(_monoMethodPtr, instance, Span<int>.Empty);
    }

    // Routes exact () -> T[] calls for blittable primitive element types through the native
    // zero-copy array fast path. byte[] keeps its own dedicated path above. The typeof checks
    // collapse to the single matching branch for each reference-typed instantiation.
    private bool TryInvokeBlittableArray<TRes>(IsolatedObject? instance, out TRes result)
    {
        if (typeof(TRes) == typeof(int[])) { result = (TRes)(object)_runtimeInstance.InvokeBlittableArrayMethod<int>(_monoMethodPtr, instance)!; return true; }
        if (typeof(TRes) == typeof(uint[])) { result = (TRes)(object)_runtimeInstance.InvokeBlittableArrayMethod<uint>(_monoMethodPtr, instance)!; return true; }
        if (typeof(TRes) == typeof(long[])) { result = (TRes)(object)_runtimeInstance.InvokeBlittableArrayMethod<long>(_monoMethodPtr, instance)!; return true; }
        if (typeof(TRes) == typeof(ulong[])) { result = (TRes)(object)_runtimeInstance.InvokeBlittableArrayMethod<ulong>(_monoMethodPtr, instance)!; return true; }
        if (typeof(TRes) == typeof(short[])) { result = (TRes)(object)_runtimeInstance.InvokeBlittableArrayMethod<short>(_monoMethodPtr, instance)!; return true; }
        if (typeof(TRes) == typeof(ushort[])) { result = (TRes)(object)_runtimeInstance.InvokeBlittableArrayMethod<ushort>(_monoMethodPtr, instance)!; return true; }
        if (typeof(TRes) == typeof(double[])) { result = (TRes)(object)_runtimeInstance.InvokeBlittableArrayMethod<double>(_monoMethodPtr, instance)!; return true; }
        if (typeof(TRes) == typeof(float[])) { result = (TRes)(object)_runtimeInstance.InvokeBlittableArrayMethod<float>(_monoMethodPtr, instance)!; return true; }
        if (typeof(TRes) == typeof(char[])) { result = (TRes)(object)_runtimeInstance.InvokeBlittableArrayMethod<char>(_monoMethodPtr, instance)!; return true; }
        if (typeof(TRes) == typeof(bool[])) { result = (TRes)(object)_runtimeInstance.InvokeBlittableArrayMethod<bool>(_monoMethodPtr, instance)!; return true; }
        if (typeof(TRes) == typeof(sbyte[])) { result = (TRes)(object)_runtimeInstance.InvokeBlittableArrayMethod<sbyte>(_monoMethodPtr, instance)!; return true; }

        result = default!;
        return false;
    }

    // Element-kind tags shared with the native element_class_for_kind switch and the managed
    // ObjectGraphPrimitiveCollections codec. Do not renumber.
    private const int KindBoolean = 1, KindSByte = 2, KindByte = 3, KindInt16 = 4, KindUInt16 = 5, KindChar = 6,
        KindInt32 = 7, KindUInt32 = 8, KindInt64 = 9, KindUInt64 = 10, KindSingle = 11, KindDouble = 12;

    // Routes exact (T[]) -> TRes calls for blittable primitive element types through the native
    // zero-copy argument path. A null array falls back to the managed path so null is preserved.
    private bool TryInvokeBlittableArrayArg<T0, TRes>(IsolatedObject? instance, T0 param0, out TRes result)
    {
        if (typeof(T0) == typeof(int[])) return TryArrayArg<int, TRes>(instance, param0, KindInt32, out result);
        if (typeof(T0) == typeof(uint[])) return TryArrayArg<uint, TRes>(instance, param0, KindUInt32, out result);
        if (typeof(T0) == typeof(long[])) return TryArrayArg<long, TRes>(instance, param0, KindInt64, out result);
        if (typeof(T0) == typeof(ulong[])) return TryArrayArg<ulong, TRes>(instance, param0, KindUInt64, out result);
        if (typeof(T0) == typeof(short[])) return TryArrayArg<short, TRes>(instance, param0, KindInt16, out result);
        if (typeof(T0) == typeof(ushort[])) return TryArrayArg<ushort, TRes>(instance, param0, KindUInt16, out result);
        if (typeof(T0) == typeof(double[])) return TryArrayArg<double, TRes>(instance, param0, KindDouble, out result);
        if (typeof(T0) == typeof(float[])) return TryArrayArg<float, TRes>(instance, param0, KindSingle, out result);
        if (typeof(T0) == typeof(char[])) return TryArrayArg<char, TRes>(instance, param0, KindChar, out result);
        if (typeof(T0) == typeof(bool[])) return TryArrayArg<bool, TRes>(instance, param0, KindBoolean, out result);
        if (typeof(T0) == typeof(sbyte[])) return TryArrayArg<sbyte, TRes>(instance, param0, KindSByte, out result);

        result = default!;
        return false;
    }

    private bool TryArrayArg<T, TRes>(IsolatedObject? instance, object? param0, int elementKind, out TRes result) where T : unmanaged
    {
        if (param0 is not T[] array)
        {
            // Null (or unexpected) array: let the managed path serialize it so null is preserved.
            result = default!;
            return false;
        }

        result = _runtimeInstance.InvokeBlittableArrayArgMethod<T, TRes>(_monoMethodPtr, instance, array, elementKind);
        return true;
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

        if (TryInvokeBlittableArrayArg<T0, TRes>(instance, param0, out var arrayArgResult))
        {
            return arrayArgResult;
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
