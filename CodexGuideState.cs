using System.Diagnostics;

namespace ScreenGuide;

internal sealed record CodexGuideEvent(long Cursor, string Kind, string Message);

// Only a bounded event journal. The desktop owner keeps windows, controls and images elsewhere.
internal sealed class CodexGuideState
{
    private const int Capacity = 32;
    private readonly object _gate = new();
    private readonly Queue<CodexGuideEvent> _events = new(Capacity);
    private TaskCompletionSource _changed = NewSignal();
    private long _cursor;
    private bool _terminal;
    private CodexGuideEvent? _terminalEvent;

    internal CodexGuideState(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        Id = sessionId;
    }

    internal string Id { get; }
    internal long Cursor { get { lock (_gate) return _cursor; } }
    internal bool Terminal { get { lock (_gate) return _terminal; } }

    internal CodexGuideEvent Publish(string kind, string message = "")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentNullException.ThrowIfNull(message);
        TaskCompletionSource changed;
        CodexGuideEvent published;
        lock (_gate)
        {
            if (_terminal) throw new InvalidOperationException("The guide session has finished.");
            published = Append(kind, message);
            changed = _changed;
            _changed = NewSignal();
        }
        changed.TrySetResult();
        return published;
    }

    internal CodexGuideEvent Finish(string kind, string message = "")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentNullException.ThrowIfNull(message);
        TaskCompletionSource changed;
        CodexGuideEvent finished;
        lock (_gate)
        {
            if (_terminal) return _terminalEvent!;
            finished = Append(kind, message);
            _terminal = true;
            _terminalEvent = finished;
            changed = _changed;
            _changed = NewSignal();
        }
        changed.TrySetResult();
        return finished;
    }

    internal async Task<CodexGuideEvent?> WaitAsync(long after, int timeoutSeconds, CancellationToken cancel)
    {
        if (after < 0) throw new ArgumentOutOfRangeException(nameof(after));
        if (timeoutSeconds is < 1 or > 25) throw new ArgumentOutOfRangeException(nameof(timeoutSeconds));
        long started = Stopwatch.GetTimestamp();
        var timeout = TimeSpan.FromSeconds(timeoutSeconds);
        while (true)
        {
            cancel.ThrowIfCancellationRequested();
            Task changed;
            lock (_gate)
            {
                var next = NextEvent(after);
                if (next is not null) return next;
                if (_terminal) return null;
                // Reading the journal and capturing its signal happen under the same lock.
                changed = _changed.Task;
            }
            var remaining = timeout - Stopwatch.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero) return null;
            try { await changed.WaitAsync(remaining, cancel).ConfigureAwait(false); }
            catch (TimeoutException)
            {
                cancel.ThrowIfCancellationRequested();
                lock (_gate) return NextEvent(after);
            }
        }
    }

    // Both helpers run only while the caller holds _gate.
    private CodexGuideEvent Append(string kind, string message)
    {
        var item = new CodexGuideEvent(++_cursor, kind, message);
        if (_events.Count == Capacity) _events.Dequeue();
        _events.Enqueue(item);
        return item;
    }

    private CodexGuideEvent? NextEvent(long after)
    {
        foreach (var item in _events)
            if (item.Cursor > after) return item;
        return null;
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
