using System.Collections.Concurrent;
using Wasmtime;

namespace DotNetIsolator;

public class IsolatedRuntimeHost : IDisposable
{
    private readonly static string _modulePath;
    private readonly static string _wasmBclDir;
    private const ulong NoMemoryReservationForGrowth = 0;
    private const string WasmAppArgumentZero = "DotNetIsolator.WasmApp.wasm";

    private readonly IsolatedRuntimeHostOptions _options;
    private readonly object _runtimeMemorySnapshotLock = new();
    private readonly ConcurrentBag<RuntimeInstanceLease> _parkedInstances = new();
    private WasiConfiguration? _wasiConfiguration;
    private RuntimeMemorySnapshot? _runtimeMemorySnapshot;
    private List<AssemblyLoadCallback> _assemblyLoaders = new();

    static IsolatedRuntimeHost()
    {
        var hostBinariesDir = Path.Combine(
            AppContext.BaseDirectory,
            "IsolatedRuntimeHost");
        _modulePath = Path.Combine(hostBinariesDir, "DotNetIsolator.WasmApp.wasm");
        _wasmBclDir = Path.Combine(hostBinariesDir, "WasmAssemblies");
    }

    public IsolatedRuntimeHost()
        : this(new IsolatedRuntimeHostOptions())
    {
    }

    public IsolatedRuntimeHost(IsolatedRuntimeHostOptions options)
    {
        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        if (options.UseInstancePool && !options.UseRuntimeMemorySnapshot)
        {
            throw new ArgumentException(
                $"{nameof(options.UseInstancePool)} requires {nameof(options.UseRuntimeMemorySnapshot)} to be enabled.",
                nameof(options));
        }

        _options = options;
        Engine = CreateEngine(options);
        Linker = new Linker(Engine);
        Module = PrecompiledModuleCache.LoadOrCompile(Engine, _modulePath, options);

        Linker.DefineWasi();
        AddIsolatedImports();
        WasiPreview2Shim.DefineMissingImports(Linker, Module);
        _assemblyLoaders.Add(LoadAssemblyFromWasmBcl);
    }

    internal Engine Engine { get; }
    internal Linker Linker { get; }
    internal Module Module { get; }
    internal bool UseInstancePool => _options.UseInstancePool;
    internal WasiConfiguration WasiConfigurationOrDefault
        => _wasiConfiguration ?? CreateDefaultWasiConfiguration();

    public IsolatedRuntimeHost WithWasiConfiguration(WasiConfiguration configuration)
    {
        if (configuration is null)
        {
            throw new ArgumentNullException(nameof(configuration));
        }

        lock (_runtimeMemorySnapshotLock)
        {
            if (_runtimeMemorySnapshot is not null)
            {
                throw new InvalidOperationException(
                    $"{WithWasiConfiguration} cannot be called after the runtime memory snapshot has been initialized.");
            }

            if (_wasiConfiguration is not null)
            {
                throw new InvalidOperationException($"{WithWasiConfiguration} can only be called once.");
            }

            _wasiConfiguration = configuration;
        }

        return this;
    }

    public IsolatedRuntimeHost WithAssemblyLoader(AssemblyLoadCallback callback)
    {
        if (callback is null)
        {
            throw new ArgumentNullException(nameof(callback));
        }

        _assemblyLoaders.Add(callback);
        return this;
    }

    public IsolatedRuntimeHost WithBinDirectoryAssemblyLoader()
    {
        var binDir = Path.GetDirectoryName(typeof(IsolatedRuntimeHost).Assembly.Location)!;
        return WithDirectoryAssemblyLoader(binDir);
    }

    public IsolatedRuntimeHost WithDirectoryAssemblyLoader(string directoryPath)
    {
        return WithAssemblyLoader(assemblyName =>
        {
            var path = Path.Combine(directoryPath, $"{assemblyName}.dll");
            return AssemblyFileCache.ReadAllBytesIfExists(path);
        });
    }

    public void PreloadRuntimeMemorySnapshot()
    {
        if (!_options.UseRuntimeMemorySnapshot)
        {
            throw new InvalidOperationException(
                $"{nameof(IsolatedRuntimeHostOptions.UseRuntimeMemorySnapshot)} must be enabled before preloading the runtime memory snapshot.");
        }

        _ = GetRuntimeMemorySnapshot();
    }

    public void Dispose()
    {
        while (_parkedInstances.TryTake(out var parked))
        {
            parked.Store.Dispose();
        }

        Module.Dispose();
        Linker.Dispose();
        Engine.Dispose();
    }

    // Provides a started guest instance, reset to its clean post-startup state. With pooling
    // enabled, a parked instance is reused (skipping Wasmtime instantiation); otherwise a fresh
    // instance is instantiated and snapshot-restored. Only valid when a runtime memory snapshot
    // is available (enforced by the constructor for UseInstancePool).
    internal RuntimeInstanceLease RentRuntimeInstance(object storeData)
    {
        var snapshot = GetRuntimeMemorySnapshot()
            ?? throw new InvalidOperationException(
                $"{nameof(IsolatedRuntimeHostOptions.UseInstancePool)} requires {nameof(IsolatedRuntimeHostOptions.UseRuntimeMemorySnapshot)} to be enabled.");

        if (_parkedInstances.TryTake(out var parked))
        {
            // Reset the reused instance to the clean post-startup state. Restoring the snapshot
            // resets every runtime root, so the previous tenant's allocations become unreachable
            // and are zero-overwritten on the next allocation.
            snapshot.RestoreTo(parked.Exports.Memory);
            parked.Store.SetData(storeData);
            return parked;
        }

        var store = CreateStore(storeData);
        try
        {
            var instance = Linker.Instantiate(store, Module);
            var exports = IsolatedRuntimeExports.Bind(instance);
            snapshot.RestoreTo(exports.Memory);
            return new RuntimeInstanceLease(store, instance, exports);
        }
        catch
        {
            store.Dispose();
            throw;
        }
    }

    internal void ReturnRuntimeInstance(RuntimeInstanceLease lease)
    {
        // Only pool instances that stayed at the snapshot size. An instance that grew its linear
        // memory has trailing pages a fresh instance would see as OS-zero; restoring the snapshot
        // would not re-zero them, so it is dropped rather than risking a cross-tenant leak.
        var snapshot = _runtimeMemorySnapshot;
        if (snapshot is not null && lease.Exports.Memory.GetLength() == snapshot.MemoryLength)
        {
            _parkedInstances.Add(lease);
        }
        else
        {
            lease.Store.Dispose();
        }
    }

    internal Store CreateStore(object data)
    {
        var store = new Store(Engine);
        try
        {
            store.SetWasiConfiguration(WasiConfigurationOrDefault);
            store.SetData(data);
            return store;
        }
        catch
        {
            store.Dispose();
            throw;
        }
    }

    internal RuntimeMemorySnapshot? GetRuntimeMemorySnapshot()
    {
        if (!_options.UseRuntimeMemorySnapshot)
        {
            return null;
        }

        lock (_runtimeMemorySnapshotLock)
        {
            return _runtimeMemorySnapshot ??= RuntimeMemorySnapshot.Create(this);
        }
    }

    private static byte[]? LoadAssemblyFromWasmBcl(string assemblyName)
    {
        var path = Path.Combine(_wasmBclDir, $"{assemblyName}.dll");
        return AssemblyFileCache.ReadAllBytesIfExists(path);
    }

    private static WasiConfiguration CreateDefaultWasiConfiguration()
        => new WasiConfiguration()
            .WithArg(WasmAppArgumentZero)
            .WithInheritedStandardOutput()
            .WithInheritedStandardError();

    private static Engine CreateEngine(IsolatedRuntimeHostOptions options)
    {
        if (!options.UsePoolingAllocator)
        {
            using var config = CreateConfig(options);
            return new Engine(config);
        }

        ValidatePoolingOptions(options);

        using var poolingAllocationConfig = new PoolingAllocationConfig()
            .WithMaxCoreInstances(options.PoolingInstanceCapacity)
            .WithMaxMemories(options.PoolingMemoryCapacity)
            .WithMaxMemorySize(options.PoolingMaxMemorySize)
            .WithMaxTables(options.PoolingTableCapacity)
            .WithMaxTableElements(options.PoolingMaxTableElements);

        using var pooledConfig = CreateConfig(options)
            .WithMemoryMayMove(true)
            .WithStaticMemoryMaximumSize((ulong)options.PoolingMaxMemorySize)
            .WithMemoryReservationForGrowth(NoMemoryReservationForGrowth)
            .WithPoolingAllocationStrategy(poolingAllocationConfig);

        return new Engine(pooledConfig);
    }

    private static Config CreateConfig(IsolatedRuntimeHostOptions options)
        => new Config().WithMemoryInitCopyOnWrite(options.UseMemoryInitCopyOnWrite);

    private static void ValidatePoolingOptions(IsolatedRuntimeHostOptions options)
    {
        if (options.PoolingInstanceCapacity == 0)
        {
            throw new ArgumentException($"{nameof(options.PoolingInstanceCapacity)} must be greater than zero.", nameof(options));
        }

        if (options.PoolingMemoryCapacity == 0)
        {
            throw new ArgumentException($"{nameof(options.PoolingMemoryCapacity)} must be greater than zero.", nameof(options));
        }

        if (options.PoolingTableCapacity == 0)
        {
            throw new ArgumentException($"{nameof(options.PoolingTableCapacity)} must be greater than zero.", nameof(options));
        }

        if (options.PoolingMaxMemorySize == 0)
        {
            throw new ArgumentException($"{nameof(options.PoolingMaxMemorySize)} must be greater than zero.", nameof(options));
        }

        if (options.PoolingMaxTableElements == 0)
        {
            throw new ArgumentException($"{nameof(options.PoolingMaxTableElements)} must be greater than zero.", nameof(options));
        }
    }

    private void AddIsolatedImports()
    {
        Linker.DefineFunction("dotnetisolator", "request_assembly", (CallerFunc<int, int, int, int, int>)HandleRequestAssembly);
        Linker.DefineFunction("dotnetisolator", "call_host", (CallerFunc<int, int, int, int, int>)HandleCallHost);
    }

    private int HandleRequestAssembly(Caller caller, int assemblyNamePtr, int assemblyNameLen, int suppliedBytesPtr, int suppliedBytesLen)
    {
        var memory = caller.GetMemory("memory") ?? throw new InvalidOperationException("Caller lacks required export 'memory'");
        var assemblyName = memory.ReadString(assemblyNamePtr, assemblyNameLen);

        foreach (var loader in _assemblyLoaders)
        {
            var assemblyBytes = loader(assemblyName);
            if (assemblyBytes is not null)
            {
                // No need to free this memory after as it's held permanently to represent the assembly
                var malloc = caller.GetFunction("malloc") ?? throw new InvalidOperationException("Caller lacks required export 'malloc'");
                var copiedAssemblyBytesPtr = CopyValue(
                    malloc.WrapFunc<int, int>()!,
                    memory,
                    assemblyBytes);
                memory.Write(suppliedBytesPtr, copiedAssemblyBytesPtr);
                memory.Write(suppliedBytesLen, assemblyBytes.Length);
                return 1;
            }
        }

        return 0;
    }

    private int HandleCallHost(Caller caller, int invocationPtr, int invocationLength, int resultPtrPtr, int resultLengthPtr)
    {
        var runtime = IsolatedRuntime.FromStore(caller.Store);
        return runtime.AcceptCallFromGuest(invocationPtr, invocationLength, resultPtrPtr, resultLengthPtr);
    }

    private static int CopyValue(Func<int, int> malloc, Memory memory, ReadOnlySpan<byte> value)
    {
        var resultPtr = malloc(value.Length);
        if (resultPtr == 0)
        {
            throw new InvalidOperationException($"malloc failed when trying to allocate {value.Length} bytes");
        }

        var destinationSpan = memory.GetSpan(resultPtr, value.Length);
        value.CopyTo(destinationSpan);
        return resultPtr;
    }
}

public delegate byte[]? AssemblyLoadCallback(string assemblyName);

internal sealed class RuntimeInstanceLease
{
    public RuntimeInstanceLease(Store store, Instance instance, IsolatedRuntimeExports exports)
    {
        Store = store;
        Instance = instance;
        Exports = exports;
    }

    public Store Store { get; }

    public Instance Instance { get; }

    public IsolatedRuntimeExports Exports { get; }
}
