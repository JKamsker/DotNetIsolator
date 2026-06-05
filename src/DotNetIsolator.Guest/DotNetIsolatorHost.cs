using DotNetIsolator.Guest;
using MessagePack;
using System.Text;
using DotNetIsolator.Internal;

namespace DotNetIsolator;

public static class DotNetIsolatorHost
{
    public static byte[] InvokeRaw(string callbackName, params byte[]?[] args)
        => InvokeRaw<object>(callbackName, args);

    public static void Invoke(string callbackName, params object[] args)
    {
        _ = PerformCall<object>(
            CreateCall(callbackName, SerializeArgs(args), isRawCall: false),
            readResult: false);
    }

    public static unsafe byte[] InvokeRaw<T>(string callbackName, params byte[]?[] args)
    {
        return PerformCall<byte[]>(new GuestToHostCall
        {
            CallbackName = callbackName,
            Args = args,
            IsRawCall = true,
        });
    }

    public static unsafe T Invoke<T>(string callbackName, params object[] args)
    {
        // Note that this overload won't work if the host is AOT compiled because it will be unable to
        // deserialize these arbitrary arg types. For that scenario, use the Memory<byte>[] overload instead.
        return PerformCall<T>(
            CreateCall(callbackName, SerializeArgs(args), isRawCall: false),
            readResult: true);
    }

    private static byte[]?[] SerializeArgs(object[] args)
        => args.Select(a => a is null
            ? null
            : MessagePackCompatibility.SerializeObject(a.GetType(), a))
            .ToArray();

    private static GuestToHostCall CreateCall(string callbackName, byte[]?[] args, bool isRawCall)
        => new()
        {
            CallbackName = callbackName,
            Args = args,
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
                    return !readResult || !hasResult
                        ? default!
                        : callInfo.IsRawCall
                            ? (T)(object)result.ToArray()
                            : MessagePackCompatibility.DeserializeObject<T>(result.ToArray())!;
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
}
