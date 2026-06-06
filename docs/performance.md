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
* isolated warm-runtime `double -> double` and `long -> long` scalar calls that
  exercise the general primitive scalar fast path
* an isolated warm-runtime zero-argument `int` return call that uses the scalar
  result fast path
* isolated warm-runtime `void` calls that use the exact-signature native void
  fast paths
* an isolated warm-runtime 4 KiB `byte[]` return call that exercises generic
  payload serialization and deserialization
* an isolated warm-runtime object return call that exercises generic object
  member serialization and deserialization
* an isolated warm-runtime `List<int>` return call that exercises generic
  collection serialization and deserialization
* isolated warm-runtime large `double[]` and `long[]` return calls and a large
  `double[]` argument call that exercise the generalized blittable bulk codec
* isolated warm-runtime typed and raw host-callback calls
* startup medians without the module cache, with a cold module cache, and with a
  warm module cache
* repeated runtime startup from one warm host, with and without the runtime
  memory snapshot, and with the reuse instance pool
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
* Instance pool: opt-in through `IsolatedRuntimeHostOptions.UseInstancePool`
  (requires the runtime memory snapshot). Instead of instantiating a fresh
  Wasmtime instance per runtime, started instances are parked on dispose and
  reused. A reused instance is reset to the clean post-startup state by restoring
  the runtime memory snapshot, which resets every runtime root so the previous
  tenant's managed state becomes unreachable and is zero-overwritten on the next
  allocation. This skips the dominant per-runtime cost, which is Wasmtime
  instantiation (about `3 ms`), not the snapshot copy. Instances that grow their
  linear memory beyond the snapshot size are dropped rather than pooled. It is a
  speed optimization for trusted guest code: the reset does not scrub every
  orphaned page, so it should not be used as an isolation boundary against
  hostile guests that can perform raw memory reads. Covered by a sequential-reuse
  isolation test that asserts static guest state does not leak across tenants.
* Native `int -> int` invoke path: `IsolatedMethod.Invoke<int, int>` bypasses
  MessagePack and object-graph serialization.
* Packed scalar return: `dotnetisolator_invoke_i32_i32_packed` returns the
  primitive result and error state in one `i64`, avoiding guest-memory
  result-frame traffic for successful scalar calls.
* Native zero-argument `int` return path: `IsolatedMethod.Invoke<int>` bypasses
  result serialization for exact `() -> int` methods while keeping native
  signature validation.
* General primitive scalar path: a single `dotnetisolator_invoke_scalar` export
  handles any `(T0) -> TRes` or `() -> TRes` call where the argument and result
  are blittable primitives (`bool`, `sbyte`, `byte`, `short`, `ushort`, `char`,
  `int`, `uint`, `long`, `ulong`, `float`, `double`). The argument is delivered
  bit-packed in a register and the result is returned bit-packed, so no guest
  memory is touched for the value on the success path. The guest validates the
  method signature against the requested element kinds, so a mismatched request
  fails instead of reinterpreting bits. This extends the `int`-only scalar fast
  path to every primitive combination (the dedicated `int -> int` packed path is
  retained as the fastest case).
* Native zero-argument `byte[]` return path: `IsolatedMethod.Invoke<byte[]>`
  bypasses object-graph serialization for exact `() -> byte[]` methods. The
  guest array is pinned only while the host copies the bytes into a new host
  array, then the guest handle is released.
* Native zero-argument blittable-array return path: `IsolatedMethod.Invoke<T[]>`
  for any blittable primitive element type (`bool`, `sbyte`, `byte`, `short`,
  `ushort`, `char`, `int`, `uint`, `long`, `ulong`, `float`, `double`) bypasses
  object-graph serialization for exact `() -> T[]` methods. The guest returns a
  pointer to the array's pinned element storage and its element count and
  element size; the host copies the raw element bytes once into a fresh host
  array and validates the element size against the expected `T`. This
  generalizes the `() -> byte[]` fast path and removes the guest-side
  serializer invocation, the guest `MemoryStream`/`ToArray`, and the redundant
  large-buffer copies for the common array-return case.
* Native plain-object serializer: the generic invoke path's result serialization
  first tries a native field-walking serializer for plain objects whose members
  are non-`char` primitives and/or `string`s (the common DTO/record shape). It
  collects the type's serializable members in the same order as the managed
  `GetSerializableMembers` (get/set properties plus non-backing instance fields,
  sorted by name) and writes the positional object-graph format directly with
  `mono_field_get_value`, skipping the managed serialize round-trip. Strings are
  encoded exactly as `BinaryWriter` does (a 7-bit-encoded UTF-8 length followed
  by UTF-8 bytes, with .NET's replacement behavior for lone surrogates), so the
  wire format stays identical. Anything it cannot prove safe -- `char` members
  (also encoded as text), collections, enums, nesting, `[NonSerialized]` or
  otherwise attributed members -- falls back to the managed serializer. A
  pure-primitive object return dropped from about `20 us` to about `2.5 us`, and
  a string-bearing object (an `int`/`string`/`int`/`bool`/`long` payload) from
  about `20 us` to about `2.7 us`, both roughly `7-8x`.
* Native blittable-list return path: `IsolatedMethod.Invoke<List<T>>` for any
  blittable primitive element type returns a pointer to the list's backing array
  (`_items`) for its live element count (`_size`), read through mono metadata,
  and the host copies the elements directly into a new `List<T>` backing store.
  This bypasses the managed serialize/deserialize round-trip for the common
  `() -> List<T>` shape, reusing the array fast path's result transport without
  staging through a temporary host array. `List<T>`'s field layout has been
  stable for many years; if the expected fields are absent the call fails rather
  than guessing.
* Native blittable-array argument path: `IsolatedMethod.Invoke<T[], TRes>` for
  any blittable primitive element type sends the raw element bytes into a guest
  buffer once, and the guest materializes the managed array directly with
  `mono_array_new` plus a single `memcpy` instead of deserializing it through the
  object-graph path. The return value still flows through the normal result
  serialization, so any return type is supported; the host deserializes that
  result directly from guest memory and releases the guest handle even if
  deserialization fails. A `null` array falls back to the managed path so `null`
  is preserved. This is the argument-direction counterpart of the native
  array-return path.
* Native void invoke paths: `IsolatedMethod.InvokeVoid` and
  `IsolatedMethod.InvokeVoid<int>` bypass object-graph serialization for exact
  `() -> void` and `int -> void` methods while keeping native signature
  validation.
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
* Direct collection serialization: collection and dictionary payloads are
  written by direct enumeration instead of staging through `Cast().ToArray()`.
  Known-count collections write the count up front; other enumerable fallbacks
  reserve the count field in the seekable serialization stream and patch it
  after enumeration. Deserializing non-array primitive collections now fills the
  destination `List<T>` directly instead of first building an array.
* Bulk primitive collection codec: arrays and lists whose element type is a
  blittable primitive (`bool`, `sbyte`, `byte`, `short`, `ushort`, `char`,
  `int`, `uint`, `long`, `ulong`, `float`, `double`) use a compact
  marker-plus-kind-plus-bytes format instead of writing each element through the
  recursive nullable value path. The codec is symmetric and bidirectional: it is
  compiled into both the host and the guest, so it accelerates collection
  arguments and return values in both directions. A whole array is now read and
  written as one contiguous little-endian memory block instead of one virtual
  reader/writer call per element, which is the dominant cost for large payloads
  inside the interpreted guest runtime. The host still receives new arrays/lists
  that are copied out of guest memory, and a big-endian host falls back to the
  per-element path. This generalizes the earlier `int`-only codec.
* Compact object-member codec: object payloads write members positionally in the
  cached serializer order instead of writing each member name and
  assembly-qualified member type. Deserialization uses the expected member
  types and still creates a fresh object graph.
* Cached serializer type-shape metadata: dictionary and collection shape checks
  now cache positive and negative results per `Type`. This avoids repeated
  generic-interface scans on recursive object-graph reads and writes without
  caching object instances or weakening the copy boundary.
* Unlocked warm module-cache hits: warm precompiled-module cache hits now
  deserialize without a process-wide cache lock, while cache misses still
  synchronize per cache file. This improves parallel host construction without
  sharing stores, instances, callbacks, linker state, or guest memory.
* File-backed assembly byte cache: the built-in BCL loader and directory loader
  cache assembly file bytes by path, length, and last-write timestamp. Each
  runtime still receives a fresh copy into its own guest memory, but repeated
  runtime starts no longer reread the same host files from disk.
* Host callback dispatch cleanup: callback registration now caches parameter and
  return type metadata, raw `byte[]` callbacks use the deserialized host-owned
  argument arrays directly instead of cloning them again, and guest callback
  result buffers are freed after the guest copies or deserializes them. The
  guest-side callback envelope now rents the serialized-argument array and
  carries the logical argument count separately, so `ArrayPool<T>` buffers are
  safe even when the rented array is larger than the callback arity.
* Scalar callback fast path: `DotNetIsolatorHost.Invoke<TRes>(name, arg)` for a
  callback whose single argument (if any) and result are blittable primitives
  travels bit-packed through a small invocation struct in guest memory via a
  dedicated `call_host_scalar` host import, skipping the MessagePack envelope and
  the object-graph (de)serialization on both sides. The host caches a typed
  scalar invoker at callback registration, so the scalar dispatch path does not
  allocate a reflection argument array. Other callback shapes keep using the
  general MessagePack path. It reduced the typed `(int) -> int` callback from
  about `9.5 us` to about `1.9 us` (about `5x`).
* Copy-free generic deserialization: the host now deserializes a generic call's
  result, a blittable-array argument call's generic result, a guest callback's
  invocation envelope, and non-raw guest callback results directly from guest or
  host-owned unmanaged memory (via `UnmanagedMemoryStream` and an
  `UnmanagedMemoryManager`) instead of first copying the payload into a managed
  array. The object-graph serializer also reuses one
  `MemoryStream`/`BinaryWriter` per thread instead of allocating and regrowing a
  buffer on every serialize. These remove per-call allocations on the generic
  fallback paths (the native fast paths already avoid them for the common
  shapes). Eliminating the envelope copy reduced the raw `byte[65536]` host
  callback from about `93 us` to about `73 us` (about `15-20%`).
* Batched primitive scalar invocation: `IsolatedMethod.InvokeBatch<T0, TRes>`
  runs a primitive `(T0) -> TRes` method once per argument in a single
  host/guest boundary crossing, reading the arguments from one contiguous guest
  buffer and writing the results to another. It amortizes the per-call boundary
  and host-side marshaling across the batch, so it suits tight loops of
  independent primitive calls. A batched `int -> int` call measured about
  `97 ns` versus about `190 ns` for the same call made one at a time (about
  `2x`); the remaining cost is the per-element guest `mono_runtime_invoke`, which
  batching cannot remove.

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
* Cached default WASI configuration per host: a prototype stored the default
  `WasiConfiguration` instead of rebuilding it in `CreateStore`. Repeated
  warm-host runtime startup did not show a meaningful improvement: about
  `35.43 ms` before and `35.34 ms` after, with runtime-snapshot startup at about
  `2.92 ms` before and `2.86 ms` after.
* Process-wide shared `Engine`/`Module` cache as a default: not accepted because
  it changes `IsolatedRuntimeHost.Dispose` resource-release semantics and can
  retain a compiled module for the process lifetime. This may be revisited as an
  explicit opt-in cache with clear lifetime ownership, but it should not be
  introduced as an invisible default optimization.
* WASI Preview 2 shim import-plan caching by `Module`: not accepted because
  hosts currently load distinct `Module` instances, so a per-module cache would
  not hit on repeated host construction. It should only be reconsidered together
  with an explicit shared-module lifetime design.

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
Direct host Increment: total 86.938 ms, mean 1.739 ns
Isolated warm-runtime Increment: total 171.357 ms, mean 171.357 ns
Isolated warm-runtime public Invoke Increment: total 250.893 ms, mean 250.893 ns
Isolated/direct mean ratio: 99x

Additional warm-call overhead
Isolated warm-runtime zero-arg int return: total 4.204 ms, mean 210.200 ns
Isolated warm-runtime void call: total 3.608 ms, mean 180.410 ns
Isolated warm-runtime int void call: total 146.819 ms, mean 146.819 ns
Isolated warm-runtime generic byte[4096] return: total 0.759 ms, mean 1.518 us
Isolated warm-runtime generic object return: total 10.072 ms, mean 20.144 us
Isolated warm-runtime generic List<int>[1024] return: total 10.775 ms, mean 21.550 us
Isolated warm-runtime typed host callback: total 6,216.301 ms, mean 6.216 us
Isolated warm-runtime raw byte[65536] host callback: total 34.050 ms, mean 68.100 us

Startup medians
No module cache: host 289.494 ms, runtime 35.167 ms, object 847.600 us, method 108.800 us, first call 50.400 us
Cold module cache: host 336.418 ms, runtime 35.285 ms, object 850.100 us, method 99.500 us, first call 51.400 us
Warm module cache: host 1.427 ms, runtime 40.519 ms, object 801.600 us, method 94.400 us, first call 55.500 us
Warm host: runtime 36.264 ms, object 816.400 us, method 99.100 us, first call 50.200 us
Runtime memory snapshot preload: 73.612 ms
Warm runtime memory snapshot: runtime 2.873 ms, object 908.200 us, method 115.700 us, first call 65.700 us

Concurrent host construction
Warm module cache parallel host construction (16 hosts): total 13.440 ms, mean 839.994 us
```

Interpretation:

* A representative warm isolated scalar call is around `171 ns` on this machine,
  versus about `1.7 ns` for the direct host call. That is roughly `99x` slower
  for this tiny method.
* Before the scalar fast path, the same benchmark measured about `37 us` per
  isolated call and about `21,000x` direct-call overhead on this machine. The
  fast path, shadow-stack frame optimization, and packed scalar return cut the
  measured isolated call cost by roughly `214x`.
* The general primitive scalar path extended that win to every primitive
  combination. Before it, a `double -> double` call fell through to the generic
  object-graph path at about `37.3 us` and a `long -> long` call at about
  `38.1 us`. With the fast path both drop to about `308-311 ns` (about `120x`
  and `124x`), close to the dedicated `int -> int` packed path (about
  `240-280 ns` in the same runs). The same export also covers non-`int`
  `() -> TRes` returns such as `() -> double` and `() -> long`.
* The zero-argument `int` return path now bypasses result serialization for
  exact `() -> int` methods. Before that fast path, nearby samples measured
  about `2.3-2.5 us`; the representative fast-path sample above is about
  `210 ns`.
* The native void paths reduced close A/B samples for exact `() -> void` calls
  from about `218 ns` at `HEAD` to about `139 ns`, and exact `int -> void`
  calls from about `25.7 us` to about `150 ns`.
* The public `IsolatedObject.Invoke` path remains slower than reusing an
  `IsolatedMethod`, but the object-local method cache reduced close A/B samples
  from about `306 ns` to about `267-277 ns` for repeated public scalar calls.
* Avoiding the duplicate deserialization copy reduced close A/B samples for the
  4 KiB generic byte-array return from about `14.65 us` to about `13.33 us`.
* The native `() -> byte[]` fast path then reduced close A/B samples for the
  same 4 KiB byte-array return from about `13.7 us` at `HEAD` to about
  `1.4-1.8 us`; the representative run above is about `1.5 us`.
* The compact object-member codec reduced close A/B samples for the generic
  object return from about `61.7 us` at `HEAD` to about `20.9-21.4 us`; the
  representative run above is about `20.1 us`.
* Direct collection serialization reduced close A/B samples for a
  `List<int>[1024]` return from about `738 us` to about `649-652 us`.
* The bulk primitive collection codec then reduced close A/B samples for the
  same `List<int>[1024]` return from about `653.8 us` at `HEAD` to about
  `28.8-29.2 us`; the representative run above is about `21.6 us`.
* Caching serializer type-shape metadata then reduced a clean A/B sample for the
  same `List<int>[1024]` return from about `20.3 us` at `HEAD` to about
  `8.1 us`, and the generic object return from about `11.0 us` to about
  `8.9 us`, using a payload-heavy close comparison.
* The native blittable-list return path then took the `List<int>[1024]` return
  from the managed bulk-codec path at about `16 us` to about `2.7-2.9 us` (about
  `6x`), close to the native array return, by reading the list's backing array
  directly and skipping the managed serialize/deserialize round-trip. It applies
  to `List<T>` of any blittable primitive element type. General object/record
  returns still use the managed object-graph path.
* Generalizing the bulk primitive collection codec to every blittable element
  type removed the per-element catastrophe for wide arrays. With a `32768`
  element payload and `--payload-iterations 30`, a `double[]` return dropped from
  about `31.911 ms` to about `218.070 us` (about `146x`), a `long[]` return from
  about `30.018 ms` to about `216.013 us` (about `139x`), and a `double[]`
  argument from about `33.248 ms` to about `548.793 us` (about `61x`). The ratio
  grows with array length because the old path cost scales with element count
  (about `0.97 us` per `double` element) while the new path scales with bytes
  copied.
* The native blittable-array return path then removed the remaining guest-side
  serialization for the `() -> T[]` case. On the same `32768`-element payload it
  reduced the `double[]` return from the codec's about `218.070 us` to about
  `41.117 us` and the `long[]` return from about `216.013 us` to about
  `42.393 us`. Measured end to end against the original per-element baseline this
  is about `776x` for the `double[]` return and about `708x` for the `long[]`
  return. Because the old path is linear in element count, the advantage keeps
  growing with size: a `131072`-element `double[]` return measured about
  `118.840 us`, versus an extrapolated per-element baseline of about `127.6 ms`,
  which is about `1074x`. `List<T>`/nested-array payloads still use the
  generalized managed codec, so they keep their large win too.
* The native blittable-array argument path then took the argument direction
  below the managed codec. On the same `32768`-element payload a `double[]`
  argument dropped from the codec's about `456 us` to about `230 us`, which is
  about `145x` versus the original per-element baseline of about `33.248 ms`. The
  argument direction stays somewhat above the matching return because the guest
  must allocate and fill a fresh managed array, whereas the return path hands
  back an array the guest already owns.
* Removing the duplicate host-side raw callback argument copy reduced a clean
  A/B sample for a raw 64 KiB host callback from about `88.4 us` at `HEAD` to
  about `70.7 us`. The callback result buffer is now also released by the guest
  after copying/deserialization.
* The warm module cache cuts host construction from about `289 ms` to about
  `1.4 ms`, roughly a `203x` improvement for that phase in this run.
* Unlocking warm module-cache hits reduced close A/B samples for 16 parallel
  warm-cache host constructions from about `21.8 ms` with the global lock to
  about `13.2 ms`; the representative optimized run above is about `13.4 ms`.
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
* The instance pool then reuses started instances and only resets them from the
  snapshot, which avoids the per-runtime Wasmtime instantiation (the dominant
  cost) and the cold-memory page faults of restoring into a fresh instance.
  Repeated runtime construction dropped from about `3.2 ms` with the snapshot to
  about `94-139 us` with the pool, roughly `25-35x` beyond the snapshot and about
  `350-430x` versus the `40-49 ms` warm-host construction without a snapshot. A
  startup breakdown showed the snapshot path spends about `3 ms` in
  `Linker.Instantiate` and only about `0.7-1 ms` restoring the `~4.4 MB` of
  changed pages; pooling removes the instantiation entirely, and the reset copy
  runs from cache at roughly `40-50 GB/s` on an already-resident instance.
