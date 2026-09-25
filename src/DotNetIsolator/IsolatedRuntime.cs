using DotNetIsolator.Internal;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Wasmtime;

namespace DotNetIsolator;

public class IsolatedRuntime : IDisposable
{
    private readonly Func<int, int, long, long, int, int, (long, int)> _invokeScalar2;
    private readonly Func<int, int, long, long, long, int, int, (long, int)> _invokeScalar3;
    private readonly Func<int, int, long, long, long, long, int, int, (long, int)> _invokeScalar4;
    private readonly Store _store;
    private readonly Instance _instance;
    private readonly Memory _memory;
    private readonly Func<int, int> _malloc;
    private readonly Action<int> _free;
    private readonly Func<int, int, int, int, int> _instantiateDotNetClass;
    private readonly Func<int, int, int, int, int, int, int> _lookupDotNetMethod;
    private readonly Func<int, int, int> _deserializeAsDotNetObject;
    private readonly Func<int, int, long, int, int, (long, int)> _invokeScalarMethod;
    private readonly Func<int, int, long, int, int> _invokeScalarVoidMethod;
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
    private int _isDisposed;

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
        _invokeScalar2 = exports.InvokeScalar2;
        _invokeScalar3 = exports.InvokeScalar3;
        _invokeScalar4 = exports.InvokeScalar4;
        _invokeScalarMethod = exports.InvokeScalarMethod;
        _invokeScalarVoidMethod = exports.InvokeScalarVoidMethod;
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
        if (store.GetData() is not IsolatedRuntime runtime)
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
        => CreateObject(typeof(T).Assembly.GetName().Name!, typeof(T).Namespace, GetMonoDeclaringTypeName(typeof(T)), typeof(T).Name);

    public IsolatedObject CopyObject<T>(T value)
    {
        using var serializedBytes = ObjectGraphSerializer.SerializeWithTypeBuffer(value);
        var serializedBytesAddress = CopyValueLengthPrefixed(serializedBytes.Span);
        var errorMessageBuf = _shadowStack.Push<int>();
        try
        {
            var gcHandle = _deserializeAsDotNetObject(serializedBytesAddress, errorMessageBuf.Address);

            if (errorMessageBuf.Value != 0)
            {
                var errorMessage = ReadDotNetString(errorMessageBuf.Value);
                throw new IsolatedException(errorMessage);
            }

            return new IsolatedObject(this, gcHandle, typeof(T).Assembly.GetName().Name!, typeof(T).Namespace, GetMonoDeclaringTypeName(typeof(T)), typeof(T).Name);
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
        => GetMethod(type.Assembly.GetName().Name!, type.Namespace, GetMonoDeclaringTypeName(type), type.Name, methodName);

    public IsolatedMethod GetMethod(Type type, string methodName, int numArgs)
        => GetMethod(type.Assembly.GetName().Name!, type.Namespace, GetMonoDeclaringTypeName(type), type.Name, methodName, numArgs);

    public IsolatedMethod GetMethod(string assemblyName, string? @namespace, string? declaringTypeName, string typeName, string methodName, int numArgs = -1)
    {
        var key = (assemblyName, @namespace, declaringTypeName, typeName, methodName, numArgs);

        // Fast path: avoid allocating the GetOrAdd factory closure on cache hits (the common case,
        // e.g. every lambda invocation re-looks-up its method).
        if (_methodLookupCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        // Consider a multilevel cache keyed first by type so that successive "GetMethod" calls on the same type
        // don't have to hash so many strings. Also handle lookup failures in a better way.
        return _methodLookupCache.GetOrAdd(key, info =>
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

    // Both the value and error pointer return in Wasm registers.
    internal long InvokeScalarMethod(int method, IsolatedObject? instance, long bits, int kind, int resultKind)
        => ReadScalarResult(_invokeScalarMethod(instance?.GuestGCHandle ?? 0, method, bits, kind, resultKind));

    internal long InvokeScalarMethod2(int method, IsolatedObject? instance, long a0, long a1, int kinds, int resultKind)
        => ReadScalarResult(_invokeScalar2(instance?.GuestGCHandle ?? 0, method, a0, a1, kinds, resultKind));

    internal long InvokeScalarMethod3(int method, IsolatedObject? instance, long a0, long a1, long a2, int kinds, int resultKind)
        => ReadScalarResult(_invokeScalar3(instance?.GuestGCHandle ?? 0, method, a0, a1, a2, kinds, resultKind));

    internal long InvokeScalarMethod4(int method, IsolatedObject? instance, long a0, long a1, long a2, long a3, int kinds, int resultKind)
        => ReadScalarResult(_invokeScalar4(instance?.GuestGCHandle ?? 0, method, a0, a1, a2, a3, kinds, resultKind));

    private long ReadScalarResult((long Bits, int Error) result)
    {
        if (result.Error != 0)
            throw new IsolatedException(ReadDotNetString(result.Error) ?? "The method call failed.");
        return result.Bits;
    }

    internal void InvokeScalarVoidMethod(int monoMethodPtr, IsolatedObject? instance, long argBits, int argKind)
    {
        var errorMessagePtr = _invokeScalarVoidMethod(
            instance is null ? 0 : instance.GuestGCHandle,
            monoMethodPtr,
            argBits,
            argKind);

        UnpackVoidMethodResult(errorMessagePtr);
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
        var len = Marshal.SizeOf<BlittableArrayInvocationResult>();
        var wasmPtr = _shadowStack.PushFrame(len);
        try
        {
            var resultStruct = _memory.GetSpan(wasmPtr, len);
            ref var result = ref MemoryMarshal.AsRef<BlittableArrayInvocationResult>(resultStruct);
            result = default;

            _invokeBlittableListMethod(
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
                    return new List<T>(0);
                }

                if (result.ElementSize != Unsafe.SizeOf<T>())
                {
                    throw new IsolatedException(
                        $"The guest returned {result.ElementSize}-byte elements but {Unsafe.SizeOf<T>()}-byte elements were expected.");
                }

                var values = new List<T>(result.Length);
                CollectionsMarshal.SetCount(values, result.Length);
                var byteCount = checked(result.Length * result.ElementSize);
                _memory.GetSpan<byte>(result.Data, byteCount)
                    .CopyTo(MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(values)));
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

    // Copy a primitive array/list into fresh guest storage. Native scalar, void and array
    // results avoid serialization; all other result types use the managed fallback.
    internal TRes InvokeBlittableArrayArgMethod<T, TRes>(int monoMethodPtr, IsolatedObject? instance, ReadOnlySpan<T> arg, int elementKind, out bool supported, bool isList = false, bool isVoid = false) where T : unmanaged
    {
        supported = true;
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
                ArgumentIsList = isList ? 1 : 0,
                ResultKind = isVoid ? -1 : CollectionResult<TRes>.Kind,
                ResultElementKind = CollectionResult<TRes>.ElementKind,
            };

            _invokeBlittableArrayArgMethod(wasmPtr);

            if (invocation.ResultException != 0)
            {
                throw new IsolatedException(ReadDotNetString(invocation.ResultException));
            }

            if (invocation.ResultKind == -3)
            {
                supported = false;
                return default!;
            }
            if (invocation.ResultKind > 0) return ScalarCodec<TRes>.Unpack(invocation.ResultBits);
            if (invocation.ResultKind == -1) return default!;
            if (invocation.ResultKind == -2)
            {
                if (invocation.ResultSerializedGCHandle == 0) return default!;
                try { return CollectionResult<TRes>.Copy!(this, invocation.ResultSerialized, invocation.ResultSerializedLength); }
                finally { ReleaseGCHandle(invocation.ResultSerializedGCHandle); }
            }

            if (invocation.ResultSerialized == 0)
            {
                return default!;
            }

            try
            {
                var resultSpan = _memory.GetSpan(invocation.ResultSerialized, invocation.ResultSerializedLength);
                unsafe
                {
                    fixed (byte* resultPtr = resultSpan)
                    {
                        using var resultStream = new UnmanagedMemoryStream(resultPtr, resultSpan.Length);
                        return MessagePackCompatibility.DeserializeObject<TRes>(resultStream)!;
                    }
                }
            }
            finally
            {
                ReleaseGCHandle(invocation.ResultSerializedGCHandle);
            }
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

    private static class CollectionResult<TResult>
    {
        public static readonly int ElementKind = typeof(TResult).IsArray && typeof(TResult).GetArrayRank() == 1
            ? PrimitiveScalarCodec.GetKind(typeof(TResult).GetElementType()!) : 0;
        public static readonly int Kind = ElementKind != 0 ? -2 : PrimitiveScalarCodec.GetKind(typeof(TResult));
        public static readonly Func<IsolatedRuntime, int, int, TResult>? Copy = ElementKind == 0 ? null
            : typeof(IsolatedRuntime).GetMethod(nameof(CopyCollectionArray), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
                .MakeGenericMethod(typeof(TResult).GetElementType()!)
                .CreateDelegate<Func<IsolatedRuntime, int, int, TResult>>();
    }

    private static T[] CopyCollectionArray<T>(IsolatedRuntime runtime, int address, int length) where T : unmanaged
    {
        var result = new T[length];
        if (length != 0) runtime._memory.GetSpan(address, checked(length * Unsafe.SizeOf<T>())).CopyTo(MemoryMarshal.AsBytes(result.AsSpan()));
        return result;
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
                // Deserialize directly from guest memory instead of copying the payload into a host
                // array first. The host deserializes using the expected result type rather than
                // trusting guest-provided top-level type metadata.
                try
                {
                    var resultSpan = _memory.GetSpan(invocation.ResultSerialized, invocation.ResultSerializedLength);
                    unsafe
                    {
                        fixed (byte* resultPtr = resultSpan)
                        {
                            using var resultStream = new UnmanagedMemoryStream(resultPtr, resultSpan.Length);
                            return MessagePackCompatibility.DeserializeObject<TRes>(resultStream)!;
                        }
                    }
                }
                finally
                {
                    ReleaseGCHandle(invocation.ResultSerializedGCHandle);
                }
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
        if (Volatile.Read(ref _isDisposed) == 0)
        {
            _releaseObject(guestGCHandle);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) != 0)
        {
            return;
        }

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
        var wasmMethod = GetMethod(methodType.Assembly.GetName().Name!, methodType.Namespace, GetMonoDeclaringTypeName(methodType), methodType.Name, method.Name, -1);
        return wasmMethod;
    }

    private static string? GetMonoDeclaringTypeName(Type type)
        => type.DeclaringType is null ? null : GetMonoTypeName(type.DeclaringType);

    private static string GetMonoTypeName(Type type)
    {
        var declaringTypeName = GetMonoDeclaringTypeName(type);
        return declaringTypeName is null ? type.Name : $"{declaringTypeName}/{type.Name}";
    }

    internal int ResolveCallback(string name) => _callbacks.Resolve(name);

    internal long InvokeScalarCallback(int callbackId, long argBits, int argKind, int resultKind)
        => _callbacks.InvokeScalar(callbackId, argBits, argKind, resultKind);

    internal int AcceptCallFromGuest(int invocationPtr, int invocationLength, int resultPtrPtr, int resultLengthPtr)
    {
        var invocationSpan = _memory.GetSpan<byte>(invocationPtr, invocationLength);
        HostCallbackResponse response;
        unsafe
        {
            fixed (byte* invocationFixed = invocationSpan)
            {
                using var manager = new UnmanagedMemoryManager(invocationFixed, invocationLength);
                response = _callbacks.Invoke(manager.Memory);
            }
        }

        return CopyCallbackResponse(response, resultPtrPtr, resultLengthPtr);
    }

    internal int AcceptRawCallFromGuest(int callbackId, int argsPtr, int count, int resultPtrPtr, int resultLengthPtr)
        => CopyCallbackResponse(_callbacks.InvokeRaw(callbackId, _memory, argsPtr, count), resultPtrPtr, resultLengthPtr);

    private int CopyCallbackResponse(HostCallbackResponse response, int resultPtrPtr, int resultLengthPtr)
    {
        var resultBytes = response.ResultBytes;
        // A non-null empty result still needs an address to distinguish it from null.
        var resultPtr = resultBytes is null ? 0 : resultBytes.Length == 0 ? _malloc(1) : CopyValue<byte>(resultBytes, false);
        if (resultBytes is not null && resultPtr == 0) throw new InvalidOperationException("Could not allocate callback result.");
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
