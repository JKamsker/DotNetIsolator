# Continued performance iterations — September 25, 2026

This comparison starts at `2ae4b6f` (the preceding callback follow-up), not the
original upstream implementation. It measures the implementation committed with
this report. Earlier comparisons remain in the [callback report](performance-callbacks-2026-09-25.md)
and [scalar/batch report](performance-2026-09-25.md).

## Retained changes

* Numeric callback dispatch now covers two through four primitive arguments and
  primitive results, with typed overloads that avoid guest params arrays and
  boxing. Void/discard calls support zero through four primitive arguments.
  Registered delegate signatures, multicast invocation lists and closed-static
  bindings are preserved. Unsupported shapes use serialization.
* Generic callback envelopes reuse a writer while keeping it unavailable during
  nested calls. Host generic callback results hold a serializer buffer lease
  through the synchronous guest copy. Null generic callback arguments are handled
  correctly. Raw callback arrays remain independently owned copies.
* Collection deserialization preallocates up to 4,096 slots using cached typed
  constructors. Dictionaries enumerate typed entries. String collections bypass
  repeated per-element type inspection while retaining their wire format,
  null/empty behavior, Unicode replacement and depth checks.
* Native string result encoding uses the existing presence/7-bit-length/UTF-8
  format. Single non-null string arguments use the existing bulk argument frame;
  scalar, void and primitive-array results also avoid the generic dispatcher.
  Null strings, object/interface parameters and task-like methods retain the
  appropriate fallback. Guest strings remain rooted through allocations and
  nested calls.
* Assembly lookup consults Mono's already-loaded assemblies before asking the
  host. A recursion guard allows Mono's remaining search hooks to inspect the
  default load context; no separate name cache is added. Supplied transfer bytes
  and temporary image references are released after ownership passes to Mono.
  The ownership/search-hook behavior was checked against the pinned
  [.NET 10.0.11 Mono source](https://github.com/dotnet/runtime/blob/v10.0.11/src/mono/mono/metadata/assembly.c).
* `InvokeBatch<TArg, TResult>(instance, args, destination)` accepts caller-owned
  result storage. Input is copied before dispatch, supporting overlap; only the
  used destination prefix is copied after complete success. Guest side effects
  before an exception remain, but the destination is unchanged.

## Method

Ryzen 9 3900X, Ubuntu 24.04.3, x64, .NET SDK 10.0.400 / runtime 10.0.11,
Wasmtime 44.0.0, WASI SDK 25. Dependencies are identical in both builds.
Sequential Release processes used CPU 7, tiered compilation disabled and process
nice level -10. No builds ran during the final measurements. This is a shared
machine; CPU affinity and priority do not remove all contention.

Both directories used the identical current sample DLL/PDB. The baseline kept
its frozen `2ae4b6f` runtime libraries and Wasm. Output directories were
`/tmp/dni-equal/baseline` and `/tmp/dni-equal/improved`, with equal-length paths.
Three process pairs ran in AB, BA, AB order. Each process records five timed
samples after up to 2,000 warmup calls; tables show medians of the three process
medians. Results are consumed. Host allocation uses
`GC.GetAllocatedBytesForCurrentThread`, excluding guest/native allocations.
The batch row divides time and allocation by 1,024 elements.

The fixed workload sizes and iterations are in
[`IterationBenchmarks.cs`](../sample/PerformanceSample/IterationBenchmarks.cs).
The two new-API modes run only against the improved build. They are not baseline
speedup comparisons.

## Results

| Workload | Before, ns | After, ns | Speedup | Host bytes before → after |
|---|---:|---:|---:|---:|
| `int control` | 276.4 | 277.9 | 0.99× | 0.0 → 0.0 |
| `callback arity 2` | 23,539.5 | 2,192.1 | 10.74× | 592.0 → 0.0 |
| `callback arity 3` | 28,426.2 | 2,431.5 | 11.69× | 808.0 → 0.0 |
| `callback arity 4` | 30,624.0 | 2,456.4 | 12.47× | 992.0 → 0.0 |
| `callback void` | 14,026.5 | 1,460.4 | 9.60× | 312.0 → 0.0 |
| `batch 1024 per element` | 39.8 | 41.8 | 0.95× | 4.0 → 4.0 |
| `nested DTO callback (32 KiB)` | 447,869.6 | 443,084.8 | 1.01× | 101,916.7 → 68,738.7 |
| `List<string>[256] roundtrip` | 993,979.5 | 681,523.5 | 1.46× | 16,664.0 → 12,568.0 |
| `Dictionary<string,int>[256] roundtrip` | 1,940,422.5 | 1,960,611.0 | 0.99× | 56,244.7 → 31,268.4 |
| `string return (32 chars)` | 4,750.3 | 1,270.1 | 3.74× | 232.0 → 232.0 |
| `string return (32 KiB UTF-8)` | 385,679.9 | 101,834.3 | 3.79× | 72,824.0 → 72,824.0 |
| `string[32] -> string` | 26,757.9 | 1,689.5 | 15.84× | 1,208.0 → 232.0 |
| `string[32] -> int` | 24,795.4 | 1,004.1 | 24.69× | 1,144.0 → 0.0 |
| `string[32] -> void` | 15,448.3 | 935.2 | 16.52× | 976.0 → 0.0 |
| `List<string>[8] roundtrip` | 97,882.1 | 64,159.2 | 1.53× | 2,656.4 → 664.0 |
| `Dictionary<string,int>[8] roundtrip` | 138,676.5 | 118,382.9 | 1.17× | 5,160.5 → 1,544.0 |

The unchanged scalar control differs by 0.5%. The array-return batch measured
5% slower in this comparison (39.8 → 41.8 ns/element), so no batch latency
improvement is claimed.
Large callback/string wins are clear; dictionary and nested DTO latency changes
are small relative to the observed ranges. Allocation reductions are the clearer
result for these generic paths. String result allocation is unchanged: it still
creates an owned host string and uses the existing host decoder.

New typed callback overloads measured:

| Workload | ns/call | Host bytes/call |
|---|---:|---:|
| `int control` | 283.1 | 0.0 |
| `typed callback arity 2` | 989.2 | 0.0 |
| `typed callback arity 3` | 1,085.3 | 0.0 |
| `typed callback arity 4` | 1,108.2 | 0.0 |

Batch output reuse, measured together in each process:

| Workload | ns/element | Host bytes/element |
|---|---:|---:|
| `batch 1024 per element` | 41.9 | 4.0 |
| `batch 1024 into span per element` | 41.6 | 0.0 |

The destination overload removes the host array allocation; it does not remove
native buffers or guest delegate calls. Its latency is effectively unchanged.

## Earlier workload regression comparison

The existing `--fast-paths` matrix also ran in three AB, BA, AB process pairs
from the same equal-length directories, after the main comparison. These shorter
samples have more scheduling sensitivity; the raw ranges are retained as
`regression-{baseline,improved}-{1,2,3}.txt`.

| Workload | Before, ns | After, ns | Host bytes before → after |
|---|---:|---:|---:|
| `int -> int` | 292.0 | 279.2 | 0.0 → 0.0 |
| `double -> double` | 367.2 | 351.9 | 0.0 → 0.0 |
| `long -> long` | 357.5 | 338.0 | 0.0 → 0.0 |
| `scalar arity 2` | 356.8 | 333.6 | 0.0 → 0.0 |
| `scalar arity 3` | 397.3 | 395.9 | 0.0 → 0.0 |
| `scalar arity 4` | 411.9 | 415.3 | 0.0 → 0.0 |
| `callback params` | 1,548.4 | 1,531.1 | 0.0 → 0.0 |
| `callback typed` | 1,073.2 | 1,071.1 | 0.0 → 0.0 |
| `callback typed double` | 1,129.2 | 1,112.6 | 0.0 → 0.0 |
| `callback raw byte[32]` | 1,672.0 | 1,704.6 | 56.0 → 56.0 |
| `callback raw byte[65536]` | 22,754.6 | 23,213.0 | 65,560.0 → 65,560.0 |
| `double[32] -> int` | 902.5 | 887.7 | 0.0 → 0.0 |
| `double[32] -> void` | 815.2 | 813.8 | 0.0 → 0.0 |
| `double[32] -> double[]` | 1,025.8 | 1,012.0 | 280.0 → 280.0 |
| `List<double>[32] -> int` | 1,203.3 | 1,167.2 | 0.0 → 0.0 |
| `nested DTO roundtrip (32 KiB)` | 527,781.6 | 435,808.0 | 36,536.9 → 35,504.7 |
| `batch int[1024] (per element)` | 52.4 | 50.5 | 4.0 → 4.0 |

## Iteration evidence and rejected experiments

[Final raw runs](benchmarks/2026-09-25-iterations/) retain every process used in
the tables. [Diagnostic runs](benchmarks/2026-09-25-iterations/diagnostics/)
retain earlier prototypes and superseded comparisons:

* `pointer-*` versus `span-*`: pointer indexing initially appeared faster, but
  subsequent comparisons did not show a repeatable gain amid changing controls.
  The managed span loop remains.
* `unroll-*` versus `unroll-control-*`: four-way unrolling consistently regressed
  from roughly 44 to 51 ns/element with comparable scalar controls. Removed.
* `capacity-1` used `Activator` for capacity constructors and regressed small
  collections. `factories-1` replaced reflection invocation with cached typed
  constructors; the final implementation retains those factories.
* `beforebuffers`, `buffers`, `beforefactories`, `dictionary`, and `strings`
  record intermediate stages, not isolated final claims. `prototype-1` overlapped
  a build and is diagnostic only. Earlier harness versions omit later workloads.
* `publish-*` was an initially intended final comparison before the assembly
  lookup change. Baseline and improved output paths had different lengths.
  Allocation tracing exposed recurring `FileInfo`/path allocations: shorter paths
  appeared to allocate less independently of the code change. Those numbers are
  superseded by the equal-length-directory results above.
* The lookup trace counted 44,008 `System.Private.CoreLib` host requests over
  22,000 list calls before the loader change; the after trace has no core-library
  host requests and measures 664 host bytes per call. The final regression test verifies
  warm repeated calls add no host requests and separately verifies custom
  assemblies still load independently in fresh runtimes.

The earlier rejected signature-hoisting, boxing shim, heap sizing and pooling
allocator experiments were not retried. No further contained candidate found in
this pass had sufficient evidence to justify shipping. Remaining work would
require a broader serialization protocol, generated dispatch, or different
buffer-ownership APIs; these have not been benchmarked and are not claimed as
improvements.

## Validation and reproduction

The full Release solution build succeeds and **181 tests pass, zero failures or
skips** (22 additional cases since `2ae4b6f`). Coverage includes mixed callback
kinds and discarded results, custom/closed-static/multicast delegates, nested
calls and guest GC, null fallback, Unicode/NUL/invalid-surrogate behavior,
collection count boundaries and wire compatibility, serializer depth/errors,
string async/static/interface fallback, overlapping batch buffers and exceptions,
and loaded/custom assembly lookup. Existing MessagePack advisories and one
existing xUnit assertion-style warning remain.

```bash
export WASI_SDK_PATH="$HOME/.wasi-sdk/wasi-sdk-25.0-x86_64-linux"
dotnet build DotNetIsolator.sln -c Release -p:UseSharedCompilation=false
dotnet test test/DotNetIsolator.Test -c Release --no-build --no-restore
# Repeat in sequential processes, using equal-length paths for before/after builds.
DOTNET_TieredCompilation=0 taskset -c 7 dotnet \
  sample/PerformanceSample/bin/Release/net10.0/PerformanceSample.dll --iteration-paths
# New API measurements:
DOTNET_TieredCompilation=0 taskset -c 7 dotnet \
  sample/PerformanceSample/bin/Release/net10.0/PerformanceSample.dll --iteration-typed
DOTNET_TieredCompilation=0 taskset -c 7 dotnet \
  sample/PerformanceSample/bin/Release/net10.0/PerformanceSample.dll --iteration-batch
```

To reproduce the exact comparison, build `2ae4b6f` separately, preserve its output
libraries/Wasm, and copy only the current `PerformanceSample.dll` and `.pdb` into
that baseline output. Do not run the new typed-overload mode against the old
library. Optional process-only priority elevation used here was
`sudo renice -n -10 -p "$$"` before launching the sequential benchmark processes.
