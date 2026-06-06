using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Wasmtime;

namespace DotNetIsolator;

internal static class WasiPreview2PollHost
{
    private const string ClockModuleName = "wasi:clocks/monotonic-clock@0.2.0";
    private const string PollModuleName = "wasi:io/poll@0.2.0";
    private const string SubscribeDurationName = "subscribe-duration";
    private const string PollName = "poll";
    private const string DropPollableName = "[resource-drop]pollable";
    private static readonly ConditionalWeakTable<Store, WasiPollState> States = new();

    public static void DefineImports(Linker linker)
    {
        linker.DefineFunction(ClockModuleName, SubscribeDurationName, (CallerFunc<long, int>)SubscribeDuration);
        linker.DefineFunction(PollModuleName, PollName, (CallerAction<int, int, int>)Poll);
        linker.DefineFunction(PollModuleName, DropPollableName, (CallerAction<int>)DropPollable);
    }

    public static bool IsSupportedImport(FunctionImport import)
        => (import.ModuleName == ClockModuleName && import.Name == SubscribeDurationName)
            || (import.ModuleName == PollModuleName && import.Name is PollName or DropPollableName);

    private static int SubscribeDuration(Caller caller, long durationNanoseconds)
        => GetState(caller).SubscribeDuration(durationNanoseconds);

    private static void DropPollable(Caller caller, int handle)
        => GetState(caller).DropPollable(handle);

    private static void Poll(Caller caller, int pollablesPtr, int pollablesCount, int resultPtr)
        => GetState(caller).Poll(caller, pollablesPtr, pollablesCount, resultPtr);

    private static WasiPollState GetState(Caller caller)
        => States.GetValue(caller.Store, _ => new WasiPollState());

    private sealed class WasiPollState
    {
        private const int IndexSize = sizeof(uint);
        private const int StackIndexLimit = 64;
        private const long NanosecondsPerSecond = 1_000_000_000;
        private readonly Dictionary<int, long> _timers = new();
        private int _nextHandle = 1;
        private int _resultBufferPtr;
        private int _resultBufferCapacity;

        public int SubscribeDuration(long durationNanoseconds)
        {
            var handle = _nextHandle++;
            _timers.Add(handle, AddNanoseconds(Stopwatch.GetTimestamp(), durationNanoseconds));
            return handle;
        }

        public void DropPollable(int handle)
            => _timers.Remove(handle);

        public void Poll(Caller caller, int pollablesPtr, int pollablesCount, int resultPtr)
        {
            if (pollablesCount < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(pollablesCount));
            }

            var memory = caller.GetMemory("memory")
                ?? throw new InvalidOperationException("Caller lacks required export 'memory'");

            int[]? rentedHandles = null;
            int[]? rentedReady = null;
            Span<int> handles = pollablesCount <= StackIndexLimit
                ? stackalloc int[pollablesCount]
                : (rentedHandles = ArrayPool<int>.Shared.Rent(pollablesCount)).AsSpan(0, pollablesCount);
            Span<int> ready = pollablesCount <= StackIndexLimit
                ? stackalloc int[pollablesCount]
                : (rentedReady = ArrayPool<int>.Shared.Rent(pollablesCount)).AsSpan(0, pollablesCount);

            try
            {
                ReadPollableHandles(memory, pollablesPtr, handles);
                var readyCount = WaitForReady(handles, ready);
                WriteReadyIndices(caller, memory, ready[..readyCount], resultPtr);
            }
            finally
            {
                if (rentedHandles is not null)
                {
                    ArrayPool<int>.Shared.Return(rentedHandles);
                }

                if (rentedReady is not null)
                {
                    ArrayPool<int>.Shared.Return(rentedReady);
                }
            }
        }

        private int WaitForReady(ReadOnlySpan<int> handles, Span<int> ready)
        {
            while (true)
            {
                var now = Stopwatch.GetTimestamp();
                var readyCount = CollectReady(handles, ready, now);
                if (readyCount > 0 || handles.Length == 0)
                {
                    return readyCount;
                }

                Thread.Sleep(GetSleepMilliseconds(handles, now));
            }
        }

        private int CollectReady(ReadOnlySpan<int> handles, Span<int> ready, long now)
        {
            var readyCount = 0;
            for (var i = 0; i < handles.Length; i++)
            {
                if (IsReady(handles[i], now))
                {
                    ready[readyCount++] = i;
                }
            }

            return readyCount;
        }

        private bool IsReady(int handle, long now)
            => !_timers.TryGetValue(handle, out var dueTimestamp) || dueTimestamp <= now;

        private int GetSleepMilliseconds(ReadOnlySpan<int> handles, long now)
        {
            var earliestDueTimestamp = long.MaxValue;
            foreach (var handle in handles)
            {
                if (!_timers.TryGetValue(handle, out var dueTimestamp))
                {
                    return 0;
                }

                earliestDueTimestamp = Math.Min(earliestDueTimestamp, dueTimestamp);
            }

            var remainingTicks = earliestDueTimestamp - now;
            if (remainingTicks <= 0)
            {
                return 0;
            }

            var milliseconds = remainingTicks * 1000d / Stopwatch.Frequency;
            if (milliseconds >= int.MaxValue)
            {
                return int.MaxValue;
            }

            return Math.Max((int)Math.Ceiling(milliseconds), 1);
        }

        private void WriteReadyIndices(Caller caller, Memory memory, ReadOnlySpan<int> ready, int resultPtr)
        {
            if (ready.Length == 0)
            {
                memory.Write(resultPtr, 0);
                memory.Write(resultPtr + sizeof(int), 0);
                return;
            }

            EnsureResultBuffer(caller, checked(ready.Length * IndexSize));
            var destination = memory.GetSpan(_resultBufferPtr, ready.Length * IndexSize);
            for (var i = 0; i < ready.Length; i++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(destination[(i * IndexSize)..], (uint)ready[i]);
            }

            memory.Write(resultPtr, _resultBufferPtr);
            memory.Write(resultPtr + sizeof(int), ready.Length);
        }

        private void EnsureResultBuffer(Caller caller, int byteCount)
        {
            if (_resultBufferCapacity >= byteCount)
            {
                return;
            }

            var malloc = caller.GetFunction("malloc")
                ?? throw new InvalidOperationException("Caller lacks required export 'malloc'");
            _resultBufferPtr = malloc.WrapFunc<int, int>()!(byteCount);
            if (_resultBufferPtr == 0)
            {
                throw new InvalidOperationException($"malloc failed when trying to allocate {byteCount} bytes");
            }

            _resultBufferCapacity = byteCount;
        }

        private static void ReadPollableHandles(Memory memory, int pollablesPtr, Span<int> handles)
        {
            for (var i = 0; i < handles.Length; i++)
            {
                handles[i] = memory.ReadInt32(pollablesPtr + i * sizeof(int));
            }
        }

        private static long AddNanoseconds(long timestamp, long nanoseconds)
        {
            if (nanoseconds <= 0)
            {
                return timestamp;
            }

            var stopwatchTicks = (long)Math.Ceiling(nanoseconds * ((double)Stopwatch.Frequency / NanosecondsPerSecond));
            if (stopwatchTicks <= 0)
            {
                stopwatchTicks = 1;
            }

            return timestamp > long.MaxValue - stopwatchTicks
                ? long.MaxValue
                : timestamp + stopwatchTicks;
        }
    }
}
