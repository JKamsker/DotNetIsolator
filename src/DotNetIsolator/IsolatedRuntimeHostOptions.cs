namespace DotNetIsolator;

public sealed class IsolatedRuntimeHostOptions
{
    public bool UsePrecompiledModuleCache { get; init; } = true;

    public string? PrecompiledModuleCacheDirectory { get; init; }

    public bool UseRuntimeMemorySnapshot { get; init; }

    public bool UseMemoryInitCopyOnWrite { get; init; } = true;

    public bool UsePoolingAllocator { get; init; }

    public uint PoolingInstanceCapacity { get; init; } = 64;

    public uint PoolingMemoryCapacity { get; init; } = 64;

    public uint PoolingTableCapacity { get; init; } = 64;

    public nuint PoolingMaxMemorySize { get; init; } = 512 * 1024 * 1024;

    public nuint PoolingMaxTableElements { get; init; } = 8192;
}
