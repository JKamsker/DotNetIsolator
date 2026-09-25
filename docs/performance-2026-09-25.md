# Fast-path improvements: September 25, 2026

All eight proposed areas were implemented. This report compares the original
`ce706a7` implementation with the final working tree on the same machine. It does
not compare Linux measurements with the older Windows numbers in `performance.md`.

## Environment and method

* AMD Ryzen 9 3900X, Ubuntu 24.04.3 LTS, Linux x64.
* .NET SDK 10.0.400, host and WASI runtime 10.0.11, Wasmtime 44.0.0.
* WASI SDK 25.0 / Clang 19.1.5; `wasi-experimental` workload installed automatically.
* Release builds; processes pinned to logical CPU 6; `DOTNET_TieredCompilation=0`
  for both versions. Startup, method lookup, and initial callback resolution are
  excluded from the measurements.
* Three processes per version, five timed samples per workload after warmup.
  The table reports the **median of the three process medians**. The machine also
  runs other workloads: pinning limits migration, but does not eliminate contention.
  Raw files preserve every sample range; these are microbenchmarks, not confidence
  intervals or application-level throughput predictions.
* The new benchmark harness and target methods were compiled against the baseline
  before implementation. Its output directory was copied before changing the
  library. The baseline's `callback typed` target uses the old `Invoke<int>(name,
  value)` API; the improved target uses `Invoke<int, int>(name, value)`.
* Scalar workloads perform 200,000 calls/sample, arities 2–4 perform 20,000,
  callbacks 50,000, collections 10,000, nested DTOs 500, and batches 1,000 batches
  of 1,024 calls. Warmup performs up to 2,000 calls. Results feed a checksum sink.
* Allocation figures count **host-thread managed bytes only**. They exclude
  allocations in the guest heap and native Wasmtime/Mono allocations.

## Results

| Workload | Before (ns/op) | After (ns/op) | Before / after | Host B/op, before → after |
|---|---:|---:|---:|---:|
| `int -> int` | 273.3 | 278.1 | 0.98× | 0.0 → 0.0 |
| `double -> double` | 404.2 | 350.6 | 1.15× | 0.0 → 0.0 |
| `long -> long` | 396.6 | 346.9 | 1.14× | 0.0 → 0.0 |
| `scalar arity 2` | 32,587.3 | 341.1 | 95.54× | 2,408.1 → 0.0 |
| `scalar arity 3` | 48,101.8 | 381.0 | 126.25× | 3,544.5 → 0.0 |
| `scalar arity 4` | 61,712.3 | 408.3 | 151.14× | 4,656.9 → 0.0 |
| `callback params` | 2,512.1 | 1,807.5 | 1.39× | 112.0 → 0.0 |
| `callback typed` | 2,495.7 | 1,366.2 | 1.83× | 112.0 → 0.0 |
| `double[32] -> int` | 2,943.4 | 873.7 | 3.37× | 168.0 → 0.0 |
| `double[32] -> void` | 792.2 | 816.5 | 0.97× | 0.0 → 0.0 |
| `double[32] -> double[]` | 6,629.1 | 1,177.6 | 5.63× | 424.0 → 280.0 |
| `List<double>[32] -> int` | 40,051.0 | 1,188.2 | 33.71× | 2,616.2 → 0.0 |
| `nested DTO roundtrip (32 KiB)` | 531,359.8 | 503,379.8 | 1.06× | 69,753.8 → 36,776.9 |
| `batch int[1024] (per element)` | 187.1 | 40.5 | 4.62× | 4.0 → 4.0 |

The dedicated packed `int -> int` path is unchanged and serves as a control.
General scalar multi-value returns take about 13% less time,
but still cost more than the dedicated int path. Arity 2–4 calls eliminate the
serializer's allocations and improve by roughly 96–151×.

The new typed callback combines numeric host dispatch and typed guest packing.
Its three process medians were 2.027, 1.366 and 1.325 µs; all are included in the
reported median. Numeric dispatch also helps existing params-based callers, but
they still allocate/box arguments in the **guest** even though host allocations
are zero. Batch performance is per element including argument/result copying;
its improved process medians were 40.3, 40.5 and 52.2 ns.

The nested workload contains eight DTOs plus a 4,096-element `double[]`, ensuring
it uses the managed serializer rather than the native flat-object serializer.
Borrowing the buffer cuts host allocations by **47%**. Its small latency gain
(about 5%) is much less decisive than the allocation reduction: the measured
ranges overlap, and serialization/reflection still dominates this workload.

`T[] -> void` did **not** have a substantial result-serialization cost in the
baseline: a null Mono result already skipped serialization. Its explicit void
mode now checks the return signature, with no measured speedup (about 3% slower
in this comparison). We retain it for exact signature validation and symmetry.
The int control differs by about 2%, within ordinary run variation.

Raw output:

* Baseline: [run 1](benchmarks/2026-09-25/baseline-1.txt),
  [run 2](benchmarks/2026-09-25/baseline-2.txt),
  [run 3](benchmarks/2026-09-25/baseline-3.txt).
* Improved: [run 1](benchmarks/2026-09-25/improved-1.txt),
  [run 2](benchmarks/2026-09-25/improved-2.txt),
  [run 3](benchmarks/2026-09-25/improved-3.txt).

## Implementation and lifetime guarantees

1. **Multi-value returns.** LLVM Wasm assembly wrappers bridge the existing C
   out-pointer ABI to `(i64, i32)` returns. Each wrapper owns a 16-byte native
   stack frame, restores the stack pointer, and returns result bits plus the
   error pointer. The host binds tuple delegates and touches guest memory only
   for errors. No experimental C ABI or post-link binary rewriting is needed.
2. **Callback IDs.** Each runtime registers stable positive IDs. The guest caches
   successful name resolutions, encoding UTF-8 only on a cache miss. Host scalar
   dispatch indexes its registration table. A missing name is not cached, so
   later registration works; runtime snapshots reset guest caches.
3. **Typed callbacks.** `Invoke<TArg, TResult>(string, TArg)` uses a generic scalar
   codec without an object-valued API. Complex types retain the existing general
   callback path. Requested scalar argument/result kinds are still checked, and
   host exception details remain hidden.
4. **Scalar arities 2–4.** Arguments use one `i64` slot each plus packed kind tags;
   scalar and void results share the multi-value transport. Native code checks
   arity and every exact type on each call, including rejecting by-reference
   types. Unsupported shapes retain serialization. The rejected experiment of
   hoisting signatures out of the native call was not retried.
5. **Array result transport.** Primitive-array arguments support native scalar,
   void and primitive-array results, including differing argument/result element
   types. Array results are pinned until the host copies them and releases the
   handle. Empty and null arrays remain distinct. Generic results still work.
6. **Managed batches.** Native code validates the signature once and invokes a
   managed dispatcher once per batch. The dispatcher caches a typed delegate
   loop per method, and bound delegates per target via `ConditionalWeakTable`.
   Static calls, ordered state changes, and stopping at the first exception are
   covered. The cache does not extend a released target object's lifetime.
7. **Borrowed serializer buffers.** Host invocation arguments and copied objects
   use a disposable buffer lease with a logical length. Generic guest results
   stay leased and pinned until the host releases their GC handle, including on
   deserialization failure. A borrowed writer is unavailable to nested calls;
   one idle writer is retained per thread. Native lease records are recycled.
   APIs requiring owned byte arrays, including generic callback envelopes,
   continue to make an ownership copy.
8. **List arguments.** Native code builds a fresh `List<T>` with a bulk-copied
   backing array and live count, checking the core-library type and expected
   fields. Mutations cannot affect the host list. Null lists and list arguments
   to interface/object parameters keep the serializer fallback. Unsupported
   result types also keep the generic result serializer.

## Reproduce and validate

Dependencies installed for this run:

```bash
dotnet workload install wasi-experimental --skip-manifest-update
mkdir -p "$HOME/.wasi-sdk"
curl -fL https://github.com/WebAssembly/wasi-sdk/releases/download/wasi-sdk-25/wasi-sdk-25.0-x86_64-linux.tar.gz -o /tmp/wasi-sdk-25.tar.gz
tar xf /tmp/wasi-sdk-25.tar.gz -C "$HOME/.wasi-sdk"
export WASI_SDK_PATH="$HOME/.wasi-sdk/wasi-sdk-25.0-x86_64-linux"
```

Build, run the tests, and benchmark (choose an allowed CPU on your machine):

```bash
dotnet build DotNetIsolator.sln -c Release
dotnet test test/DotNetIsolator.Test -c Release --no-build --no-restore
taskset -c 6 env DOTNET_TieredCompilation=0 \
  dotnet sample/PerformanceSample/bin/Release/net10.0/PerformanceSample.dll --fast-paths
```

The full Release solution builds successfully. The original 139 tests and 14
new tests pass: **153 passed, zero failed, zero skipped**. New coverage includes
all twelve callback scalar kinds, mixed scalar arities and void returns, NaN bit
preservation, collection copying/null/empty/mismatched types/interface fallbacks,
static and stateful batches, exceptions, reentrant scalar and serialization
calls, deserialization failure cleanup, and callback isolation across runtime
instances and pool reuse. See the [test output](benchmarks/2026-09-25/tests.txt).
The build still reports the pre-existing NuGet advisories for MessagePack 3.1.6;
package versions were held constant for the comparison.
