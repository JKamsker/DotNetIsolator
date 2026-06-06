using DotNetIsolator.Internal;
using MessagePack;
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

        var args = argKind == PrimitiveScalarCodec.None
            ? Array.Empty<object?>()
            : new[] { PrimitiveScalarCodec.Unpack(argBits, argKind) };

        var result = callback.Delegate.DynamicInvoke(args)
            ?? throw new InvalidOperationException("The scalar callback returned null.");

        return PrimitiveScalarCodec.Pack(result, resultKind);
    }

    private static object?[] DeserializeArguments(Type[] parameterTypes, GuestToHostCall invocationInfo)
    {
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
    private RegisteredCallback(Delegate callback, Type[] parameterTypes, Type returnType)
    {
        Delegate = callback;
        ParameterTypes = parameterTypes;
        ReturnType = returnType;
    }

    public Delegate Delegate { get; }

    public Type[] ParameterTypes { get; }

    public Type ReturnType { get; }

    public static RegisteredCallback Create(Delegate callback)
        => new(
            callback,
            callback.Method.GetParameters().Select(p => p.ParameterType).ToArray(),
            callback.Method.ReturnType);
}
