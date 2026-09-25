using DotNetIsolator.Guest;
using MessagePack;
using System.Buffers;
using System.Text;
using System.Runtime.InteropServices;
using DotNetIsolator.Internal;

namespace DotNetIsolator;

public static class DotNetIsolatorHost
{
    // Guest statics belong to this runtime and are reset when a pooled instance is restored.
    [ThreadStatic] private static ArrayBufferWriter<byte>? _availableEnvelopeWriter;

    private static readonly Dictionary<string, int> CallbackIds = new(StringComparer.Ordinal);

    /// <summary>Invokes a callback without a params array for primitive arguments and results.</summary>
    public static TRes Invoke<TArg, TRes>(string callbackName, TArg arg)
    {
        if (ScalarCodec<TArg>.Kind != 0 && ScalarCodec<TRes>.Kind != 0)
            return ScalarCodec<TRes>.Unpack(CallScalar(callbackName, ScalarCodec<TArg>.Pack(arg), ScalarCodec<TArg>.Kind, ScalarCodec<TRes>.Kind));
        return Invoke<TRes>(callbackName, new object[] { arg! });
    }

    /// <summary>Invokes a callback without a params array for primitive arguments and results.</summary>
    public static TRes Invoke<T0, T1, TRes>(string callbackName, T0 arg0, T1 arg1)
    {
        if (ScalarCodec<T0>.Kind != 0 && ScalarCodec<T1>.Kind != 0 && ScalarCodec<TRes>.Kind != 0)
            return ScalarCodec<TRes>.Unpack(CallScalars(callbackName, (ScalarCodec<T0>.Kind << 0) | (ScalarCodec<T1>.Kind << 4) | (ScalarCodec<TRes>.Kind << 16) | (2 << 20), ScalarCodec<T0>.Pack(arg0), ScalarCodec<T1>.Pack(arg1)));
        return Invoke<TRes>(callbackName, new object[] { arg0!, arg1! });
    }

    /// <summary>Invokes a callback without a params array for primitive arguments and results.</summary>
    public static TRes Invoke<T0, T1, T2, TRes>(string callbackName, T0 arg0, T1 arg1, T2 arg2)
    {
        if (ScalarCodec<T0>.Kind != 0 && ScalarCodec<T1>.Kind != 0 && ScalarCodec<T2>.Kind != 0 && ScalarCodec<TRes>.Kind != 0)
            return ScalarCodec<TRes>.Unpack(CallScalars(callbackName, (ScalarCodec<T0>.Kind << 0) | (ScalarCodec<T1>.Kind << 4) | (ScalarCodec<T2>.Kind << 8) | (ScalarCodec<TRes>.Kind << 16) | (3 << 20), ScalarCodec<T0>.Pack(arg0), ScalarCodec<T1>.Pack(arg1), ScalarCodec<T2>.Pack(arg2)));
        return Invoke<TRes>(callbackName, new object[] { arg0!, arg1!, arg2! });
    }

    /// <summary>Invokes a callback without a params array for primitive arguments and results.</summary>
    public static TRes Invoke<T0, T1, T2, T3, TRes>(string callbackName, T0 arg0, T1 arg1, T2 arg2, T3 arg3)
    {
        if (ScalarCodec<T0>.Kind != 0 && ScalarCodec<T1>.Kind != 0 && ScalarCodec<T2>.Kind != 0 && ScalarCodec<T3>.Kind != 0 && ScalarCodec<TRes>.Kind != 0)
            return ScalarCodec<TRes>.Unpack(CallScalars(callbackName, (ScalarCodec<T0>.Kind << 0) | (ScalarCodec<T1>.Kind << 4) | (ScalarCodec<T2>.Kind << 8) | (ScalarCodec<T3>.Kind << 12) | (ScalarCodec<TRes>.Kind << 16) | (4 << 20), ScalarCodec<T0>.Pack(arg0), ScalarCodec<T1>.Pack(arg1), ScalarCodec<T2>.Pack(arg2), ScalarCodec<T3>.Pack(arg3)));
        return Invoke<TRes>(callbackName, new object[] { arg0!, arg1!, arg2!, arg3! });
    }

    public static byte[] InvokeRaw(string callbackName, params byte[]?[] args)
        => InvokeRaw<object>(callbackName, args);

    public static void Invoke(string callbackName, params object[] args)
    {
        if (TryInvokeScalars(callbackName, args, 0, out _)) return;
        using var serializedArgs = SerializeArgs(args);
        _ = PerformCall<object>(
            CreateCall(callbackName, serializedArgs.Args, serializedArgs.Length, isRawCall: false),
            readResult: false);
    }

    public static unsafe byte[] InvokeRaw<T>(string callbackName, params byte[]?[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var success = Interop.CallHostRaw(ResolveCallback(callbackName, includeName: true), args, out var resultPtr, out var resultLength);
        try
        {
            if (!success)
                throw new InvalidOperationException("Call to host failed: The call failed. See host console logs for details.");
            return resultPtr is null ? null! : new ReadOnlySpan<byte>(resultPtr, resultLength).ToArray();
        }
        finally
        {
            if (resultPtr is not null) Interop.FreeHostCallResult(resultPtr);
        }
    }

    public static unsafe T Invoke<T>(string callbackName, params object[] args)
    {
        // Lean fast path: a callback whose single argument (if any) and result are blittable
        // primitives travels bit-packed in registers, skipping the MessagePack envelope and the
        // object-graph (de)serialization on both sides.
        if (TryInvokeScalar<T>(callbackName, args, out var scalarResult))
        {
            return scalarResult;
        }

        // Note that this overload won't work if the host is AOT compiled because it will be unable to
        // deserialize these arbitrary arg types. For that scenario, use the Memory<byte>[] overload instead.
        using var serializedArgs = SerializeArgs(args);
        return PerformCall<T>(
            CreateCall(callbackName, serializedArgs.Args, serializedArgs.Length, isRawCall: false),
            readResult: true);
    }

    private static unsafe bool TryInvokeScalar<T>(string callbackName, object[] args, out T result)
    {
        var resultKind = PrimitiveScalarCodec.GetKind(typeof(T));
        if (args.Length is >= 2 and <= 4 && resultKind != 0 && TryInvokeScalars(callbackName, args, resultKind, out var manyBits))
        {
            result = ScalarCodec<T>.Unpack(manyBits);
            return true;
        }
        if (resultKind == PrimitiveScalarCodec.None || args.Length > 1)
        {
            result = default!;
            return false;
        }

        long argBits = 0;
        var argKind = PrimitiveScalarCodec.None;
        if (args.Length == 1)
        {
            if (args[0] is null)
            {
                result = default!;
                return false;
            }

            argKind = PrimitiveScalarCodec.GetKind(args[0].GetType());
            if (argKind == PrimitiveScalarCodec.None)
            {
                result = default!;
                return false;
            }

            argBits = PrimitiveScalarCodec.Pack(args[0], argKind);
        }

        result = ScalarCodec<T>.Unpack(CallScalar(callbackName, argBits, argKind, resultKind));
        return true;
    }

    private static unsafe bool TryInvokeScalars(string name, object[] args, int resultKind, out long result)
    {
        result = 0;
        if (args.Length > 4) return false;
        var kinds = (resultKind << 16) | (args.Length << 20);
        long* bits = stackalloc long[4];
        for (var i = 0; i < 4; i++) bits[i] = 0;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] is null) return false;
            var kind = PrimitiveScalarCodec.GetKind(args[i].GetType());
            if (kind == 0) return false;
            kinds |= kind << (i * 4);
            bits[i] = PrimitiveScalarCodec.Pack(args[i], kind);
        }
        result = CallScalars(name, kinds, bits[0], bits[1], bits[2], bits[3]);
        return true;
    }

    // Four bits per argument kind, then result kind at bit 16 and arity at bit 20.
    private static unsafe long CallScalars(string name, int kinds, long a0 = 0, long a1 = 0, long a2 = 0, long a3 = 0)
    {
        var invocation = new ScalarArguments { A0 = a0, A1 = a1, A2 = a2, A3 = a3 };
        Interop.CallHostScalars(ResolveCallback(name, includeName: true), kinds, &invocation);
        if (invocation.Error != 0)
            throw new InvalidOperationException("Call to host failed: The call failed. See host console logs for details.");
        return invocation.Result;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ScalarArguments
    {
        public long A0, A1, A2, A3, Result;
        public int Error;
    }

    private static unsafe long CallScalar(string name, long bits, int argKind, int resultKind)
    {
        var result = Interop.CallHostScalar(ResolveCallback(name), argKind | (resultKind << 8), out var error, bits);
        if (error != 0)
            throw new InvalidOperationException("Call to host failed: The call failed. See host console logs for details.");
        return result;
    }

    private static unsafe int ResolveCallback(string name, bool includeName = false)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (CallbackIds.TryGetValue(name, out var id)) return id;
        var byteCount = Encoding.UTF8.GetByteCount(name);
        var rented = byteCount > 256 ? ArrayPool<byte>.Shared.Rent(byteCount) : null;
        try
        {
            Span<byte> bytes = rented is null ? stackalloc byte[byteCount] : rented.AsSpan(0, byteCount);
            Encoding.UTF8.GetBytes(name, bytes);
            fixed (byte* ptr = bytes) id = Interop.ResolveCallback(ptr, byteCount);
        }
        finally
        {
            if (rented is not null) ArrayPool<byte>.Shared.Return(rented);
        }
        if (id == 0)
            throw new InvalidOperationException(includeName
                ? $"Call to host failed: There is no registered callback with name '{name}'"
                : "Call to host failed: The call failed. See host console logs for details.");
        CallbackIds.Add(name, id);
        return id;
    }

    private static SerializedArgs SerializeArgs(object[] args)
    {
        if (args.Length == 0)
        {
            return SerializedArgs.Empty;
        }

        var result = ArrayPool<byte[]?>.Shared.Rent(args.Length);
        try
        {
            for (var i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                result[i] = arg is null ? null : MessagePackCompatibility.SerializeObject(arg.GetType(), arg);
            }

            return new SerializedArgs(result, args.Length, isPooled: true);
        }
        catch
        {
            ArrayPool<byte[]?>.Shared.Return(result, clearArray: true);
            throw;
        }
    }

    private static GuestToHostCall CreateCall(string callbackName, byte[]?[] args, int argsLength, bool isRawCall)
        => new()
        {
            CallbackName = callbackName,
            Args = args,
            ArgsLength = argsLength,
            IsRawCall = isRawCall,
        };

    private static unsafe T PerformCall<T>(GuestToHostCall callInfo, bool readResult = true)
    {
        // Keep the writer unavailable until the synchronous call finishes, including reentry.
        var writer = _availableEnvelopeWriter ?? new ArrayBufferWriter<byte>();
        _availableEnvelopeWriter = null;
        writer.ResetWrittenCount();
        try
        {
            MessagePackSerializer.Serialize(writer, callInfo, MessagePackCompatibility.GuestToHostCallOptions);
            fixed (void* callInfoPtr = writer.WrittenSpan)
            {
                return ReadHostCallResult<T>(callInfo, readResult, callInfoPtr, writer.WrittenCount);
            }
        }
        finally
        {
            _availableEnvelopeWriter ??= writer;
        }
    }

    private static unsafe T ReadHostCallResult<T>(GuestToHostCall callInfo, bool readResult, void* callInfoPtr, int callInfoLength)
    {
        var success = Interop.CallHost(callInfoPtr, callInfoLength, out var resultPtr, out var resultLength);
        try
        {
            var hasResult = (int)resultPtr != 0 && (callInfo.IsRawCall || resultLength > 0);
            var result = hasResult ? new Span<byte>(resultPtr, resultLength) : default;
            if (success)
            {
                if (!readResult || !hasResult)
                {
                    return default!;
                }

                if (callInfo.IsRawCall)
                {
                    return (T)(object)result.ToArray();
                }

                using var resultStream = new UnmanagedMemoryStream((byte*)resultPtr, resultLength);
                return MessagePackCompatibility.DeserializeObject<T>(resultStream)!;
            }
            else
            {
                var errorString = Encoding.UTF8.GetString(result);
                throw new InvalidOperationException($"Call to host failed: {errorString}");
            }
        }
        finally
        {
            if (resultPtr is not null)
            {
                Interop.FreeHostCallResult(resultPtr);
            }
        }
    }

    private readonly struct SerializedArgs : IDisposable
    {
        public static readonly SerializedArgs Empty = new(Array.Empty<byte[]?>(), 0, isPooled: false);

        public SerializedArgs(byte[]?[] args, int length, bool isPooled)
        {
            Args = args;
            Length = length;
            _isPooled = isPooled;
        }

        private readonly bool _isPooled;

        public byte[]?[] Args { get; }

        public int Length { get; }

        public void Dispose()
        {
            if (_isPooled)
            {
                ArrayPool<byte[]?>.Shared.Return(Args, clearArray: true);
            }
        }
    }
}
