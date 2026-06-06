using DotNetIsolator.Internal;
using MessagePack;
using System.Reflection;
using System.Text;

namespace DotNetIsolator;

internal sealed class HostCallbackRegistry
{
    private static readonly byte[] HiddenFailureMessage =
        Encoding.UTF8.GetBytes("The call failed. See host console logs for details.");

    private readonly Dictionary<string, RegisteredCallback> _callbacks = new();

    public void Add(string name, Delegate callback)
    {
        if (callback is null)
        {
            throw new ArgumentNullException(nameof(callback));
        }

        _callbacks.Add(name, RegisteredCallback.Create(callback));
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
            return HostCallbackResponse.Success(SerializeResult(callback.ReturnType, invocationInfo.IsRawCall, result));
        }
        catch (Exception ex)
        {
            // We could supply the raw exception info to the guest, but since we consider the guest untrusted,
            // we don't want to expose arbitrary information about the host internals.
            Console.Error.WriteLine(ex.ToString());
            return HostCallbackResponse.Failure(HiddenFailureMessage);
        }
    }

    public long InvokeScalar(string callbackName, long argBits, int argKind, int resultKind)
    {
        if (!_callbacks.TryGetValue(callbackName, out var callback))
        {
            throw new InvalidOperationException($"There is no registered callback with name '{callbackName}'");
        }

        var result = callback.InvokeScalar(argBits, argKind, resultKind)
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
            deserializedArgs[i] = invocationInfo.IsRawCall
                ? invocationInfo.Args![i]
                : MessagePackCompatibility.DeserializeObject(parameterTypes[i], invocationInfo.Args[i]);
        }

        return deserializedArgs;
    }

    private static byte[]? SerializeResult(Type returnType, bool isRawCall, object? result)
    {
        if (result is null)
        {
            return null;
        }

        return isRawCall
            ? (byte[])result
            : MessagePackCompatibility.SerializeObject(returnType, result);
    }
}

internal readonly struct HostCallbackResponse
{
    private HostCallbackResponse(bool success, byte[]? resultBytes)
    {
        IsSuccess = success;
        ResultBytes = resultBytes;
    }

    public bool IsSuccess { get; }

    public byte[]? ResultBytes { get; }

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
    }

    public Delegate Delegate { get; }

    public Type[] ParameterTypes { get; }

    public Type ReturnType { get; }

    private ScalarCallbackInvoker? ScalarInvoker { get; }

    public long? InvokeScalar(long argBits, int argKind, int resultKind)
        => ScalarInvoker?.Invoke(argBits, argKind, resultKind);

    public static RegisteredCallback Create(Delegate callback)
    {
        var parameterTypes = callback.Method.GetParameters().Select(p => p.ParameterType).ToArray();
        return new RegisteredCallback(
            callback,
            parameterTypes,
            callback.Method.ReturnType,
            CreateScalarInvoker(callback, parameterTypes, callback.Method.ReturnType));
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
        var typedCallback = callback as Func<TRes>
            ?? callback.Method.CreateDelegate<Func<TRes>>(callback.Target);
        var expectedResultKind = PrimitiveScalarCodec.GetKind(typeof(TRes));

        return (_, argKind, resultKind) =>
        {
            if (argKind != PrimitiveScalarCodec.None || resultKind != expectedResultKind)
            {
                throw new InvalidOperationException("The scalar callback signature does not match the guest request.");
            }

            return PrimitiveScalarCodec.Pack(typedCallback()!, resultKind);
        };
    }

    private static ScalarCallbackInvoker CreateOneArgScalarInvoker<TArg, TRes>(Delegate callback)
    {
        var typedCallback = callback as Func<TArg, TRes>
            ?? callback.Method.CreateDelegate<Func<TArg, TRes>>(callback.Target);
        var expectedArgKind = PrimitiveScalarCodec.GetKind(typeof(TArg));
        var expectedResultKind = PrimitiveScalarCodec.GetKind(typeof(TRes));

        return (argBits, argKind, resultKind) =>
        {
            if (argKind != expectedArgKind || resultKind != expectedResultKind)
            {
                throw new InvalidOperationException("The scalar callback signature does not match the guest request.");
            }

            var arg = (TArg)PrimitiveScalarCodec.Unpack(argBits, argKind);
            return PrimitiveScalarCodec.Pack(typedCallback(arg)!, resultKind);
        };
    }

    private delegate long ScalarCallbackInvoker(long argBits, int argKind, int resultKind);
}
