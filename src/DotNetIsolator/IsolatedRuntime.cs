using DotNetIsolator.Internal;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
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
    private readonly Func<int, int, long, int, int, int, long> _invokeScalarMethod;
    private readonly Action<int> _invokeScalarBatchMethod;
    private readonly Func<int, int, long> _invokeInt32MethodNoArgsPacked;
    private readonly Func<int, int, int, long> _invokeInt32MethodPacked;
    private readonly Func<int, int, int> _invokeVoidMethod;
    private readonly Func<int, int, int, int> _invokeVoidMethodInt32;
    private readonly Action<int, int, int> _invokeByteArrayMethod;
    private readonly Action<int, int, int> _invokeBlittableArrayMethod;
    private readonly Action<int, int, int> _invokeBlittableListMethod;
    private readonly Action<int> _invokeBlittableArrayArgMethod;
    private readonly Action<int> _invokeDotNetMethod;
    private readonly Action<int> _releaseObject;
    private readonly ConcurrentDictionary<(string AssemblyName, string? Namespace, string? DeclaringTypeName, string TypeName, string MethodName, int NumArgs), IsolatedMethod> _methodLookupCache = new();
    private readonly ShadowStack _shadowStack;
    private readonly HostCallbackRegistry _callbacks = new();
    private readonly IsolatedRuntimeHost _host;
    private readonly RuntimeInstanceLease? _lease;
    private bool _isDisposed;

    public IsolatedRuntime(IsolatedRuntimeHost host)
    {
        _host = host;

        IsolatedRuntimeExports exports;
        RuntimeMemorySnapshot? snapshot = null;

        if (host.UseInstancePool)
        {
            // The rented instance is already started and reset to the clean post-startup state.
            var lease = host.RentRuntimeInstance(this);
            _lease = lease;
            _store = lease.Store;
            _instance = lease.Instance;
            exports = lease.Exports;
        }
        else
        {
            snapshot = host.GetRuntimeMemorySnapshot();
            _store = host.CreateStore(this);
            _instance = host.Linker.Instantiate(_store, host.Module);
            exports = IsolatedRuntimeExports.Bind(_instance);
        }

        _memory = exports.Memory;
        _malloc = exports.Malloc;
        _free = exports.Free;
        _instantiateDotNetClass = exports.InstantiateDotNetClass;
        _lookupDotNetMethod = exports.LookupDotNetMethod;
        _deserializeAsDotNetObject = exports.DeserializeAsDotNetObject;
        _invokeInt32MethodNoArgsPacked = exports.InvokeInt32MethodNoArgsPacked;
        _invokeInt32MethodPacked = exports.InvokeInt32MethodPacked;
        _invokeVoidMethod = exports.InvokeVoidMethod;
        _invokeVoidMethodInt32 = exports.InvokeVoidMethodInt32;
        _invokeByteArrayMethod = exports.InvokeByteArrayMethod;
        _invokeBlittableArrayMethod = exports.InvokeBlittableArrayMethod;
        _invokeBlittableListMethod = exports.InvokeBlittableListMethod;
        _invokeBlittableArrayArgMethod = exports.InvokeBlittableArrayArgMethod;
        _invokeScalarMethod = exports.InvokeScalarMethod;
        _invokeScalarBatchMethod = exports.InvokeScalarBatchMethod;
        _invokeDotNetMethod = exports.InvokeDotNetMethod;
        _releaseObject = exports.ReleaseObject;

        if (_lease is not null)
        {
            // Already started and reset by RentRuntimeInstance.
            _shadowStack = new ShadowStack(_memory, _malloc, _free);
        }
        else if (snapshot is null)
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

    // General primitive scalar fast path. The argument is bit-packed into argBits (argKind 0 means
    // no argument) and the result is returned bit-packed. The guest validates the signature against
    // the requested kinds. Errors are reported through a shadow-stack error slot.
    internal long InvokeScalarMethod(int monoMethodPtr, IsolatedObject? instance, long argBits, int argKind, int resultKind)
    {
        var errorParam = _shadowStack.Push<int>();
        try
        {
            var resultBits = _invokeScalarMethod(
                instance is null ? 0 : instance.GuestGCHandle,
                monoMethodPtr,
                argBits,
                argKind,
                resultKind,
                errorParam.Address);

            if (errorParam.Value != 0)
            {
                throw new IsolatedException(ReadDotNetString(errorParam.Value) ?? "The method call failed.");
            }

            return resultBits;
        }
        finally
        {
            errorParam.Pop();
        }
    }

    // Batched primitive scalar invocation: runs the method once per argument in a single boundary
    // crossing. The arguments are copied into one guest buffer and the results read back from one
    // guest buffer, amortizing the host/guest boundary and host-side per-call overhead.
    internal TRes[] InvokeScalarBatch<T0, TRes>(int monoMethodPtr, IsolatedObject? instance, ReadOnlySpan<T0> args, int argKind, int resultKind)
        where T0 : unmanaged
        where TRes : unmanaged
    {
        var count = args.Length;
        if (count == 0)
        {
            return Array.Empty<TRes>();
        }

        var argsPtr = CopyValue<T0>(args, addLengthPrefix: false);
        var resultsByteCount = checked(count * Unsafe.SizeOf<TRes>());
        var resultsPtr = _malloc(resultsByteCount);
        if (resultsPtr == 0)
        {
            _free(argsPtr);
            throw new InvalidOperationException($"malloc failed when trying to allocate {resultsByteCount} bytes for batch results");
        }

        var len = Marshal.SizeOf<BatchInvocation>();
        var wasmPtr = _shadowStack.PushFrame(len);
        try
        {
            var invocationStruct = _memory.GetSpan(wasmPtr, len);
            ref var invocation = ref MemoryMarshal.AsRef<BatchInvocation>(invocationStruct);
            invocation = new BatchInvocation
            {
                Target = instance is null ? 0 : instance.GuestGCHandle,
                MethodPtr = monoMethodPtr,
                Args = argsPtr,
                Count = count,
                ArgKind = argKind,
                ResultKind = resultKind,
                Results = resultsPtr,
            };

            _invokeScalarBatchMethod(wasmPtr);

            if (invocation.ErrorMessage != 0)
            {
                throw new IsolatedException(ReadDotNetString(invocation.ErrorMessage) ?? "The batch method call failed.");
            }

            var results = new TRes[count];
            _memory.GetSpan<byte>(resultsPtr, resultsByteCount).CopyTo(MemoryMarshal.AsBytes(results.AsSpan()));
            return results;
        }
        finally
        {
            _shadowStack.PopFrame(wasmPtr, len);
            _free(resultsPtr);
            _free(argsPtr);
        }
    }

    internal void InvokeVoidMethod(int monoMethodPtr, IsolatedObject? instance)
    {
        var errorMessagePtr = _invokeVoidMethod(
            instance is null ? 0 : instance.GuestGCHandle,
            monoMethodPtr);

        UnpackVoidMethodResult(errorMessagePtr);
    }

    internal void InvokeVoidMethod(int monoMethodPtr, IsolatedObject? instance, int arg0)
    {
        var errorMessagePtr = _invokeVoidMethodInt32(
            instance is null ? 0 : instance.GuestGCHandle,
            monoMethodPtr,
            arg0);

        UnpackVoidMethodResult(errorMessagePtr);
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

    // Zero-copy fast path for exact () -> T[] methods where T is a blittable primitive.
    // The guest returns a pointer to the array's pinned element storage; the host copies the
    // raw element bytes once into a fresh host array. This bypasses guest-side object-graph
    // serialization entirely, mirroring the existing () -> byte[] fast path.
    internal T[]? InvokeBlittableArrayMethod<T>(int monoMethodPtr, IsolatedObject? instance) where T : unmanaged
        => InvokeBlittableSequence<T>(_invokeBlittableArrayMethod, monoMethodPtr, instance);

    // Zero-copy fast path for exact () -> List<T> methods where T is a blittable primitive. The guest
    // returns a pointer to the list's backing-array storage for its live element count.
    internal List<T>? InvokeBlittableListMethod<T>(int monoMethodPtr, IsolatedObject? instance) where T : unmanaged
    {
        var values = InvokeBlittableSequence<T>(_invokeBlittableListMethod, monoMethodPtr, instance);
        return values is null ? null : new List<T>(values);
    }

    private T[]? InvokeBlittableSequence<T>(Action<int, int, int> invokeExport, int monoMethodPtr, IsolatedObject? instance) where T : unmanaged
    {
        var len = Marshal.SizeOf<BlittableArrayInvocationResult>();
        var wasmPtr = _shadowStack.PushFrame(len);
        try
        {
            var resultStruct = _memory.GetSpan(wasmPtr, len);
            ref var result = ref MemoryMarshal.AsRef<BlittableArrayInvocationResult>(resultStruct);
            result = default;

            invokeExport(
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
                if (result.Length == 0)
                {
                    return Array.Empty<T>();
                }

                if (result.ElementSize != Unsafe.SizeOf<T>())
                {
                    throw new IsolatedException(
                        $"The guest returned {result.ElementSize}-byte elements but {Unsafe.SizeOf<T>()}-byte elements were expected.");
                }

                var values = new T[result.Length];
                var byteCount = checked(result.Length * result.ElementSize);
                _memory.GetSpan<byte>(result.Data, byteCount).CopyTo(MemoryMarshal.AsBytes(values.AsSpan()));
                return values;
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

    // Zero-copy fast path for exact (T[]) -> TRes methods where T is a blittable primitive.
    // The host copies the raw element bytes into a guest buffer once; the guest builds the managed
    // array directly from those bytes, bypassing managed argument serialization. The return value is
    // serialized through the normal path, so any return type is supported.
    internal TRes InvokeBlittableArrayArgMethod<T, TRes>(int monoMethodPtr, IsolatedObject? instance, T[] arg, int elementKind) where T : unmanaged
    {
        var argDataPtr = arg.Length == 0 ? 0 : CopyValue<T>(arg, addLengthPrefix: false);
        var len = Marshal.SizeOf<BlittableArgInvocation>();
        var wasmPtr = _shadowStack.PushFrame(len);
        try
        {
            var invocationStruct = _memory.GetSpan(wasmPtr, len);
            ref var invocation = ref MemoryMarshal.AsRef<BlittableArgInvocation>(invocationStruct);
            invocation = new BlittableArgInvocation
            {
                Target = instance is null ? 0 : instance.GuestGCHandle,
                MethodPtr = monoMethodPtr,
                ArgData = argDataPtr,
                ArgLength = arg.Length,
                ArgElementSize = Unsafe.SizeOf<T>(),
                ArgElementKind = elementKind,
            };

            _invokeBlittableArrayArgMethod(wasmPtr);

            if (invocation.ResultException != 0)
            {
                throw new IsolatedException(ReadDotNetString(invocation.ResultException));
            }

            if (invocation.ResultSerialized == 0)
            {
                return default!;
            }

            var resultBytes = _memory
                .GetSpan(invocation.ResultSerialized, invocation.ResultSerializedLength)
                .ToArray();
            var result = MessagePackCompatibility.DeserializeObject<TRes>(resultBytes)!;
            ReleaseGCHandle(invocation.ResultSerializedGCHandle);
            return result;
        }
        finally
        {
            _shadowStack.PopFrame(wasmPtr, len);
            if (argDataPtr != 0)
            {
                Free(argDataPtr);
            }
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

    private void UnpackVoidMethodResult(int errorMessagePtr)
    {
        if (errorMessagePtr != 0)
        {
            throw new IsolatedException(ReadDotNetString(errorMessagePtr) ?? "The method call failed.");
        }
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

        if (_lease is not null)
        {
            // Return the instance to the host pool for reuse instead of tearing it down.
            _host.ReturnRuntimeInstance(_lease);
        }
        else
        {
            _store.Dispose();
        }
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
        => _callbacks.Add(name, callback);

    private IsolatedMethod LookupDelegateMethod(MulticastDelegate @delegate)
    {
        var method = @delegate.Method;
        var methodType = method.DeclaringType!;
        var wasmMethod = GetMethod(methodType.Assembly.GetName().Name!, methodType.Namespace, methodType.DeclaringType?.Name, methodType.Name, method.Name, -1);
        return wasmMethod;
    }

    internal long InvokeScalarCallback(string name, long argBits, int argKind, int resultKind)
        => _callbacks.InvokeScalar(name, argBits, argKind, resultKind);

    internal int AcceptCallFromGuest(int invocationPtr, int invocationLength, int resultPtrPtr, int resultLengthPtr)
    {
        var response = _callbacks.Invoke(_memory.GetSpan<byte>(invocationPtr, invocationLength));
        var resultBytes = response.ResultBytes;
        var resultPtr = resultBytes is null ? 0 : CopyValue<byte>(resultBytes, false);
        _memory.WriteInt32(resultPtrPtr, resultPtr);
        _memory.WriteInt32(resultLengthPtr, resultBytes is null ? 0 : resultBytes.Length);
        return response.IsSuccess ? 1 : 0;
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
