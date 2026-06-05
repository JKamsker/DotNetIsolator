using Wasmtime;

namespace DotNetIsolator;

internal sealed class RuntimeMemorySnapshot
{
    private const int WebAssemblyPageSize = 64 * 1024;
    private static readonly object StoreData = new();

    private readonly int _memoryLength;
    private readonly PageDelta[] _changedPages;

    private RuntimeMemorySnapshot(int memoryLength, PageDelta[] changedPages)
    {
        _memoryLength = memoryLength;
        _changedPages = changedPages;
    }

    public static RuntimeMemorySnapshot Create(IsolatedRuntimeHost host)
    {
        using var baselineStore = host.CreateStore(StoreData);
        var baselineInstance = host.Linker.Instantiate(baselineStore, host.Module);
        var baselineExports = IsolatedRuntimeExports.Bind(baselineInstance);
        var baselineMemory = baselineExports.Memory;

        using var initializedStore = host.CreateStore(StoreData);
        var initializedInstance = host.Linker.Instantiate(initializedStore, host.Module);
        var initializedExports = IsolatedRuntimeExports.Bind(initializedInstance);
        initializedExports.Start();

        var memoryLength = initializedExports.Memory.GetLength();
        if (memoryLength > int.MaxValue)
        {
            throw new InvalidOperationException(
                $"The initialized runtime memory is too large to snapshot: {memoryLength:N0} bytes.");
        }

        var baselineLength = baselineMemory.GetLength();
        if (baselineLength > int.MaxValue)
        {
            throw new InvalidOperationException(
                $"The baseline runtime memory is too large to snapshot: {baselineLength:N0} bytes.");
        }

        return new RuntimeMemorySnapshot(
            (int)memoryLength,
            CreatePageDeltas(
                baselineMemory,
                (int)baselineLength,
                initializedExports.Memory,
                (int)memoryLength));
    }

    public void RestoreTo(Memory memory)
    {
        var currentLength = memory.GetLength();
        if (currentLength < _memoryLength)
        {
            var missingBytes = _memoryLength - currentLength;
            var pagesToGrow = (missingBytes + WebAssemblyPageSize - 1) / WebAssemblyPageSize;
            memory.Grow(pagesToGrow);
        }

        foreach (var page in _changedPages)
        {
            page.Bytes.CopyTo(memory.GetSpan(page.Offset, page.Bytes.Length));
        }
    }

    private static PageDelta[] CreatePageDeltas(
        Memory baselineMemory,
        int baselineLength,
        Memory initializedMemory,
        int initializedLength)
    {
        var pageCount = (initializedLength + WebAssemblyPageSize - 1) / WebAssemblyPageSize;
        var changedPages = new List<PageDelta>();

        for (var pageIndex = 0; pageIndex < pageCount; pageIndex++)
        {
            var offset = pageIndex * WebAssemblyPageSize;
            var pageLength = Math.Min(WebAssemblyPageSize, initializedLength - offset);
            var initializedPage = initializedMemory.GetSpan(offset, pageLength);

            if (!PageChanged(baselineMemory, baselineLength, initializedPage, offset))
            {
                continue;
            }

            changedPages.Add(new PageDelta(offset, initializedPage.ToArray()));
        }

        return changedPages.ToArray();
    }

    private static bool PageChanged(
        Memory baselineMemory,
        int baselineLength,
        ReadOnlySpan<byte> initializedPage,
        int offset)
    {
        if (offset >= baselineLength)
        {
            return ContainsNonZero(initializedPage);
        }

        var baselinePageLength = Math.Min(initializedPage.Length, baselineLength - offset);
        var baselinePage = baselineMemory.GetSpan(offset, baselinePageLength);
        if (!initializedPage[..baselinePageLength].SequenceEqual(baselinePage))
        {
            return true;
        }

        return baselinePageLength != initializedPage.Length
            && ContainsNonZero(initializedPage[baselinePageLength..]);
    }

    private static bool ContainsNonZero(ReadOnlySpan<byte> value)
    {
        foreach (var item in value)
        {
            if (item != 0)
            {
                return true;
            }
        }

        return false;
    }

    private sealed record PageDelta(int Offset, byte[] Bytes);
}
