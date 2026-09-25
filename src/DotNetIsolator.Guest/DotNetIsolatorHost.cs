using DotNetIsolator.Guest;
using MessagePack;
using System.Buffers;
using System.Text;
using DotNetIsolator.Internal;

namespace DotNetIsolator;

public static class DotNetIsolatorHost
{
    // Guest statics belong to this runtime and are reset when a pooled instance is restored.
    private static readonly Dictionary<string, int> CallbackIds = new(StringComparer.Ordinal);

    /// <summary>Invokes a callback without a params array for primitive arguments and results.</summary>
    public static TRes Invoke<TArg, TRes>(string callbackName, TArg arg)
    {
        if (ScalarCodec<TArg>.Kind != 0 && ScalarCodec<TRes>.Kind != 0)
            return ScalarCodec<TRes>.Unpack(CallScalar(callbackName, ScalarCodec<TArg>.Pack(arg), ScalarCodec<TArg>.Kind, ScalarCodec<TRes>.Kind));
        return Invoke<TRes>(callbackName, new object[] { arg! });
    }

    public static byte[] InvokeRaw(string callbackName, params byte[]?[] args)
        => InvokeRaw<object>(callbackName, args);

    public static void Invoke(string callbackName, params object[] args)
    {
        using var serializedArgs = SerializeArgs(args);
        _ = PerformCall<object>(
            CreateCall(callbackName, serializedArgs.Args, serializedArgs.Length, isRawCall: false),
            readResult: false);
    }

    public static unsafe byte[] InvokeRaw<T>(string callbackName, params byte[]?[] args)
    {
        return PerformCall<byte[]>(new GuestToHostCall
        {
            CallbackName = callbackName,
            Args = args,
            ArgsLength = args.Length,
            IsRawCall = true,
        });
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

    private static unsafe long CallScalar(string name, long bits, int argKind, int resultKind)
    {
        var invocation = new ScalarCallInvocation { ArgBits = bits, ArgKind = argKind, ResultKind = resultKind };
        Interop.CallHostScalar(ResolveCallback(name), &invocation);
        if (invocation.Error != 0)
            throw new InvalidOperationException("Call to host failed: The call failed. See host console logs for details.");
        return invocation.ResultBits;
    }

    private static unsafe int ResolveCallback(string name)
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
            throw new InvalidOperationException("Call to host failed: The call failed. See host console logs for details.");
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
        var callInfoBytes = MessagePackSerializer.Serialize(callInfo, MessagePackCompatibility.GuestToHostCallOptions);

        fixed (void* callInfoPtr = callInfoBytes)
        {
            var success = Interop.CallHost(callInfoPtr, callInfoBytes.Length, out var resultPtr, out var resultLength);
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
