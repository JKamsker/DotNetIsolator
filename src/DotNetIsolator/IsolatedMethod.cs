using DotNetIsolator.Internal;
using System.Runtime.InteropServices;

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

        // Non-int primitive () -> TRes returns (int uses the dedicated packed path above).
        if (TryGetScalarKind<TRes>(out var resultKind))
        {
            var resultBits = _runtimeInstance.InvokeScalarMethod(_monoMethodPtr, instance, 0, 0, resultKind);
            return UnpackScalarResult<TRes>(resultBits);
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

        if (typeof(TRes) == typeof(List<int>)) { result = (TRes)(object)_runtimeInstance.InvokeBlittableListMethod<int>(_monoMethodPtr, instance)!; return true; }
        if (typeof(TRes) == typeof(List<uint>)) { result = (TRes)(object)_runtimeInstance.InvokeBlittableListMethod<uint>(_monoMethodPtr, instance)!; return true; }
        if (typeof(TRes) == typeof(List<long>)) { result = (TRes)(object)_runtimeInstance.InvokeBlittableListMethod<long>(_monoMethodPtr, instance)!; return true; }
        if (typeof(TRes) == typeof(List<ulong>)) { result = (TRes)(object)_runtimeInstance.InvokeBlittableListMethod<ulong>(_monoMethodPtr, instance)!; return true; }
        if (typeof(TRes) == typeof(List<short>)) { result = (TRes)(object)_runtimeInstance.InvokeBlittableListMethod<short>(_monoMethodPtr, instance)!; return true; }
        if (typeof(TRes) == typeof(List<ushort>)) { result = (TRes)(object)_runtimeInstance.InvokeBlittableListMethod<ushort>(_monoMethodPtr, instance)!; return true; }
        if (typeof(TRes) == typeof(List<double>)) { result = (TRes)(object)_runtimeInstance.InvokeBlittableListMethod<double>(_monoMethodPtr, instance)!; return true; }
        if (typeof(TRes) == typeof(List<float>)) { result = (TRes)(object)_runtimeInstance.InvokeBlittableListMethod<float>(_monoMethodPtr, instance)!; return true; }
        if (typeof(TRes) == typeof(List<char>)) { result = (TRes)(object)_runtimeInstance.InvokeBlittableListMethod<char>(_monoMethodPtr, instance)!; return true; }
        if (typeof(TRes) == typeof(List<bool>)) { result = (TRes)(object)_runtimeInstance.InvokeBlittableListMethod<bool>(_monoMethodPtr, instance)!; return true; }
        if (typeof(TRes) == typeof(List<byte>)) { result = (TRes)(object)_runtimeInstance.InvokeBlittableListMethod<byte>(_monoMethodPtr, instance)!; return true; }
        if (typeof(TRes) == typeof(List<sbyte>)) { result = (TRes)(object)_runtimeInstance.InvokeBlittableListMethod<sbyte>(_monoMethodPtr, instance)!; return true; }

        result = default!;
        return false;
    }

    // Element-kind tags shared with the native element_class_for_kind switch and the managed
    // ObjectGraphPrimitiveCollections codec. Do not renumber.
    private const int KindBoolean = 1, KindSByte = 2, KindByte = 3, KindInt16 = 4, KindUInt16 = 5, KindChar = 6,
        KindInt32 = 7, KindUInt32 = 8, KindInt64 = 9, KindUInt64 = 10, KindSingle = 11, KindDouble = 12;

    // Routes primitive arrays and lists through bulk argument transport. Null collections retain
    // the managed fallback so their null value is preserved.
    private bool TryInvokeBlittableArrayArg<T0, TRes>(IsolatedObject? instance, T0 param0, out TRes result, bool isVoid = false)
    {
        if ((typeof(T0) == typeof(int[]) || typeof(T0) == typeof(List<int>))) return TryArrayArg<int, TRes>(instance, param0, KindInt32, out result, isVoid);
        if ((typeof(T0) == typeof(uint[]) || typeof(T0) == typeof(List<uint>))) return TryArrayArg<uint, TRes>(instance, param0, KindUInt32, out result, isVoid);
        if ((typeof(T0) == typeof(long[]) || typeof(T0) == typeof(List<long>))) return TryArrayArg<long, TRes>(instance, param0, KindInt64, out result, isVoid);
        if ((typeof(T0) == typeof(ulong[]) || typeof(T0) == typeof(List<ulong>))) return TryArrayArg<ulong, TRes>(instance, param0, KindUInt64, out result, isVoid);
        if ((typeof(T0) == typeof(short[]) || typeof(T0) == typeof(List<short>))) return TryArrayArg<short, TRes>(instance, param0, KindInt16, out result, isVoid);
        if ((typeof(T0) == typeof(ushort[]) || typeof(T0) == typeof(List<ushort>))) return TryArrayArg<ushort, TRes>(instance, param0, KindUInt16, out result, isVoid);
        if ((typeof(T0) == typeof(double[]) || typeof(T0) == typeof(List<double>))) return TryArrayArg<double, TRes>(instance, param0, KindDouble, out result, isVoid);
        if ((typeof(T0) == typeof(float[]) || typeof(T0) == typeof(List<float>))) return TryArrayArg<float, TRes>(instance, param0, KindSingle, out result, isVoid);
        if ((typeof(T0) == typeof(char[]) || typeof(T0) == typeof(List<char>))) return TryArrayArg<char, TRes>(instance, param0, KindChar, out result, isVoid);
        if ((typeof(T0) == typeof(bool[]) || typeof(T0) == typeof(List<bool>))) return TryArrayArg<bool, TRes>(instance, param0, KindBoolean, out result, isVoid);
        if ((typeof(T0) == typeof(byte[]) || typeof(T0) == typeof(List<byte>))) return TryArrayArg<byte, TRes>(instance, param0, KindByte, out result, isVoid);
        if ((typeof(T0) == typeof(sbyte[]) || typeof(T0) == typeof(List<sbyte>))) return TryArrayArg<sbyte, TRes>(instance, param0, KindSByte, out result, isVoid);

        result = default!;
        return false;
    }

    private bool TryArrayArg<T, TRes>(IsolatedObject? instance, object? param0, int elementKind, out TRes result, bool isVoid) where T : unmanaged
    {
        if (param0 is List<T> list)
        {
            result = _runtimeInstance.InvokeBlittableArrayArgMethod<T, TRes>(_monoMethodPtr, instance, CollectionsMarshal.AsSpan(list), elementKind, out var supported, isList: true, isVoid: isVoid);
            return supported;
        }
        if (param0 is not T[] array)
        {
            // Null (or unexpected) array: let the managed path serialize it so null is preserved.
            result = default!;
            return false;
        }

        result = _runtimeInstance.InvokeBlittableArrayArgMethod<T, TRes>(_monoMethodPtr, instance, array, elementKind, out _, isVoid: isVoid);
        return true;
    }

    // Bit-packs a primitive scalar argument into a 64-bit register and reports its element kind.
    private static bool TryPackScalarArg<T0>(T0 value, out long bits, out int kind)
    {
        if (typeof(T0) == typeof(int)) { bits = (uint)(int)(object)value!; kind = KindInt32; return true; }
        if (typeof(T0) == typeof(uint)) { bits = (uint)(object)value!; kind = KindUInt32; return true; }
        if (typeof(T0) == typeof(long)) { bits = (long)(object)value!; kind = KindInt64; return true; }
        if (typeof(T0) == typeof(ulong)) { bits = unchecked((long)(ulong)(object)value!); kind = KindUInt64; return true; }
        if (typeof(T0) == typeof(short)) { bits = (ushort)(short)(object)value!; kind = KindInt16; return true; }
        if (typeof(T0) == typeof(ushort)) { bits = (ushort)(object)value!; kind = KindUInt16; return true; }
        if (typeof(T0) == typeof(byte)) { bits = (byte)(object)value!; kind = KindByte; return true; }
        if (typeof(T0) == typeof(sbyte)) { bits = (byte)(sbyte)(object)value!; kind = KindSByte; return true; }
        if (typeof(T0) == typeof(bool)) { bits = (bool)(object)value! ? 1L : 0L; kind = KindBoolean; return true; }
        if (typeof(T0) == typeof(char)) { bits = (char)(object)value!; kind = KindChar; return true; }
        if (typeof(T0) == typeof(float)) { bits = BitConverter.SingleToUInt32Bits((float)(object)value!); kind = KindSingle; return true; }
        if (typeof(T0) == typeof(double)) { bits = BitConverter.DoubleToInt64Bits((double)(object)value!); kind = KindDouble; return true; }
        bits = 0;
        kind = 0;
        return false;
    }

    // Reports the element kind for a primitive scalar type, or false if it is not a primitive scalar.
    private static bool TryGetScalarKind<T>(out int kind)
    {
        kind = typeof(T) == typeof(int) ? KindInt32
            : typeof(T) == typeof(uint) ? KindUInt32
            : typeof(T) == typeof(long) ? KindInt64
            : typeof(T) == typeof(ulong) ? KindUInt64
            : typeof(T) == typeof(short) ? KindInt16
            : typeof(T) == typeof(ushort) ? KindUInt16
            : typeof(T) == typeof(byte) ? KindByte
            : typeof(T) == typeof(sbyte) ? KindSByte
            : typeof(T) == typeof(bool) ? KindBoolean
            : typeof(T) == typeof(char) ? KindChar
            : typeof(T) == typeof(float) ? KindSingle
            : typeof(T) == typeof(double) ? KindDouble
            : 0;
        return kind != 0;
    }

    // Reconstructs a primitive scalar result from its bit-packed register form.
    private static TRes UnpackScalarResult<TRes>(long bits)
    {
        if (typeof(TRes) == typeof(int)) return (TRes)(object)(int)bits;
        if (typeof(TRes) == typeof(uint)) return (TRes)(object)(uint)bits;
        if (typeof(TRes) == typeof(long)) return (TRes)(object)bits;
        if (typeof(TRes) == typeof(ulong)) return (TRes)(object)unchecked((ulong)bits);
        if (typeof(TRes) == typeof(short)) return (TRes)(object)(short)bits;
        if (typeof(TRes) == typeof(ushort)) return (TRes)(object)(ushort)bits;
        if (typeof(TRes) == typeof(byte)) return (TRes)(object)(byte)bits;
        if (typeof(TRes) == typeof(sbyte)) return (TRes)(object)unchecked((sbyte)bits);
        if (typeof(TRes) == typeof(bool)) return (TRes)(object)(bits != 0);
        if (typeof(TRes) == typeof(char)) return (TRes)(object)(char)bits;
        if (typeof(TRes) == typeof(float)) return (TRes)(object)BitConverter.UInt32BitsToSingle((uint)bits);
        if (typeof(TRes) == typeof(double)) return (TRes)(object)BitConverter.Int64BitsToDouble(bits);
        return default!;
    }

    private int CopyArgument<T>(T value)
    {
        using var buffer = ObjectGraphSerializer.SerializeWithTypeBuffer(value);
        return _runtimeInstance.CopyValueLengthPrefixed(buffer.Span);
    }

    private void FreeArguments(ReadOnlySpan<int> argAddresses)
    {
        foreach (var argAddress in argAddresses)
        {
            if (argAddress != 0)
            {
                _runtimeInstance.Free(argAddress);
            }
        }
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

        // Any primitive (T0) -> TRes call (int -> int uses the dedicated packed path above).
        if (TryPackScalarArg(param0, out var argBits, out var argKind) && TryGetScalarKind<TRes>(out var scalarResultKind))
        {
            var resultBits = _runtimeInstance.InvokeScalarMethod(_monoMethodPtr, instance, argBits, argKind, scalarResultKind);
            return UnpackScalarResult<TRes>(resultBits);
        }

        // Ideally we'd serialize directly into guest memory but that probably involves implementing
        // an IBufferWriter<byte> that knows how to allocate chunks of guest memory
        // We might also want to special-case some basic known parameter types and skip MessagePack
        // for them, instead using ShadowStack and the raw bytes
        Span<int> argAddresses = stackalloc int[1];
        try
        {
            argAddresses[0] = CopyArgument(param0);
            return _runtimeInstance.InvokeDotNetMethod<TRes>(_monoMethodPtr, instance, argAddresses);
        }
        finally
        {
            FreeArguments(argAddresses);
        }
    }

    public TRes Invoke<T0, T1, TRes>(IsolatedObject? instance, T0 param0, T1 param1)
    {
        if (TryPackScalarArg(param0, out var bits0, out var kind0) && TryPackScalarArg(param1, out var bits1, out var kind1) && TryGetScalarKind<TRes>(out var resultKind))
        {
            return UnpackScalarResult<TRes>(_runtimeInstance.InvokeScalarMethod2(_monoMethodPtr, instance, bits0, bits1, (kind0 << 0) | (kind1 << 8), resultKind));
        }

        Span<int> argAddresses = stackalloc int[2];
        try
        {
            argAddresses[0] = CopyArgument(param0);
            argAddresses[1] = CopyArgument(param1);
            return _runtimeInstance.InvokeDotNetMethod<TRes>(_monoMethodPtr, instance, argAddresses);
        }
        finally
        {
            FreeArguments(argAddresses);
        }
    }

    public TRes Invoke<T0, T1, T2, TRes>(IsolatedObject? instance, T0 param0, T1 param1, T2 param2)
    {
        if (TryPackScalarArg(param0, out var bits0, out var kind0) && TryPackScalarArg(param1, out var bits1, out var kind1) && TryPackScalarArg(param2, out var bits2, out var kind2) && TryGetScalarKind<TRes>(out var resultKind))
        {
            return UnpackScalarResult<TRes>(_runtimeInstance.InvokeScalarMethod3(_monoMethodPtr, instance, bits0, bits1, bits2, (kind0 << 0) | (kind1 << 8) | (kind2 << 16), resultKind));
        }

        Span<int> argAddresses = stackalloc int[3];
        try
        {
            argAddresses[0] = CopyArgument(param0);
            argAddresses[1] = CopyArgument(param1);
            argAddresses[2] = CopyArgument(param2);
            return _runtimeInstance.InvokeDotNetMethod<TRes>(_monoMethodPtr, instance, argAddresses);
        }
        finally
        {
            FreeArguments(argAddresses);
        }
    }

    public TRes Invoke<T0, T1, T2, T3, TRes>(IsolatedObject? instance, T0 param0, T1 param1, T2 param2, T3 param3)
    {
        if (TryPackScalarArg(param0, out var bits0, out var kind0) && TryPackScalarArg(param1, out var bits1, out var kind1) && TryPackScalarArg(param2, out var bits2, out var kind2) && TryPackScalarArg(param3, out var bits3, out var kind3) && TryGetScalarKind<TRes>(out var resultKind))
        {
            return UnpackScalarResult<TRes>(_runtimeInstance.InvokeScalarMethod4(_monoMethodPtr, instance, bits0, bits1, bits2, bits3, (kind0 << 0) | (kind1 << 8) | (kind2 << 16) | (kind3 << 24), resultKind));
        }

        Span<int> argAddresses = stackalloc int[4];
        try
        {
            argAddresses[0] = CopyArgument(param0);
            argAddresses[1] = CopyArgument(param1);
            argAddresses[2] = CopyArgument(param2);
            argAddresses[3] = CopyArgument(param3);
            return _runtimeInstance.InvokeDotNetMethod<TRes>(_monoMethodPtr, instance, argAddresses);
        }
        finally
        {
            FreeArguments(argAddresses);
        }
    }

    public TRes Invoke<T0, T1, T2, T3, T4, TRes>(IsolatedObject? instance, T0 param0, T1 param1, T2 param2, T3 param3, T4 param4)
    {
        Span<int> argAddresses = stackalloc int[5];
        try
        {
            argAddresses[0] = CopyArgument(param0);
            argAddresses[1] = CopyArgument(param1);
            argAddresses[2] = CopyArgument(param2);
            argAddresses[3] = CopyArgument(param3);
            argAddresses[4] = CopyArgument(param4);
            return _runtimeInstance.InvokeDotNetMethod<TRes>(_monoMethodPtr, instance, argAddresses);
        }
        finally
        {
            FreeArguments(argAddresses);
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

        if (TryPackScalarArg(param0, out var argBits, out var argKind))
        {
            _runtimeInstance.InvokeScalarVoidMethod(_monoMethodPtr, instance, argBits, argKind);
            return;
        }

        if (TryInvokeBlittableArrayArg<T0, object>(instance, param0, out _, isVoid: true)) return;

        Invoke<T0, object>(instance, param0);
    }

    public void InvokeVoid<T0, T1>(IsolatedObject? instance, T0 param0, T1 param1)
    {
        if (TryPackScalarArg(param0, out var bits0, out var kind0) && TryPackScalarArg(param1, out var bits1, out var kind1))
        {
            _runtimeInstance.InvokeScalarMethod2(_monoMethodPtr, instance, bits0, bits1, (kind0 << 0) | (kind1 << 8), 0);
            return;
        }
        Invoke<T0, T1, object>(instance, param0, param1);
    }

    public void InvokeVoid<T0, T1, T2>(IsolatedObject? instance, T0 param0, T1 param1, T2 param2)
    {
        if (TryPackScalarArg(param0, out var bits0, out var kind0) && TryPackScalarArg(param1, out var bits1, out var kind1) && TryPackScalarArg(param2, out var bits2, out var kind2))
        {
            _runtimeInstance.InvokeScalarMethod3(_monoMethodPtr, instance, bits0, bits1, bits2, (kind0 << 0) | (kind1 << 8) | (kind2 << 16), 0);
            return;
        }
        Invoke<T0, T1, T2, object>(instance, param0, param1, param2);
    }

    public void InvokeVoid<T0, T1, T2, T3>(IsolatedObject? instance, T0 param0, T1 param1, T2 param2, T3 param3)
    {
        if (TryPackScalarArg(param0, out var bits0, out var kind0) && TryPackScalarArg(param1, out var bits1, out var kind1) && TryPackScalarArg(param2, out var bits2, out var kind2) && TryPackScalarArg(param3, out var bits3, out var kind3))
        {
            _runtimeInstance.InvokeScalarMethod4(_monoMethodPtr, instance, bits0, bits1, bits2, bits3, (kind0 << 0) | (kind1 << 8) | (kind2 << 16) | (kind3 << 24), 0);
            return;
        }
        Invoke<T0, T1, T2, T3, object>(instance, param0, param1, param2, param3);
    }

    public void InvokeVoid<T0, T1, T2, T3, T4>(IsolatedObject? instance, T0 param0, T1 param1, T2 param2, T3 param3, T4 param4)
        => Invoke<T0, T1, T2, T3, T4, object>(instance, param0, param1, param2, param3, param4);

    /// <summary>
    /// Invokes this method once per supplied argument in a single host/guest boundary crossing and
    /// returns the results in order. The argument and result types must be blittable primitives.
    /// This amortizes the per-call boundary and host-side marshaling cost across the whole batch,
    /// so it is most useful for tight loops of independent primitive calls.
    /// </summary>
    public TRes[] InvokeBatch<T0, TRes>(IsolatedObject? instance, ReadOnlySpan<T0> args)
        where T0 : unmanaged
        where TRes : unmanaged
    {
        var argKind = PrimitiveScalarCodec.GetKind(typeof(T0));
        var resultKind = PrimitiveScalarCodec.GetKind(typeof(TRes));
        if (argKind == PrimitiveScalarCodec.None || resultKind == PrimitiveScalarCodec.None)
        {
            throw new ArgumentException("InvokeBatch requires blittable primitive argument and result types.");
        }

        return _runtimeInstance.InvokeScalarBatch<T0, TRes>(_monoMethodPtr, instance, args, argKind, resultKind);
    }

    internal IsolatedRuntime Runtime => _runtimeInstance;

    internal int MethodPointer => _monoMethodPtr;
}
