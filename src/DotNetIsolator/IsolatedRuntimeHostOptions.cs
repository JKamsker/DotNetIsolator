namespace DotNetIsolator;

public sealed class IsolatedRuntimeHostOptions
{
    public bool UsePrecompiledModuleCache { get; init; } = true;

    public string? PrecompiledModuleCacheDirectory { get; init; }

    public bool UseRuntimeMemorySnapshot { get; init; }

    /// <summary>
    /// When enabled, started guest instances are reused across <see cref="IsolatedRuntime"/>
    /// lifetimes instead of instantiating a fresh Wasmtime instance each time. Each reused
    /// instance is reset to the clean post-startup state by restoring the runtime memory
    /// snapshot, so this requires <see cref="UseRuntimeMemorySnapshot"/>. Instances that grow
    /// their linear memory beyond the snapshot size are not pooled. This skips the per-runtime
    /// Wasmtime instantiation cost for runtime-churn workloads, typically reducing repeated
    /// runtime creation by more than an order of magnitude over the snapshot alone.
    ///
    /// SECURITY: resetting an instance restores the runtime's roots so the previous tenant's
    /// managed state is unreachable and is zero-overwritten on the next allocation, but it does
    /// not scrub every orphaned page unless <see cref="InstancePoolResetMode"/> is set to
    /// <see cref="DotNetIsolator.InstancePoolResetMode.FullSnapshotRestore"/>. In the default
    /// fast mode, a previous tenant's leftover bytes can therefore remain readable to guest code
    /// that performs raw/unsafe memory access. Use the default mode only when guest code is
    /// trusted (isolation for cleanliness, not as a boundary against hostile code).
    /// </summary>
    public bool UseInstancePool { get; init; }

    public int MaxInstancePoolSize { get; init; } = Environment.ProcessorCount;

    public DotNetIsolator.InstancePoolResetMode InstancePoolResetMode { get; init; } =
        DotNetIsolator.InstancePoolResetMode.FastTrustedChangedPages;

    public bool UseMemoryInitCopyOnWrite { get; init; } = true;

    public bool UsePoolingAllocator { get; init; }

    public uint PoolingInstanceCapacity { get; init; } = 64;

    public uint PoolingMemoryCapacity { get; init; } = 64;

    public uint PoolingTableCapacity { get; init; } = 64;

    public nuint PoolingMaxMemorySize { get; init; } = 512 * 1024 * 1024;

    public nuint PoolingMaxTableElements { get; init; } = 8192;
}
