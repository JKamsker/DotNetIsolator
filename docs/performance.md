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
* an isolated warm-runtime public `IsolatedObject.Invoke` call to the same
  method, including the public method lookup path.
* an isolated warm-runtime zero-argument `int` return call that uses the scalar
  result fast path
* an isolated warm-runtime 4 KiB `byte[]` return call that exercises generic
  payload serialization and deserialization
* an isolated warm-runtime object return call that exercises generic object
  member serialization and deserialization
* an isolated warm-runtime `List<int>` return call that exercises generic
  collection serialization and deserialization
* startup medians without the module cache, with a cold module cache, and with a
  warm module cache
* repeated runtime startup from one warm host, with and without the runtime
  memory snapshot
* parallel warm module-cache host construction

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
instantiate the bundled module once, run `_start`, compare the initialized
linear memory with a fresh pre-start instance, and store only the WebAssembly
pages that changed during startup. Later runtimes still get their own fresh
Wasmtime store, instance, and guest heap; the snapshot restore only copies those
pre-user-code changed pages into the new instance before user code runs.

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
copies linear memory pages, not arbitrary tables or globals. It is covered by
startup and host-callback tests for the current generated .NET 10 module.

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
  `IsolatedRuntimeHostOptions.UseRuntimeMemorySnapshot`. It stores changed
  initialized linear-memory pages before user code runs and restores those pages
  into later runtimes from the same host. It improves repeated runtime
  construction while preserving per-runtime guest memory ownership.
* Native `int -> int` invoke path: `IsolatedMethod.Invoke<int, int>` bypasses
  MessagePack and object-graph serialization.
* Packed scalar return: `dotnetisolator_invoke_i32_i32_packed` returns the
  primitive result and error state in one `i64`, avoiding guest-memory
  result-frame traffic for successful scalar calls.
* Native zero-argument `int` return path: `IsolatedMethod.Invoke<int>` bypasses
  result serialization for exact `() -> int` methods while keeping native
  signature validation.
* Arity-aware public method lookup cache: `IsolatedObject.FindMethod` now passes
  the requested argument count into the runtime lookup, caches successful
  lookups on the object, and the runtime cache key includes the declaring type
  name. This avoids repeated full runtime-cache hashing on public
  `IsolatedObject.Invoke` calls and prevents nested-type method cache
  collisions.
* Shadow-stack generic invocation frame: the generic invocation path now places
  its transient `Invocation` struct on the per-runtime shadow stack and skips the
  empty guest args-buffer allocation for zero-argument calls. This preserves
  per-call guest memory isolation while reducing guest `malloc`/`free` traffic
  for non-scalar calls.
* Array-backed object-graph deserialization streams: when deserializing from an
  array-backed `ReadOnlyMemory<byte>`, the object-graph serializer now reads
  directly from that array instead of copying it into a second array first. It
  still copies non-array-backed memory before parsing.
* Direct collection serialization: collection payloads with a known `Count` are
  written by direct enumeration instead of staging through `Cast().ToArray()`,
  and deserializing non-array collections now fills the destination `List<T>`
  directly instead of first building an array.
* Bulk primitive collection codec: exact `int[]` and `List<int>` payloads use a
  compact count-plus-bytes format instead of writing each element through the
  recursive nullable value path. The host still receives new arrays/lists that
  are copied out of guest memory.
* Unlocked warm module-cache hits: warm precompiled-module cache hits now
  deserialize without a process-wide cache lock, while cache misses still
  synchronize per cache file. This improves parallel host construction without
  sharing stores, instances, callbacks, linker state, or guest memory.
* File-backed assembly byte cache: the built-in BCL loader and directory loader
  cache assembly file bytes by path, length, and last-write timestamp. Each
  runtime still receives a fresh copy into its own guest memory, but repeated
  runtime starts no longer reread the same host files from disk.

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
* Removing generic boxing from the existing `int -> int` public fast-path shim:
  a prototype replaced `(object)` casts in `IsolatedMethod.Invoke<T0, TRes>` with
  `Unsafe.As` after exact type checks. Close A/B samples showed no meaningful
  improvement: the boxed baseline measured about `155 ns`, while the prototype
  measured about `157 ns` in nearby runs. The JIT already appears to make this
  path cheap enough that the extra unsafe code is not justified.
* Smaller default WASM initial heap sizes: 64 MiB and 96 MiB prototypes both
  passed the test suite, but neither produced a clear default performance win.
  The 64 MiB build lowered snapshot preload samples to about `62-65 ms` versus
  about `74 ms`, but worsened nearby generic payload and warm-runtime samples.
  The 96 MiB build was mixed as well: preload was about `71 ms`, snapshot
  runtime stayed about `3 ms`, and warm generic/object timings varied around the
  current 128 MiB default. Keep the 128 MiB default unless a workload-specific
  tuning option is added and benchmarked separately.
* Cached object-member lookup dictionary during deserialization: a prototype
  changed `ObjectGraphTypes` to cache both the ordered member list and a
  name-to-member dictionary. A controlled A/B using the generic object-return
  benchmark measured about `46.0 us` with the existing per-object dictionary
  build and about `47.6 us` with the cached dictionary. The cache adds
  complexity and memory retention without improving this workload.

## Measured Results

Measured on June 5, 2026:

* .NET SDK 10.0.204, runtime .NET 10.0.8
* Windows 10.0.26200, `win-x64`
* WASI SDK 25.0
* Wasmtime .NET package 44.0.0

Representative run with
`--host-iterations 50000000 --isolated-iterations 1000000 --zero-arg-iterations 20000 --payload-iterations 500 --startup-iterations 3 --concurrent-hosts 16`:

```text
Steady-state call overhead
Direct host Increment: total 88.127 ms, mean 1.763 ns
Isolated warm-runtime Increment: total 173.202 ms, mean 173.202 ns
Isolated warm-runtime public Invoke Increment: total 268.454 ms, mean 268.454 ns
Isolated/direct mean ratio: 98x

Additional warm-call overhead
Isolated warm-runtime zero-arg int return: total 4.376 ms, mean 218.780 ns
Isolated warm-runtime generic byte[4096] return: total 6.931 ms, mean 13.862 us
Isolated warm-runtime generic object return: total 35.502 ms, mean 71.004 us
Isolated warm-runtime generic List<int>[1024] return: total 17.141 ms, mean 34.282 us

Startup medians
No module cache: host 303.684 ms, runtime 35.673 ms, object 551.000 us, method 95.100 us, first call 55.400 us
Cold module cache: host 328.154 ms, runtime 39.470 ms, object 580.900 us, method 115.200 us, first call 83.900 us
Warm module cache: host 1.603 ms, runtime 54.384 ms, object 596.500 us, method 125.300 us, first call 71.200 us
Warm host: runtime 39.908 ms, object 657.000 us, method 117.700 us, first call 57.700 us
Runtime memory snapshot preload: 82.370 ms
Warm runtime memory snapshot: runtime 3.054 ms, object 627.800 us, method 100.900 us, first call 58.100 us

Concurrent host construction
Warm module cache parallel host construction (16 hosts): total 16.414 ms, mean 1.026 ms
```

Interpretation:

* A representative warm isolated scalar call is around `173 ns` on this machine,
  versus about `1.8 ns` for the direct host call. That is roughly `98x` slower
  for this tiny method.
* Before the scalar fast path, the same benchmark measured about `37 us` per
  isolated call and about `21,000x` direct-call overhead on this machine. The
  fast path, shadow-stack frame optimization, and packed scalar return cut the
  measured isolated call cost by roughly `214x`.
* The zero-argument `int` return path now bypasses result serialization for
  exact `() -> int` methods. Before that fast path, nearby samples measured
  about `2.3-2.5 us`; the representative fast-path sample above is about
  `220 ns`.
* The public `IsolatedObject.Invoke` path remains slower than reusing an
  `IsolatedMethod`, but the object-local method cache reduced close A/B samples
  from about `306 ns` to about `267-277 ns` for repeated public scalar calls.
* Avoiding the duplicate deserialization copy reduced close A/B samples for the
  4 KiB generic byte-array return from about `14.65 us` to about `13.33 us`.
* Direct collection serialization reduced close A/B samples for a
  `List<int>[1024]` return from about `738 us` to about `649-652 us`.
* The bulk primitive collection codec then reduced close A/B samples for the
  same `List<int>[1024]` return from about `653.8 us` at `HEAD` to about
  `28.8-29.2 us`; the representative run above is about `34.3 us`.
* The warm module cache cuts host construction from about `311 ms` to about
  `1.6 ms`, roughly a `198x` improvement for that phase in this run.
* Unlocking warm module-cache hits reduced close A/B samples for 16 parallel
  warm-cache host constructions from about `21.8 ms` with the global lock to
  about `13.2 ms`; the representative optimized run above is about `15.5 ms`.
* The first cache miss is slower than no cache because it compiles and writes the
  serialized module. The cache is intended for repeated host construction.
* Runtime startup on a warm host is still about `40-60 ms` because the .NET WASI
  runtime is still instantiated and started.
* Caching host assembly file bytes reduced close A/B samples for repeated
  warm-host runtime startup from about `60.2 ms` at `HEAD` to about
  `42.6-44.3 ms` with the cache, using
  `--host-iterations 10000000 --isolated-iterations 200000 --zero-arg-iterations 5000 --payload-iterations 100 --startup-iterations 9 --concurrent-hosts 8`.
* The runtime memory snapshot moves one-time startup work into a preload step and
  cuts repeated runtime construction to about `3-4 ms`, roughly a `13x`
  improvement for that phase in this run.
