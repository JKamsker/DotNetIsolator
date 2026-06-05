using DotNetIsolator.Internal;
using MessagePack;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using Wasmtime;

namespace DotNetIsolator;

public class IsolatedRuntime : IDisposable
{
    private readonly Store _store;
    private readonly Instance _instance;
    private readonly Memory _memory;
    private readonly Func<int, int> _malloc;
    private readonly Action<int> _free;
    private readonly Func<int, int, int, int, int> _instantiateDotNetClass;
    private readonly Func<int, int, int, int, int, int, int> _lookupDotNetMethod;
    private readonly Func<int, int, int> _deserializeAsDotNetObject;
    private readonly Func<int, int, long> _invokeInt32MethodNoArgsPacked;
    private readonly Func<int, int, int, long> _invokeInt32MethodPacked;
    private readonly Action<int, int, int> _invokeByteArrayMethod;
    private readonly Action<int> _invokeDotNetMethod;
    private readonly Action<int> _releaseObject;
    private readonly ConcurrentDictionary<(string AssemblyName, string? Namespace, string? DeclaringTypeName, string TypeName, string MethodName, int NumArgs), IsolatedMethod> _methodLookupCache = new();
    private readonly ShadowStack _shadowStack;
    private readonly Dictionary<string, Delegate> _registeredCallbacks = new();
    private bool _isDisposed;

    public IsolatedRuntime(IsolatedRuntimeHost host)
    {
        var snapshot = host.GetRuntimeMemorySnapshot();
        var store = host.CreateStore(this);

        _store = store;
        _instance = host.Linker.Instantiate(store, host.Module);
        var exports = IsolatedRuntimeExports.Bind(_instance);

        _memory = exports.Memory;
        _malloc = exports.Malloc;
        _free = exports.Free;
        _instantiateDotNetClass = exports.InstantiateDotNetClass;
        _lookupDotNetMethod = exports.LookupDotNetMethod;
        _deserializeAsDotNetObject = exports.DeserializeAsDotNetObject;
        _invokeInt32MethodNoArgsPacked = exports.InvokeInt32MethodNoArgsPacked;
        _invokeInt32MethodPacked = exports.InvokeInt32MethodPacked;
        _invokeByteArrayMethod = exports.InvokeByteArrayMethod;
        _invokeDotNetMethod = exports.InvokeDotNetMethod;
        _releaseObject = exports.ReleaseObject;

        if (snapshot is null)
        {
            _shadowStack = new ShadowStack(_memory, _malloc, _free);
            exports.Start();
        }
        else
        {
            snapshot.RestoreTo(_memory);
            _shadowStack = new ShadowStack(_memory, _malloc, _free);
        }
    }

    internal static IsolatedRuntime FromStore(Store store)
    {
        var runtime = (IsolatedRuntime?)store.GetData();
        if (runtime is null)
        {
            throw new InvalidOperationException("Runtime was not set on the store");
        }

        return runtime;
    }

    internal ShadowStack ShadowStack => _shadowStack;

    public IsolatedObject CreateObject(string assemblyName, string? @namespace, string className)
        => CreateObject(assemblyName, @namespace, declaringTypeName: null, className);

    public IsolatedObject CreateObject(string assemblyName, string? @namespace, string? declaringTypeName, string className)
    {
        var errorMessageParam = _shadowStack.Push<int>();
        try
        {
            // All these CopyValue strings are freed inside the C code
            var monoClassName = declaringTypeName is null ? className : $"{declaringTypeName}/{className}";
            var gcHandle = _instantiateDotNetClass(CopyValue(assemblyName), CopyValue(@namespace), CopyValue(monoClassName), errorMessageParam.Address);

            if (errorMessageParam.Value != 0)
            {
                var errorString = _memory.ReadNullTerminatedString(errorMessageParam.Value);
                _free(errorMessageParam.Value);
                throw new IsolatedException(errorString);
            }

            return new IsolatedObject(this, gcHandle, assemblyName, @namespace, declaringTypeName, className);
        }
        finally
        {
            errorMessageParam.Pop();
        }
    }

    public IsolatedObject CreateObject<T>()
        => CreateObject(typeof(T).Assembly.GetName().Name!, typeof(T).Namespace, typeof(T).DeclaringType?.Name, typeof(T).Name);

    public IsolatedObject CopyObject<T>(T value)
    {
        var serializedBytes = MessagePackCompatibility.SerializeTypeless(value);
        var serializedBytesAddress = CopyValueLengthPrefixed(serializedBytes);
        var errorMessageBuf = _shadowStack.Push<int>();
        try
        {
            var gcHandle = _deserializeAsDotNetObject(serializedBytesAddress, errorMessageBuf.Address);

            if (errorMessageBuf.Value != 0)
            {
                var errorMessage = ReadDotNetString(errorMessageBuf.Value);
                throw new IsolatedException(errorMessage);
            }

            return new IsolatedObject(this, gcHandle, typeof(T).Assembly.GetName().Name!, typeof(T).Namespace, typeof(T).DeclaringType?.Name, typeof(T).Name);
        }
        finally
        {
            errorMessageBuf.Pop();
            Free(serializedBytesAddress);
        }
    }

    internal int CopyValue(string? value)
    {
        if (value is null)
        {
            return 0;
        }

        var valueUtf8Length = Encoding.UTF8.GetByteCount(value);
        var resultPtr = _malloc(valueUtf8Length + 1);
        if (resultPtr == 0)
        {
            throw new InvalidOperationException($"malloc failed when trying to allocate {valueUtf8Length} bytes");
        }

        var destinationSpan = _memory.GetSpan(resultPtr, valueUtf8Length);
        Encoding.UTF8.GetBytes(value, destinationSpan);
        _memory.WriteByte(resultPtr + valueUtf8Length, 0); // Null-terminated string
        return resultPtr;
    }

    internal int CopyValue<T>(ReadOnlySpan<T> value, bool addLengthPrefix) where T : unmanaged
    {
        var lengthPrefixSize = addLengthPrefix ? 4 : 0;
        var valueAsBytes = MemoryMarshal.AsBytes(value);
        var length = valueAsBytes.Length;
        var resultPtr = _malloc(length + lengthPrefixSize);
        if (resultPtr == 0)
        {
            throw new InvalidOperationException($"malloc failed when trying to allocate {length + 4} bytes");
        }

        if (addLengthPrefix)
        {
            _memory.WriteInt32(resultPtr, length);
        }

        var destinationSpan = _memory.GetSpan(resultPtr + lengthPrefixSize, length);
        valueAsBytes.CopyTo(destinationSpan);
        return resultPtr;
    }

    internal int CopyValueLengthPrefixed(ReadOnlySpan<byte> value)
        => CopyValue(value, addLengthPrefix: true);

    public IsolatedMethod GetMethod(Type type, string methodName)
        => GetMethod(type.Assembly.GetName().Name!, type.Namespace, type.DeclaringType?.Name, type.Name, methodName);

    public IsolatedMethod GetMethod(Type type, string methodName, int numArgs)
        => GetMethod(type.Assembly.GetName().Name!, type.Namespace, type.DeclaringType?.Name, type.Name, methodName, numArgs);

    public IsolatedMethod GetMethod(string assemblyName, string? @namespace, string? declaringTypeName, string typeName, string methodName, int numArgs = -1)
    {
        // Consider a multilevel cache keyed first by type so that successive "GetMethod" calls on the same type
        // don't have to hash so many strings. Also handle lookup failures in a better way.
        return _methodLookupCache.GetOrAdd((assemblyName, @namespace, declaringTypeName, typeName, methodName, numArgs), info =>
        {
            // All these CopyValue strings are freed inside the C code
            var monoClassName = info.DeclaringTypeName is null ? info.TypeName : $"{info.DeclaringTypeName}/{info.TypeName}";

            var errorMessageParam = _shadowStack.Push<int>();
            try
            {
                var methodPtr = _lookupDotNetMethod(
                    CopyValue(info.AssemblyName),
                    CopyValue(info.Namespace),
                    CopyValue(monoClassName),
                    CopyValue(info.MethodName),
                    info.NumArgs,
                    errorMessageParam.Address);

                if (errorMessageParam.Value != 0)
                {
                    var errorString = _memory.ReadNullTerminatedString(errorMessageParam.Value);
                    _free(errorMessageParam.Value);
                    throw new IsolatedException(errorString);
                }

                return new IsolatedMethod(this, info.MethodName, methodPtr);
            }
            finally
            {
                errorMessageParam.Pop();
            }
        });
    }

    // Internal because you only need to call it via DotNetMethod
    internal int InvokeInt32Method(int monoMethodPtr, IsolatedObject? instance)
    {
        var packedResult = unchecked((ulong)_invokeInt32MethodNoArgsPacked(
            instance is null ? 0 : instance.GuestGCHandle,
            monoMethodPtr));

        return UnpackInt32MethodResult(packedResult);
    }

    internal int InvokeInt32Method(int monoMethodPtr, IsolatedObject? instance, int arg0)
    {
        var packedResult = unchecked((ulong)_invokeInt32MethodPacked(
            instance is null ? 0 : instance.GuestGCHandle,
            monoMethodPtr,
            arg0));

        return UnpackInt32MethodResult(packedResult);
    }

    internal byte[]? InvokeByteArrayMethod(int monoMethodPtr, IsolatedObject? instance)
    {
        var len = Marshal.SizeOf<ByteArrayInvocationResult>();
        var wasmPtr = _shadowStack.PushFrame(len);
        try
        {
            var resultStruct = _memory.GetSpan(wasmPtr, len);
            ref var result = ref MemoryMarshal.AsRef<ByteArrayInvocationResult>(resultStruct);
            result = default;

            _invokeByteArrayMethod(
                wasmPtr,
                instance is null ? 0 : instance.GuestGCHandle,
                monoMethodPtr);

            if (result.ErrorMessage != 0)
            {
                throw new IsolatedException(ReadDotNetString(result.ErrorMessage) ?? "The method call failed.");
            }

            if (result.ResultGCHandle == 0)
            {
                return null;
            }

            try
            {
                var bytes = new byte[result.Length];
                if (result.Length > 0)
                {
                    _memory.GetSpan<byte>(result.Data, result.Length).CopyTo(bytes);
                }

                return bytes;
            }
            finally
            {
                ReleaseGCHandle(result.ResultGCHandle);
            }
        }
        finally
        {
            _shadowStack.PopFrame(wasmPtr, len);
        }
    }

    private int UnpackInt32MethodResult(ulong packedResult)
    {
        var errorMessagePtr = (int)(packedResult >> 32);
        if (errorMessagePtr != 0)
        {
            throw new IsolatedException(ReadDotNetString(errorMessagePtr) ?? "The method call failed.");
        }

        return unchecked((int)packedResult);
    }

    internal TRes InvokeDotNetMethod<TRes>(int monoMethodPtr, IsolatedObject? instance, ReadOnlySpan<int> argAddresses)
    {
        // Prepare an Invocation struct within guest memory
        var len = Marshal.SizeOf<Invocation>();
        var wasmPtr = _shadowStack.PushFrame(len);
        try
        {
            var invocationStruct = _memory.GetSpan(wasmPtr, len);
            ref var invocation = ref MemoryMarshal.AsRef<Invocation>(invocationStruct);
            invocation = new()
            {
                TargetGCHandle = instance is IsolatedObject o ? o.GuestGCHandle : 0,
                MethodPtr = monoMethodPtr,
                ArgsLengthPrefixedBuffers = argAddresses.Length == 0
                    ? 0
                    : CopyValue(argAddresses, addLengthPrefix: false), // Freed in C code
                ArgsLengthPrefixedBuffersLength = argAddresses.Length,
            };

            _invokeDotNetMethod(wasmPtr);
            if (invocation.ResultException != 0)
            {
                var exceptionString = ReadDotNetString(invocation.ResultException);
                throw new IsolatedException(exceptionString);
            }

            if (invocation.ResultSerialized == 0)
            {
                return default!;
            }
            else
            {
                var resultBytes = _memory
                    .GetSpan(invocation.ResultSerialized, invocation.ResultSerializedLength)
                    .ToArray();

                // The host deserializes using the expected result type instead of trusting guest-provided
                // top-level type metadata.
                var result = MessagePackCompatibility.DeserializeObject<TRes>(resultBytes)!;

                ReleaseGCHandle(invocation.ResultSerializedGCHandle);
                return result;
            }
        }
        finally
        {
            _shadowStack.PopFrame(wasmPtr, len);
        }
    }

    internal string? ReadDotNetString(int ptr)
    {
        if (ptr == 0)
        {
            return null;
        }

        // The source data is already a .NET string (length-prefixed UTF-16), but we want a string in
        // the host heap so we need to copy the bytes. The following is pretty direct but there might
        // be some marshalling method that does this even more succinctly.
        var stringLength = _memory.ReadInt32(ptr + 8); // MonoString has an 8-byte header for the object type
        var stringUtf16Bytes = _memory.GetSpan<byte>(ptr + 12, stringLength * 2);
        var stringChars = MemoryMarshal.Cast<byte, char>(stringUtf16Bytes);
        return new string(stringChars);
    }

    internal void ReleaseGCHandle(int guestGCHandle)
    {
        if (!_isDisposed)
        {
            _releaseObject(guestGCHandle);
        }
    }

    public void Dispose()
    {
        _isDisposed = true;
        _shadowStack.Dispose();
        _store.Dispose();
    }

    internal void Free(int malloced_ptr)
    {
        _free(malloced_ptr);
    }

    public void Invoke(Action value)
    {
        // TODO: Find a way of not serializing value.Target if it doesn't contain any fields we care about serializing
        // This makes invoking static lambdas vastly faster. It's not clear to me why the target is nonnull in these cases anyway.
        var targetInGuest = value.Target is null ? null : CopyObject(value.Target);
        try
        {
            LookupDelegateMethod(value).InvokeVoid(targetInGuest);
        }
        finally
        {
            targetInGuest?.ReleaseGCHandle(); // TODO: Make this into a 'Dispose' call on IsolatedObject?
        }
    }

    public TRes Invoke<TRes>(Func<TRes> value)
    {
        // TODO: Find a way of not serializing value.Target if it doesn't contain any fields we care about serializing
        // This makes invoking static lambdas vastly faster. It's not clear to me why the target is nonnull in these cases anyway.
        var targetInGuest = value.Target is null ? null : CopyObject(value.Target);
        try
        {
            return LookupDelegateMethod(value).Invoke<TRes>(targetInGuest);
        }
        finally
        {
            targetInGuest?.ReleaseGCHandle(); // TODO: Make this into a 'Dispose' call on IsolatedObject?
        }
    }

    public void RegisterCallback(string name, Delegate callback)
        => _registeredCallbacks.Add(name, callback);

    private IsolatedMethod LookupDelegateMethod(MulticastDelegate @delegate)
    {
        var method = @delegate.Method;
        var methodType = method.DeclaringType!;
        var wasmMethod = GetMethod(methodType.Assembly.GetName().Name!, methodType.Namespace, methodType.DeclaringType?.Name, methodType.Name, method.Name, -1);
        return wasmMethod;
    }

    internal int AcceptCallFromGuest(int invocationPtr, int invocationLength, int resultPtrPtr, int resultLengthPtr)
    {
        try
        {
            var invocationInfo = MessagePackSerializer.Deserialize<GuestToHostCall>(
                _memory.GetSpan<byte>(invocationPtr, invocationLength).ToArray(),
                MessagePackCompatibility.GuestToHostCallOptions);

            if (!_registeredCallbacks.TryGetValue(invocationInfo.CallbackName, out var callback))
            {
                var errorString = Encoding.UTF8.GetBytes($"There is no registered callback with name '{invocationInfo.CallbackName}'");
                var errorStringPtr = CopyValue<byte>(errorString, false);
                _memory.WriteInt32(resultPtrPtr, errorStringPtr);
                _memory.WriteInt32(resultLengthPtr, errorString.Length);
                return 0;
            }

            var expectedParameterTypes = callback.Method.GetParameters();
            var deserializedArgs = new object?[expectedParameterTypes.Length];
            for (var i = 0; i < expectedParameterTypes.Length; i++)
            {
                if (invocationInfo.IsRawCall)
                {
                    // Assumes the parameter type is byte[]
                    deserializedArgs[i] = invocationInfo.Args![i]?.ToArray();
                }
                else
                {
                    deserializedArgs[i] = MessagePackCompatibility.DeserializeObject(
                        expectedParameterTypes[i].ParameterType,
                        invocationInfo.Args[i]);
                }
            }

            var result = callback.DynamicInvoke(deserializedArgs);
            var resultBytes = result is null
                ? null
                : invocationInfo.IsRawCall
                    ? (byte[])result
                    : MessagePackCompatibility.SerializeObject(
                        callback.Method.ReturnType,
                        result);

            var resultPtr = resultBytes is null ? 0 : CopyValue<byte>(resultBytes, false);
            _memory.WriteInt32(resultPtrPtr, resultPtr);
            _memory.WriteInt32(resultLengthPtr, resultBytes is null ? 0 : resultBytes.Length);
            return 1; // Success
        }
        catch (Exception ex)
        {
            // We could supply the raw exception info to the guest, but since we consider the guest untrusted,
            // we don't want to expose arbitrary information about the host internals. Ideally this behavior would
            // vary based on whether this is a dev or prod scenario, but that's not a concept that exists natively
            // in .NET (whereas it does in ASP.NET Core).
            Console.Error.WriteLine(ex.ToString());
            var resultBytes = Encoding.UTF8.GetBytes("The call failed. See host console logs for details.");
            var resultPtr = CopyValue<byte>(resultBytes, false);
            _memory.WriteInt32(resultPtrPtr, resultPtr);
            _memory.WriteInt32(resultLengthPtr, resultBytes.Length);
            return 0; // Failure
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Invocation
    {
        public int TargetGCHandle;
        public int MethodPtr;
        public int ResultException;
        public int ResultSerialized;
        public int ResultSerializedLength;
        public int ResultSerializedGCHandle;
        public int ArgsLengthPrefixedBuffers;
        public int ArgsLengthPrefixedBuffersLength;
    }
}
