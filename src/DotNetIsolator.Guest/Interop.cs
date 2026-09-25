using System.Runtime.CompilerServices;

namespace DotNetIsolator.Guest;

internal static class Interop
{
    [MethodImpl(MethodImplOptions.InternalCall)]
    public static unsafe extern bool CallHost(void* invocationPtr, int invocationLength, out void* result, out int resultLength);

    // This ordering uses the existing WASI interpreter wrapper (i32, i32, i32, i64) -> i64.
    [MethodImpl(MethodImplOptions.InternalCall)]
    public static extern long CallHostScalar(int callbackId, int kinds, out int error, long argBits);

    [MethodImpl(MethodImplOptions.InternalCall)]
    public static unsafe extern bool CallHostRaw(int callbackId, byte[]?[] args, out void* result, out int resultLength);

    [MethodImpl(MethodImplOptions.InternalCall)]
    public static unsafe extern int ResolveCallback(void* namePtr, int nameLength);

    [MethodImpl(MethodImplOptions.InternalCall)]
    public static unsafe extern void FreeHostCallResult(void* result);
}
