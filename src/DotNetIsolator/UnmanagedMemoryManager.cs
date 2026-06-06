using System.Buffers;

namespace DotNetIsolator;

// Exposes a region of (stable, synchronously-accessed) guest linear memory as a
// ReadOnlyMemory<byte> without copying, so the host can deserialize directly from it. The pointer
// must stay valid for the manager's lifetime.
internal sealed unsafe class UnmanagedMemoryManager : MemoryManager<byte>
{
    private readonly byte* _pointer;
    private readonly int _length;

    public UnmanagedMemoryManager(byte* pointer, int length)
    {
        _pointer = pointer;
        _length = length;
    }

    public override Span<byte> GetSpan() => new(_pointer, _length);

    public override MemoryHandle Pin(int elementIndex = 0) => new(_pointer + elementIndex);

    public override void Unpin()
    {
    }

    protected override void Dispose(bool disposing)
    {
    }
}
