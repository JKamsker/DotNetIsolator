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

## Wizer replacement

The standalone Wizer project moved into Wasmtime, and the old CLI is no longer
the forward path. Wasmtime now documents serializing compiled modules to disk and
deserializing them later to skip compilation on the critical path:

* https://github.com/bytecodealliance/wizer
* https://docs.wasmtime.dev/examples-serialize.html
* https://bytecodealliance.github.io/wasmtime-dotnet/api/Wasmtime.Module.html

DotNetIsolator uses that mechanism through `Wasmtime.Module.Serialize` and
`Wasmtime.Module.DeserializeFile`. The cache is enabled by default through
`IsolatedRuntimeHostOptions.UsePrecompiledModuleCache` and can be redirected with
`PrecompiledModuleCacheDirectory`.

This is not a full Wizer snapshot of an already-started .NET runtime. It removes
repeated Wasmtime module compilation from host construction, but runtime startup
still pays the cost of instantiating the wasm module and running `_start`.

## Measured Results

Measured on June 5, 2026:

* .NET SDK 10.0.204, runtime .NET 10.0.8
* Windows 10.0.26200, `win-x64`
* WASI SDK 25.0
* Wasmtime .NET package 44.0.0

Representative run:

```text
Steady-state call overhead
Direct host Increment: total 17.557 ms, mean 1.756 ns
Isolated warm-runtime Increment: total 70.376 ms, mean 35.188 us
Isolated/direct mean ratio: 20,042x

Startup medians
No module cache: host 296.274 ms, runtime 38.029 ms, object 321.400 us, method 115.600 us, first call 863.000 us
Cold module cache: host 321.183 ms, runtime 39.985 ms, object 424.600 us, method 146.400 us, first call 950.500 us
Warm module cache: host 1.641 ms, runtime 47.530 ms, object 349.000 us, method 126.800 us, first call 998.000 us
```

Interpretation:

* A best-case warm isolated call is around `35 us` on this machine, versus about
  `1.8 ns` for the direct host call. That is roughly `20,000x` slower for this
  tiny method. Larger guest workloads amortize the boundary cost better.
* The warm module cache cuts host construction from about `296 ms` to about
  `1.6 ms`, roughly a `180x` improvement for that phase.
* The first cache miss is slower than no cache because it compiles and writes the
  serialized module. The cache is intended for repeated host construction.
* Runtime startup is still about `37-48 ms` because the .NET WASI runtime is
  still instantiated and started. A true Wizer-style initialized-memory snapshot
  would be needed to remove most of that remaining cost.
