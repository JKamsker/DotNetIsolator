# Callback transport follow-up — September 25, 2026

This comparison starts at `e6fd608`, the previous PR head containing callback IDs
and typed overloads. It measures additional improvements beyond the
[preceding fast-path report](performance-2026-09-25.md), not the upstream baseline.

## Changes

Scalar callbacks now pass their argument bits, packed type tags, and result bits
through Wasm parameters/returns. Successful host dispatch no longer maps guest
memory to read or write an invocation struct. Native code initializes an error
slot; only the host failure path writes it. Registered signature checks and opaque
host exception behavior remain in place.

Raw byte-array callbacks now resolve a numeric ID once and pass pinned argument
descriptors through a dedicated import, eliminating the MessagePack envelope and
guest staging serialization. Common zero/one-argument `Func` callbacks invoke
directly; other delegate shapes retain reflection dispatch. Up to eight argument
descriptors use stack storage, with heap storage for larger arities. The outer
argument array and its elements stay pinned through synchronous host execution,
including nested guest calls and GC.

Each argument is copied into host-owned storage, and each result is copied into
guest-owned storage. Both sides can retain or mutate their arrays independently.
Null, empty, zero-argument, object-typed, and void callback behavior is preserved.
A non-null empty result receives a native address so it remains distinct from
null without depending on `malloc(0)` behavior. Result buffers are freed after
consumption, including failures. Existing public APIs use the new transport.

## Results

Values are medians of three process medians, each containing five timed samples.
Timings include the host-to-guest invocation that executes the callback and the
return to the host. Allocation counts cover the host thread only, excluding guest
managed and native allocation. Raw identity callbacks return the received bytes;
the guest consumes the result length.

| Workload | `e6fd608` | Follow-up | Speedup | Host bytes/call, before → after |
|---|---:|---:|---:|---:|
| `int -> int` control | 285.1 ns | 273.2 ns | 1.04× | 0 → 0 |
| Params scalar callback | 1,830.5 ns | 1,535.8 ns | 1.19× | 0 → 0 |
| Typed int callback | 1,350.4 ns | 1,066.7 ns | 1.27× | 0 → 0 |
| Typed double callback | 1,395.7 ns | 1,131.1 ns | 1.23× | 0 → 0 |
| Raw `byte[32]` callback | 6,700.5 ns | 1,705.5 ns | 3.93× | 216 → 56 |
| Raw `byte[65536]` callback | 54,379.9 ns | 21,503.0 ns | 2.53× | 65,721.3 → 65,560 |

The typed scalar reduction is 19–21%; the unchanged control improved by 4.2%, so
these are approximate workload improvements rather than isolated instruction
costs. Raw callback latency falls by 61–75%. The small raw callback also removes
160 host bytes per call (74%), leaving the owned 32-byte argument array and its
object overhead. Large payloads still pay for owned copies on both sides.

All three process medians, in nanoseconds:

| Workload | Baseline processes | Improved processes |
|---|---|---|
| Control | 275.8 / 285.1 / 286.1 | 273.2 / 269.9 / 280.2 |
| Params callback | 1960.2 / 1821.1 / 1830.5 | 1668.7 / 1535.8 / 1517.4 |
| Typed int | 1375.0 / 1350.4 / 1338.2 | 1066.7 / 1067.6 / 1065.2 |
| Typed double | 1874.2 / 1395.7 / 1387.5 | 1255.1 / 1125.7 / 1131.1 |
| Raw 32 bytes | 6692.7 / 6732.7 / 6700.5 | 1742.2 / 1705.5 / 1690.9 |
| Raw 64 KiB | 54394.5 / 54379.9 / 53334.7 | 21467.9 / 21503.0 / 21966.0 |

[Raw focused runs and test results](benchmarks/2026-09-25-callbacks/) include each
process's minimum and maximum sample. The machine was shared, and some samples
show scheduling/frequency noise. Six earlier pairs using the shorter full-suite
harness showed substantial swings even in unchanged controls. Those runs are
retained in [diagnostics](benchmarks/2026-09-25-callbacks/diagnostics/), alongside
the codec experiment below. They are not mixed into the longer-sample table.
The first three diagnostic pairs used CPU 6 and the last three used CPU 7.

## Method and reproduction

- AMD Ryzen 9 3900X, Ubuntu 24.04.3 LTS, x64.
- .NET SDK 10.0.400, .NET/WASI runtime 10.0.11, Wasmtime 44.0.0, WASI SDK 25.
- Release build, tiered compilation disabled, sequential processes pinned to CPU 7.
- Baseline then improved, repeated three times; no simultaneous benchmark processes.
- The same compiled sample assembly ran against frozen `e6fd608` libraries/Wasm
  and the follow-up libraries/Wasm. Only the sample DLL/PDB were copied into the
  baseline output after adding the focused harness; baseline runtime binaries
  remained unchanged. Both versions already expose all public APIs it uses.
- Per sample: 1,000,000 control calls; 200,000 of each scalar callback;
  50,000 small raw callbacks; 5,000 large raw callbacks. Each workload warms up
  for 2,000 calls before its five timed samples. Every result contributes to a
  sink, which matched across all six focused processes.

Build the solution and test:

```bash
export WASI_SDK_PATH="$HOME/.wasi-sdk/wasi-sdk-25.0-x86_64-linux"
dotnet build DotNetIsolator.sln -c Release
dotnet test test/DotNetIsolator.Test -c Release --no-build --no-restore
taskset -c 7 env DOTNET_TieredCompilation=0 \
  dotnet sample/PerformanceSample/bin/Release/net10.0/PerformanceSample.dll --callback-paths
```

For an A/B reproduction, build `e6fd608` in a separate checkout with the four
current benchmark files (`BenchmarkOptions.cs`, `BenchmarkTarget.cs`,
`FastPathBenchmarks.cs`, `Program.cs`) copied into `sample/PerformanceSample`,
then run each output with the same command above. `--fast-paths` still runs the
broader scalar, collection, DTO, and batch suite with shorter callback samples.

## Validation and experiments

The full Release solution build succeeds. All **159 tests pass**, with none
failed or skipped: the previous 153 plus six raw callback tests covering
independent ownership, null/empty arrays, zero and nine arguments, void/object
callbacks, hidden failures, registration after a failed lookup, and nested
callbacks with guest GC. Existing scalar tests cover all twelve primitive kinds,
signature mismatches, exceptions, reentry, and runtime/pool identity. Existing
MessagePack NuGet advisories and an existing xUnit assertion-style warning remain.

Two prototypes informed the retained implementation:

- An initial direct scalar internal-call signature required the absent generated
  `wasm_invoke_liliii` interpreter wrapper and failed at invocation. Packing the
  kind tags and ordering parameters as `(i32, i32, i32, i64) -> i64` uses the
  existing `wasm_invoke_liiil` wrapper. This order is documented in `Interop.cs`.
- Cached guest `Func<T, long>` / `Func<long, T>` codec delegates did not improve
  the exploratory paired run: typed int measured 1,076.9 ns versus 1,066.4 ns
  with the existing codec; double measured 1,134.5 ns versus 1,116.7 ns. The
  extra delegate machinery was removed. These short exploratory samples are
  evidence against retaining the added complexity, not a precise regression claim.
