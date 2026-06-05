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
  method lookup already created
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

## Measured Results

Measured on June 5, 2026:

* .NET SDK 10.0.204, runtime .NET 10.0.8
* Windows 10.0.26200, `win-x64`
* WASI SDK 25.0
* Wasmtime .NET package 44.0.0

Representative run:

```text
Steady-state call overhead
Direct host Increment: total 17.600 ms, mean 1.760 ns
Isolated warm-runtime Increment: total 74.442 ms, mean 37.221 us
Isolated/direct mean ratio: 21,148x

Startup medians
No module cache: host 320.187 ms, runtime 46.153 ms, object 446.700 us, method 186.600 us, first call 1.461 ms
Cold module cache: host 456.377 ms, runtime 55.407 ms, object 558.300 us, method 190.700 us, first call 1.358 ms
Warm module cache: host 2.123 ms, runtime 61.453 ms, object 371.800 us, method 143.200 us, first call 1.033 ms
Warm host: runtime 52.594 ms, object 447.500 us, method 171.400 us, first call 1.481 ms
Runtime memory snapshot preload: 102.919 ms
Warm runtime memory snapshot: runtime 17.497 ms, object 581.400 us, method 161.500 us, first call 1.513 ms
```

Interpretation:

* A best-case warm isolated call is around `37 us` on this machine, versus about
  `1.8 ns` for the direct host call. That is roughly `20,000x` slower for this
  tiny method. Larger guest workloads amortize the boundary cost better.
* The warm module cache cuts host construction from about `320 ms` to about
  `2.1 ms`, roughly a `150x` improvement for that phase.
* The first cache miss is slower than no cache because it compiles and writes the
  serialized module. The cache is intended for repeated host construction.
* Runtime startup on a warm host is still about `53 ms` because the .NET WASI
  runtime is still instantiated and started.
* The runtime memory snapshot moves one-time startup work into a preload step and
  cuts repeated runtime construction to about `17.5 ms`, roughly a `3x`
  improvement for that phase.
