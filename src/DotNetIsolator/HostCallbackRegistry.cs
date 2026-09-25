using DotNetIsolator.Internal;
using MessagePack;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Wasmtime;
using System.Text;

namespace DotNetIsolator;

internal sealed class HostCallbackRegistry
{
    private static readonly byte[] HiddenFailureMessage =
        Encoding.UTF8.GetBytes("The call failed. See host console logs for details.");

    private readonly Dictionary<string, RegisteredCallback> _callbacks = new();

    private readonly Dictionary<string, int> _callbackIds = new(StringComparer.Ordinal);
    private readonly List<RegisteredCallback> _callbacksById = new();

    public int Resolve(string name) => _callbackIds.GetValueOrDefault(name);

    public void Add(string name, Delegate callback)
    {
        if (callback is null)
        {
            throw new ArgumentNullException(nameof(callback));
        }

        var registered = RegisteredCallback.Create(callback);
        _callbacks.Add(name, registered);
        _callbacksById.Add(registered);
        _callbackIds.Add(name, _callbacksById.Count);
    }

    public HostCallbackResponse Invoke(ReadOnlyMemory<byte> invocationBytes)
    {
        try
        {
            // Deserialize directly from guest memory; the envelope bytes are not needed afterwards.
            var invocationInfo = MessagePackSerializer.Deserialize<GuestToHostCall>(
                invocationBytes,
                MessagePackCompatibility.GuestToHostCallOptions);

            if (!_callbacks.TryGetValue(invocationInfo.CallbackName, out var callback))
            {
                return HostCallbackResponse.Failure(
                    Encoding.UTF8.GetBytes($"There is no registered callback with name '{invocationInfo.CallbackName}'"));
            }

            var deserializedArgs = DeserializeArguments(callback.ParameterTypes, invocationInfo);
            var result = callback.Delegate.DynamicInvoke(deserializedArgs);
            return invocationInfo.IsRawCall || result is null
                ? HostCallbackResponse.Success((byte[]?)result)
                : HostCallbackResponse.Serialized(ObjectGraphSerializer.SerializeBuffer(callback.ReturnType, result));
        }
        catch (Exception ex)
        {
            // We could supply the raw exception info to the guest, but since we consider the guest untrusted,
            // we don't want to expose arbitrary information about the host internals.
            Console.Error.WriteLine(ex.ToString());
            return HostCallbackResponse.Failure(HiddenFailureMessage);
        }
    }

    public HostCallbackResponse InvokeRaw(int callbackId, Memory memory, int argsPtr, int count)
    {
        try
        {
            if ((uint)(callbackId - 1) >= (uint)_callbacksById.Count)
                throw new InvalidOperationException("Unknown callback ID.");
            var callback = _callbacksById[callbackId - 1];
            if (count != callback.ParameterTypes.Length)
                throw new InvalidOperationException("Raw callback argument count does not match its registration.");
            var args = MemoryMarshal.Cast<byte, RawCallbackArgument>(memory.GetSpan(argsPtr, checked(count * Unsafe.SizeOf<RawCallbackArgument>())));

            if (callback.Delegate is Func<byte[]?, byte[]?> single && count == 1)
                return HostCallbackResponse.Success(single(CopyRawArgument(memory, args[0])));
            if (callback.Delegate is Func<byte[]?> zero && count == 0)
                return HostCallbackResponse.Success(zero());

            var values = count == 0 ? Array.Empty<object?>() : new object?[count];
            for (var i = 0; i < count; i++) values[i] = CopyRawArgument(memory, args[i]);
            return HostCallbackResponse.Success((byte[]?)callback.Delegate.DynamicInvoke(values));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.ToString());
            return HostCallbackResponse.Failure(HiddenFailureMessage);
        }
    }

    private static byte[]? CopyRawArgument(Memory memory, RawCallbackArgument arg)
    {
        if (arg.Length == -1) return null;
        if (arg.Length < 0) throw new InvalidOperationException("Invalid raw callback argument length.");
        return memory.GetSpan(arg.Data, arg.Length).ToArray();
    }

    // Layout matches RawCallbackArgument in native/host_callback.c. The handle is guest-owned.
    [StructLayout(LayoutKind.Sequential)]
    private struct RawCallbackArgument
    {
        public int Data;
        public int Length;
        public int GuestHandle;
    }

    public long InvokeScalars(int callbackId, int kinds, long a0, long a1, long a2, long a3)
    {
        if ((uint)(callbackId - 1) >= (uint)_callbacksById.Count)
            throw new InvalidOperationException("Unknown callback ID.");
        return _callbacksById[callbackId - 1].InvokeScalars(kinds, a0, a1, a2, a3);
    }

    public long InvokeScalar(int callbackId, long argBits, int argKind, int resultKind)
    {
        if ((uint)(callbackId - 1) >= (uint)_callbacksById.Count)
        {
            throw new InvalidOperationException($"There is no registered callback with ID {callbackId}");
        }

        var result = _callbacksById[callbackId - 1].InvokeScalar(argBits, argKind, resultKind)
            ?? throw new InvalidOperationException("The callback does not have a scalar-compatible signature.");

        return result;
    }

    private static object?[] DeserializeArguments(Type[] parameterTypes, GuestToHostCall invocationInfo)
    {
        if (invocationInfo.ArgsLength != parameterTypes.Length)
        {
            throw new InvalidOperationException(
                $"Callback expected {parameterTypes.Length} argument(s), but the guest supplied {invocationInfo.ArgsLength}.");
        }

        if (parameterTypes.Length == 0)
        {
            return Array.Empty<object?>();
        }

        var deserializedArgs = new object?[parameterTypes.Length];
        for (var i = 0; i < parameterTypes.Length; i++)
        {
            var bytes = invocationInfo.Args[i];
            deserializedArgs[i] = invocationInfo.IsRawCall || bytes is null
                ? bytes
                : MessagePackCompatibility.DeserializeObject(parameterTypes[i], bytes);
        }

        return deserializedArgs;
    }

}

internal readonly struct HostCallbackResponse : IDisposable
{
    private HostCallbackResponse(bool success, byte[]? resultBytes, ObjectGraphSerializer.BufferLease? lease = null)
    {
        IsSuccess = success;
        ResultBytes = resultBytes;
        _lease = lease;
    }

    public bool IsSuccess { get; }

    private readonly ObjectGraphSerializer.BufferLease? _lease;
    public byte[]? ResultBytes { get; }
    public bool HasResult => _lease is not null || ResultBytes is not null;
    public ReadOnlySpan<byte> Span => _lease is not null ? _lease.Span : ResultBytes;
    public void Dispose() => _lease?.Dispose();

    public static HostCallbackResponse Serialized(ObjectGraphSerializer.BufferLease lease)
        => new(true, null, lease);

    public static HostCallbackResponse Success(byte[]? resultBytes)
        => new(true, resultBytes);

    public static HostCallbackResponse Failure(byte[] resultBytes)
        => new(false, resultBytes);
}

internal sealed class RegisteredCallback
{
    private RegisteredCallback(Delegate callback, Type[] parameterTypes, Type returnType, ScalarCallbackInvoker? scalarInvoker)
    {
        Delegate = callback;
        ParameterTypes = parameterTypes;
        ReturnType = returnType;
        ScalarInvoker = scalarInvoker;
        if (parameterTypes.Length <= 4 && parameterTypes.All(t => PrimitiveScalarCodec.GetKind(t) != 0))
        {
            _primitiveArguments = true;
            _argumentKinds = parameterTypes.Length << 20;
            for (var i = 0; i < parameterTypes.Length; i++) _argumentKinds |= PrimitiveScalarCodec.GetKind(parameterTypes[i]) << (4 * i);
            _resultKind = PrimitiveScalarCodec.GetKind(returnType);
            if (parameterTypes.Length >= 2 && _resultKind != 0)
                _manyInvoker = CreateManyInvoker(callback, parameterTypes, returnType);
        }
    }

    public Delegate Delegate { get; }

    public Type[] ParameterTypes { get; }

    public Type ReturnType { get; }

    private ScalarCallbackInvoker? ScalarInvoker { get; }

    private readonly bool _primitiveArguments;
    private readonly int _argumentKinds;
    private readonly int _resultKind;
    private readonly ManyScalarInvoker? _manyInvoker;
    private ManyScalarInvoker? _discardInvoker;

    public long InvokeScalars(int kinds, long a0, long a1, long a2, long a3)
    {
        var resultKind = (kinds >> 16) & 15;
        if (!_primitiveArguments || (kinds & ~0xF0000) != _argumentKinds || (resultKind != 0 && resultKind != _resultKind))
            throw new InvalidOperationException("The scalar callback signature does not match the guest request.");
        var invoke = resultKind == 0
            ? _discardInvoker ??= CreateManyInvoker(Delegate, ParameterTypes, typeof(void))
            : _manyInvoker;
        if (invoke is null) throw new InvalidOperationException("The callback does not have a scalar-compatible signature.");
        return invoke(a0, a1, a2, a3);
    }

    private delegate long ManyScalarInvoker(long a0, long a1, long a2, long a3);

    private static ManyScalarInvoker CreateManyInvoker(Delegate callback, Type[] args, Type result)
    {
        var callbackResult = callback.GetType().GetMethod("Invoke")!.ReturnType;
        if (result == typeof(void) && callbackResult != typeof(void))
        {
            var discardFactory = typeof(RegisteredCallback).GetMethod($"CreateDiscardResult{args.Length}", BindingFlags.NonPublic | BindingFlags.Static)!
                .MakeGenericMethod(args.Append(callbackResult).ToArray());
            return discardFactory.CreateDelegate<Func<Delegate, ManyScalarInvoker>>()(callback);
        }
        var factoryName = result == typeof(void) ? $"CreateDiscard{args.Length}" : $"CreateMany{args.Length}";
        var factory = typeof(RegisteredCallback).GetMethod(factoryName, BindingFlags.NonPublic | BindingFlags.Static)!;
        var types = result == typeof(void) ? args : args.Append(result).ToArray();
        if (types.Length != 0) factory = factory.MakeGenericMethod(types);
        return factory.CreateDelegate<Func<Delegate, ManyScalarInvoker>>()(callback);
    }

    // Adapt the whole invocation list, preserving custom and multicast delegate semantics.
    private static TDelegate Adapt<TDelegate>(Delegate callback) where TDelegate : Delegate
    {
        if (callback is TDelegate typed) return typed;
        Delegate? combined = null;
        foreach (var part in callback.GetInvocationList())
            combined = Delegate.Combine(combined, part.Method.CreateDelegate<TDelegate>(part.Target));
        return (TDelegate)combined!;
    }

    public long? InvokeScalar(long argBits, int argKind, int resultKind)
        => ScalarInvoker?.Invoke(argBits, argKind, resultKind);

    public static RegisteredCallback Create(Delegate callback)
    {
        var signature = callback.GetType().GetMethod("Invoke")!;
        var parameterTypes = signature.GetParameters().Select(p => p.ParameterType).ToArray();
        return new RegisteredCallback(
            callback,
            parameterTypes,
            signature.ReturnType,
            CreateScalarInvoker(callback, parameterTypes, signature.ReturnType));
    }

    private static ScalarCallbackInvoker? CreateScalarInvoker(Delegate callback, Type[] parameterTypes, Type returnType)
    {
        if (PrimitiveScalarCodec.GetKind(returnType) == PrimitiveScalarCodec.None)
        {
            return null;
        }

        if (parameterTypes.Length == 0)
        {
            return CreateScalarInvokerFactory(returnType).Invoke(callback);
        }

        if (parameterTypes.Length == 1
            && PrimitiveScalarCodec.GetKind(parameterTypes[0]) != PrimitiveScalarCodec.None)
        {
            return CreateScalarInvokerFactory(parameterTypes[0], returnType).Invoke(callback);
        }

        return null;
    }

    private static Func<Delegate, ScalarCallbackInvoker> CreateScalarInvokerFactory(Type resultType)
        => typeof(RegisteredCallback)
            .GetMethod(nameof(CreateZeroArgScalarInvoker), BindingFlags.Static | BindingFlags.NonPublic)!
            .MakeGenericMethod(resultType)
            .CreateDelegate<Func<Delegate, ScalarCallbackInvoker>>();

    private static Func<Delegate, ScalarCallbackInvoker> CreateScalarInvokerFactory(Type argType, Type resultType)
        => typeof(RegisteredCallback)
            .GetMethod(nameof(CreateOneArgScalarInvoker), BindingFlags.Static | BindingFlags.NonPublic)!
            .MakeGenericMethod(argType, resultType)
            .CreateDelegate<Func<Delegate, ScalarCallbackInvoker>>();

    private static ScalarCallbackInvoker CreateZeroArgScalarInvoker<TRes>(Delegate callback)
    {
        var typedCallback = Adapt<Func<TRes>>(callback);
        var expectedResultKind = PrimitiveScalarCodec.GetKind(typeof(TRes));

        return (_, argKind, resultKind) =>
        {
            if (argKind != PrimitiveScalarCodec.None || resultKind != expectedResultKind)
            {
                throw new InvalidOperationException("The scalar callback signature does not match the guest request.");
            }

            return ScalarCodec<TRes>.Pack(typedCallback());
        };
    }

    private static ScalarCallbackInvoker CreateOneArgScalarInvoker<TArg, TRes>(Delegate callback)
    {
        var typedCallback = Adapt<Func<TArg, TRes>>(callback);
        var expectedArgKind = PrimitiveScalarCodec.GetKind(typeof(TArg));
        var expectedResultKind = PrimitiveScalarCodec.GetKind(typeof(TRes));

        return (argBits, argKind, resultKind) =>
        {
            if (argKind != expectedArgKind || resultKind != expectedResultKind)
            {
                throw new InvalidOperationException("The scalar callback signature does not match the guest request.");
            }

            var arg = ScalarCodec<TArg>.Unpack(argBits);
            return ScalarCodec<TRes>.Pack(typedCallback(arg));
        };
    }

    private static ManyScalarInvoker CreateDiscard0(Delegate callback)
    {
        var call = Adapt<Action>(callback);
        return (a0, a1, a2, a3) => { call(); return 0; };
    }

    private static ManyScalarInvoker CreateDiscardResult0<TRes>(Delegate callback)
    {
        var call = Adapt<Func<TRes>>(callback);
        return (a0, a1, a2, a3) => { _ = call(); return 0; };
    }

    private static ManyScalarInvoker CreateDiscard1<T0>(Delegate callback)
    {
        var call = Adapt<Action<T0>>(callback);
        return (a0, a1, a2, a3) => { call(ScalarCodec<T0>.Unpack(a0)); return 0; };
    }

    private static ManyScalarInvoker CreateDiscardResult1<T0, TRes>(Delegate callback)
    {
        var call = Adapt<Func<T0, TRes>>(callback);
        return (a0, a1, a2, a3) => { _ = call(ScalarCodec<T0>.Unpack(a0)); return 0; };
    }

    private static ManyScalarInvoker CreateDiscard2<T0, T1>(Delegate callback)
    {
        var call = Adapt<Action<T0, T1>>(callback);
        return (a0, a1, a2, a3) => { call(ScalarCodec<T0>.Unpack(a0), ScalarCodec<T1>.Unpack(a1)); return 0; };
    }

    private static ManyScalarInvoker CreateDiscardResult2<T0, T1, TRes>(Delegate callback)
    {
        var call = Adapt<Func<T0, T1, TRes>>(callback);
        return (a0, a1, a2, a3) => { _ = call(ScalarCodec<T0>.Unpack(a0), ScalarCodec<T1>.Unpack(a1)); return 0; };
    }

    private static ManyScalarInvoker CreateMany2<T0, T1, TRes>(Delegate callback)
    {
        var call = Adapt<Func<T0, T1, TRes>>(callback);
        return (a0, a1, a2, a3) => ScalarCodec<TRes>.Pack(call(ScalarCodec<T0>.Unpack(a0), ScalarCodec<T1>.Unpack(a1)));
    }

    private static ManyScalarInvoker CreateDiscard3<T0, T1, T2>(Delegate callback)
    {
        var call = Adapt<Action<T0, T1, T2>>(callback);
        return (a0, a1, a2, a3) => { call(ScalarCodec<T0>.Unpack(a0), ScalarCodec<T1>.Unpack(a1), ScalarCodec<T2>.Unpack(a2)); return 0; };
    }

    private static ManyScalarInvoker CreateDiscardResult3<T0, T1, T2, TRes>(Delegate callback)
    {
        var call = Adapt<Func<T0, T1, T2, TRes>>(callback);
        return (a0, a1, a2, a3) => { _ = call(ScalarCodec<T0>.Unpack(a0), ScalarCodec<T1>.Unpack(a1), ScalarCodec<T2>.Unpack(a2)); return 0; };
    }

    private static ManyScalarInvoker CreateMany3<T0, T1, T2, TRes>(Delegate callback)
    {
        var call = Adapt<Func<T0, T1, T2, TRes>>(callback);
        return (a0, a1, a2, a3) => ScalarCodec<TRes>.Pack(call(ScalarCodec<T0>.Unpack(a0), ScalarCodec<T1>.Unpack(a1), ScalarCodec<T2>.Unpack(a2)));
    }

    private static ManyScalarInvoker CreateDiscard4<T0, T1, T2, T3>(Delegate callback)
    {
        var call = Adapt<Action<T0, T1, T2, T3>>(callback);
        return (a0, a1, a2, a3) => { call(ScalarCodec<T0>.Unpack(a0), ScalarCodec<T1>.Unpack(a1), ScalarCodec<T2>.Unpack(a2), ScalarCodec<T3>.Unpack(a3)); return 0; };
    }

    private static ManyScalarInvoker CreateDiscardResult4<T0, T1, T2, T3, TRes>(Delegate callback)
    {
        var call = Adapt<Func<T0, T1, T2, T3, TRes>>(callback);
        return (a0, a1, a2, a3) => { _ = call(ScalarCodec<T0>.Unpack(a0), ScalarCodec<T1>.Unpack(a1), ScalarCodec<T2>.Unpack(a2), ScalarCodec<T3>.Unpack(a3)); return 0; };
    }

    private static ManyScalarInvoker CreateMany4<T0, T1, T2, T3, TRes>(Delegate callback)
    {
        var call = Adapt<Func<T0, T1, T2, T3, TRes>>(callback);
        return (a0, a1, a2, a3) => ScalarCodec<TRes>.Pack(call(ScalarCodec<T0>.Unpack(a0), ScalarCodec<T1>.Unpack(a1), ScalarCodec<T2>.Unpack(a2), ScalarCodec<T3>.Unpack(a3)));
    }

    private delegate long ScalarCallbackInvoker(long argBits, int argKind, int resultKind);
}
