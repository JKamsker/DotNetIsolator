# Performance

DotNetIsolator has two separate costs:

1. Cold setup creates a Wasmtime engine, compiles or deserializes the bundled
   `DotNetIsolator.WasmApp.wasm` module, instantiates the module, and starts the
   .NET WASI runtime.
2. Warm calls cross the host/guest boundary and serialize arguments and return
   values with MessagePack.

The practical guidance is to create one `IsolatedRuntimeHost`, keep
`IsolatedRuntime` instances warm when possible, and reuse `IsolatedMethod`
lookups for repeated calls.

## Benchmark

Run the sample in Release mode:

```powershell
$env:WASI_SDK_PATH = 'C:\Users\Jonas\.wasi-sdk\wasi-sdk-25.0'
dotnet run -c Release --project sample\PerformanceSample\PerformanceSample.csproj
```

The sample measures:

* a direct host call to `BenchmarkTarget.Increment`
* an isolated warm-runtime call to the same method, with the isolated object and
  method lookup already created. This uses the scalar `int -> int` fast path.
* startup medians without the module cache, with a cold module cache, and with a
  warm module cache
* repeated runtime startup from one warm host, with and without the runtime
  memory snapshot

## Wizer status and replacements

The standalone Wizer project moved into Wasmtime, and the old CLI is no longer
the forward path. Wasmtime also documents serializing compiled modules to disk
and deserializing them later to skip compilation on the critical path:

* https://github.com/bytecodealliance/wizer
* https://docs.wasmtime.dev/examples-serialize.html
* https://bytecodealliance.github.io/wasmtime-dotnet/api/Wasmtime.Module.html

DotNetIsolator uses that mechanism through `Wasmtime.Module.Serialize` and
`Wasmtime.Module.DeserializeFile`. The cache is enabled by default through
`IsolatedRuntimeHostOptions.UsePrecompiledModuleCache` and can be redirected with
`PrecompiledModuleCacheDirectory`.

That cache is not a full Wizer snapshot of an already-started .NET runtime. It
removes repeated Wasmtime module compilation from host construction, but runtime
startup still pays the cost of instantiating the wasm module and running
`_start`.

The current Wasmtime 44 `wasmtime wizer` command was tested against the
generated .NET 10 WASI module. It cannot currently snapshot this module as-is:
the generated core module imports the WASI Preview 2 surface and DotNetIsolator
host functions, and even `wasmtime run -S cli=y` fails to satisfy
`wasi:clocks/monotonic-clock@0.2.0::subscribe-duration`. The Wizer CLI help also
states that the initialization function may not call imported functions, which
rules out using the generated `_start` function as a normal Wizer initializer.

DotNetIsolator therefore has an opt-in in-memory replacement:
`IsolatedRuntimeHostOptions.UseRuntimeMemorySnapshot`. When enabled, the host can
instantiate the bundled module once, run `_start`, copy the initialized linear
memory, and restore that memory into later runtimes before user code runs.

```csharp
using var host = new IsolatedRuntimeHost(new IsolatedRuntimeHostOptions
{
    UseRuntimeMemorySnapshot = true,
}).WithBinDirectoryAssemblyLoader();

host.PreloadRuntimeMemorySnapshot();

using var runtime = new IsolatedRuntime(host);
```

This snapshot is per-host and in-memory only. It improves latency for repeated
runtimes from the same host, but it does not help the first runtime unless the
snapshot is preloaded off the critical path. It is also narrower than Wizer: it
copies linear memory, not arbitrary tables or globals. It is covered by startup
and host-callback tests for the current generated .NET 10 module.

## Scalar fast path

The generic invocation path still supports arbitrary serializable object graphs,
but it is far too expensive for primitive calls. DotNetIsolator now has a native
`int -> int` fast path that bypasses object-graph serialization for
`IsolatedMethod.Invoke<int, int>`. It passes the integer argument directly to
`mono_runtime_invoke`, unboxes the integer result in native code, and only uses
the existing managed serialization path for other shapes.

This is intentionally narrow. It preserves existing behavior for complex
arguments and return values, while proving that the boundary can be made much
cheaper for common scalar signatures.

An intermediate version reserved one two-slot shadow-stack frame per call
instead of pushing two typed entries. The current scalar path removes that result
frame entirely for the successful
`int -> int` case. The WASM export returns one packed `i64`: the low 32 bits are
the integer result, and the high 32 bits are zero on success or a guest
`MonoString*` error pointer on failure. The host still reads guest memory for
errors, but the normal successful path no longer maps guest memory just to read
the result and success state.

This does not weaken sandboxing. The method still runs inside the same Wasmtime
instance and guest heap; the optimization only changes how a primitive result is
transported back over an already-authorized export call.

## Experiment ledger

### Accepted

The following paths were tested and kept:

* Wasmtime module serialization: enabled by default through
  `IsolatedRuntimeHostOptions.UsePrecompiledModuleCache`. This skips repeated
  Wasmtime module compilation during host construction, but does not snapshot
  initialized .NET runtime state.
* Runtime memory snapshot: opt-in through
  `IsolatedRuntimeHostOptions.UseRuntimeMemorySnapshot`. It copies initialized
  per-runtime linear memory before user code runs and restores that memory into
  later runtimes from the same host. It improves repeated runtime construction
  while preserving per-runtime guest memory ownership.
* Native `int -> int` invoke path: `IsolatedMethod.Invoke<int, int>` bypasses
  MessagePack and object-graph serialization.
* Packed scalar return: `dotnetisolator_invoke_i32_i32_packed` returns the
  primitive result and error state in one `i64`, avoiding guest-memory
  result-frame traffic for successful scalar calls.

### Rejected

The following paths were tested and rejected so they do not need to be
rediscovered without a new runtime, SDK, or workload:

* `wasmtime wizer` / standalone Wizer: the current .NET 10 WASI module imports
  the WASI Preview 2 surface and DotNetIsolator host functions during startup.
  The Wizer initializer also cannot call imports, so generated `_start` is not a
  valid initializer for this module shape.
* Dirty runtime pooling: not implemented because it would reuse a guest heap,
  GC handles, loaded assemblies, and static state after user code. That would
  trade away sandbox isolation safety for speed.
* `mono_method_get_unmanaged_thunk` for `int -> int`: a prototype cached the
  unmanaged thunk at method lookup and called it from the native fast path. It
  trapped inside the WASM runtime during method lookup with an out-of-bounds
  memory access, so it was rejected as unsafe for this target.
* `mono_wasm_invoke_method`: this exists in older `wasi.sdk`
  `mono-wasi/driver.h` headers, but it is not declared by the .NET 10
  `Microsoft.NETCore.App.Runtime.Mono.wasi-wasm` `wasm/driver.h` used by this
  repo. The current pack exposes `mono_wasm_marshal_get_managed_wrapper`
  instead, but that API is documented in the runtime source as a wrapper
  initializer for `[UnmanagedCallersOnly]` function pointers, not as an
  arbitrary reflected `MonoMethod` invoker.
* Lookup-time signature hoisting: a prototype exported
  `dotnetisolator_method_is_i32_i32`, cached the result in `IsolatedMethod`, and
  removed the native per-call signature check. It was slower in measurements:
  about `234 ns` versus `212 ns` after the shadow-stack frame optimization, and
  earlier about `332 ns` versus `248 ns` before that optimization.
* Reusable scalar call frame: a prototype reserved one per-runtime two-slot
  frame for scalar result/error transport and used an interlocked guard with a
  normal shadow-stack fallback for reentrancy. It tested correctly, but the
  larger benchmark still measured about `217.793 ns` per isolated call and
  `121x` direct-call overhead. The packed scalar return replaced it because it
  avoids that guest-memory frame on successful calls.
* Wasmtime pooling allocator as a startup optimization: it remains available as
  `IsolatedRuntimeHostOptions.UsePoolingAllocator`, but on this workload it made
  warm runtime startup worse. Representative samples were `49.752 ms` pooled
  versus `40.169 ms` unpooled, and later `62.802 ms` pooled versus `48.011 ms`
  unpooled.

## Measured Results

Measured on June 5, 2026:

* .NET SDK 10.0.204, runtime .NET 10.0.8
* Windows 10.0.26200, `win-x64`
* WASI SDK 25.0
* Wasmtime .NET package 44.0.0

Representative run with
`--host-iterations 50000000 --isolated-iterations 1000000 --startup-iterations 3`:

```text
Steady-state call overhead
Direct host Increment: total 87.627 ms, mean 1.753 ns
Isolated warm-runtime Increment: total 172.577 ms, mean 172.577 ns
Isolated/direct mean ratio: 98x

Startup medians
No module cache: host 300.490 ms, runtime 39.247 ms, object 340.200 us, method 119.300 us, first call 56.100 us
Cold module cache: host 329.123 ms, runtime 37.663 ms, object 326.600 us, method 105.600 us, first call 52.800 us
Warm module cache: host 1.633 ms, runtime 45.220 ms, object 336.000 us, method 120.100 us, first call 84.000 us
Warm host: runtime 41.087 ms, object 288.900 us, method 119.500 us, first call 58.100 us
Runtime memory snapshot preload: 74.771 ms
Warm runtime memory snapshot: runtime 16.916 ms, object 632.700 us, method 120.900 us, first call 74.300 us
```

Interpretation:

* A representative warm isolated scalar call is around `173 ns` on this machine,
  versus about `1.75 ns` for the direct host call. That is roughly `98x` slower
  for this tiny method.
* Before the scalar fast path, the same benchmark measured about `37 us` per
  isolated call and about `21,000x` direct-call overhead on this machine. The
  fast path, shadow-stack frame optimization, and packed scalar return cut the
  measured isolated call cost by roughly `214x`.
* The warm module cache cuts host construction from about `302 ms` to about
  `1.6 ms`, roughly a `190x` improvement for that phase.
* The first cache miss is slower than no cache because it compiles and writes the
  serialized module. The cache is intended for repeated host construction.
* Runtime startup on a warm host is still about `40 ms` because the .NET WASI
  runtime is still instantiated and started.
* The runtime memory snapshot moves one-time startup work into a preload step and
  cuts repeated runtime construction to about `17 ms`, roughly a `2.4x`
  improvement for that phase.
