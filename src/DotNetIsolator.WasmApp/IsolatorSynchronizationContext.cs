namespace DotNetIsolator.WasmApp;

internal sealed class IsolatorSynchronizationContext : SynchronizationContext
{
    private static readonly IsolatorSynchronizationContext Instance = new();
    private readonly Queue<WorkItem> _queue = new();

    public static void Install()
    {
        if (SynchronizationContext.Current is not IsolatorSynchronizationContext)
        {
            SynchronizationContext.SetSynchronizationContext(Instance);
        }
    }

    public static void RunUntilCompleted(Task task)
    {
        Install();
        while (!task.IsCompleted)
        {
            if (!Instance.TryRunOne())
            {
                throw new InvalidOperationException(
                    "The async operation is incomplete, but the isolated scheduler has no queued continuations. "
                    + "Awaited operations that depend on timers, I/O, or ThreadPool work are not currently supported.");
            }
        }

        task.GetAwaiter().GetResult();
    }

    public override void Post(SendOrPostCallback callback, object? state)
        => _queue.Enqueue(new WorkItem(callback, state));

    public override void Send(SendOrPostCallback callback, object? state)
        => callback(state);

    private bool TryRunOne()
    {
        if (!_queue.TryDequeue(out var item))
        {
            return false;
        }

        item.Callback(item.State);
        return true;
    }

    private readonly struct WorkItem
    {
        public WorkItem(SendOrPostCallback callback, object? state)
        {
            Callback = callback;
            State = state;
        }

        public SendOrPostCallback Callback { get; }

        public object? State { get; }
    }
}
