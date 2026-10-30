using System.Windows;
using System.Windows.Automation;

namespace ScreenGuide;

// Only these small labels go to the model; no values, document text, HWNDs or coordinates.
internal sealed record ControlOption(string Id, string Name, string Role, string Context, bool Enabled, bool Expandable);
internal sealed record SelectionContext(string App, bool Partial, IReadOnlyList<ControlOption> Controls);
internal sealed record ControlCandidate(ControlOption Option, AutomationElement Element, Rect Bounds, nint HitWindow, int[] RuntimeId)
{
    internal bool IsWindowChrome { get; init; }
}
internal sealed record ControlSnapshot(nint Window, Rect WindowBounds, SelectionContext Context, IReadOnlyList<ControlCandidate> Candidates)
{
    internal int PopupCount { get; init; }
    internal int NodesRead { get; init; }
    internal int ElapsedMs { get; init; }
    internal string PartialReason { get; init; } = "none";
    internal int ApplicationCandidateCount => Candidates.Count(c => !c.IsWindowChrome);
    internal int WindowChromeCandidateCount => Candidates.Count(c => c.IsWindowChrome);
    internal IReadOnlyDictionary<string, int> RoleCounts { get; init; } = new Dictionary<string, int>();
    internal IReadOnlyDictionary<string, int> RejectedCounts { get; init; } = new Dictionary<string, int>();
}
internal sealed record LocatedControl(nint Window, Rect WindowBounds, AutomationElement? Element, Rect Bounds, nint HitWindow, int[] RuntimeId, string Name, string Role)
{
    // An image estimate is deliberately separate from a UIA element/boundary.
    internal VisualProof? ImageProof { get; init; }
}
internal sealed record LocateResult(LocatedControl? Control, string Message)
{
    internal bool ReadFailed { get; init; }
}
internal enum SelectionFailure
{
    None, NotFound, Ambiguous, LowConfidence, Disabled, InvalidCandidate, InvalidResponse,
    InvalidEntry, UnconfirmedDone, Configuration, InvalidQuestion, ServiceError,
    RequestTimeout, Cancelled, NoEnabledEntries
}

internal sealed record SelectionDecision(string Action, string? Id, double Confidence, string Message)
{
    internal SelectionFailure Failure { get; init; }
    internal bool CanPoint => Action is "point" or "open_entry" && Id != null;
    internal static SelectionDecision Refuse(string message, SelectionFailure failure = SelectionFailure.InvalidResponse) =>
        new("not_found", null, 0, message) { Failure = failure };
}
