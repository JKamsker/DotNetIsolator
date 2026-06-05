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

## Measured Results

Measured on June 5, 2026:

* .NET SDK 10.0.204, runtime .NET 10.0.8
* Windows 10.0.26200, `win-x64`
* WASI SDK 25.0
* Wasmtime .NET package 44.0.0

Representative run with `--isolated-iterations 100000 --startup-iterations 5`:

```text
Steady-state call overhead
Direct host Increment: total 17.745 ms, mean 1.775 ns
Isolated warm-runtime Increment: total 24.771 ms, mean 247.714 ns
Isolated/direct mean ratio: 140x

Startup medians
No module cache: host 301.500 ms, runtime 36.902 ms, object 347.600 us, method 115.300 us, first call 53.200 us
Cold module cache: host 327.497 ms, runtime 42.183 ms, object 429.100 us, method 179.800 us, first call 90.500 us
Warm module cache: host 1.581 ms, runtime 45.727 ms, object 319.800 us, method 131.700 us, first call 68.900 us
Warm host: runtime 39.762 ms, object 424.800 us, method 155.000 us, first call 61.500 us
Runtime memory snapshot preload: 84.893 ms
Warm runtime memory snapshot: runtime 14.105 ms, object 387.900 us, method 109.500 us, first call 64.200 us
```

Interpretation:

* A best-case warm isolated scalar call is around `248 ns` on this machine,
  versus about `1.8 ns` for the direct host call. That is roughly `140x` slower
  for this tiny method.
* Before the scalar fast path, the same benchmark measured about `37 us` per
  isolated call and about `21,000x` direct-call overhead on this machine. The
  fast path cuts the measured isolated call cost by roughly `150x`.
* The warm module cache cuts host construction from about `302 ms` to about
  `1.6 ms`, roughly a `190x` improvement for that phase.
* The first cache miss is slower than no cache because it compiles and writes the
  serialized module. The cache is intended for repeated host construction.
* Runtime startup on a warm host is still about `40 ms` because the .NET WASI
  runtime is still instantiated and started.
* The runtime memory snapshot moves one-time startup work into a preload step and
  cuts repeated runtime construction to about `14 ms`, roughly a `3x`
  improvement for that phase.
