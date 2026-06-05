using Wasmtime;

namespace DotNetIsolator;

internal sealed class RuntimeMemorySnapshot
{
    private const int WebAssemblyPageSize = 64 * 1024;
    private static readonly object StoreData = new();

    private readonly byte[] _memory;

    private RuntimeMemorySnapshot(byte[] memory)
    {
        _memory = memory;
    }

    public static RuntimeMemorySnapshot Create(IsolatedRuntimeHost host)
    {
        using var store = host.CreateStore(StoreData);
        var instance = host.Linker.Instantiate(store, host.Module);
        var exports = IsolatedRuntimeExports.Bind(instance);

        exports.Start();

        var memoryLength = exports.Memory.GetLength();
        if (memoryLength > int.MaxValue)
        {
            throw new InvalidOperationException(
                $"The initialized runtime memory is too large to snapshot: {memoryLength:N0} bytes.");
        }

        return new RuntimeMemorySnapshot(exports.Memory.GetSpan(0, (int)memoryLength).ToArray());
    }

    public void RestoreTo(Memory memory)
    {
        var currentLength = memory.GetLength();
        if (currentLength < _memory.Length)
        {
            var missingBytes = _memory.Length - currentLength;
            var pagesToGrow = (missingBytes + WebAssemblyPageSize - 1) / WebAssemblyPageSize;
            memory.Grow(pagesToGrow);
        }

        _memory.CopyTo(memory.GetSpan(0, _memory.Length));
    }
}
