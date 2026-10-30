using System.Windows;

namespace ScreenGuide;

// Only the current guide lives in memory. Old snapshots, coordinates and IDs never go to the model.
internal sealed record GuideInteraction(int Step, string Name, string Role, string Context, string Action, string Event);
internal sealed record GuideMemory(IReadOnlyList<GuideInteraction> Interactions);
internal sealed record GuidanceFrame(int Token, LocatedControl Control, ControlOption Option, string Action,
    IReadOnlySet<string> VisibleEntries)
{
    internal bool ExpectsChild => Action == "open_entry" || Option.Expandable;
    // Baseline for detecting unexposed hover menus in hybrid mode. It does not turn
    // a UIA-bound highlight into an image estimate or replace its live validation.
    internal PixelSnapshot? HoverImage { get; init; }
}

internal enum GuideMode { Controls, DirectImage, ImageOnControlFailure, Hybrid }
internal enum ControlReadFailure
{
    EmptyControls, ReadTimeout, ReadError, MenuNotUpdated, NoApplicationEntries,
    SelectionNotFound, SelectionAmbiguous, SelectionLowConfidence, RepeatedEntry
}

internal sealed class GuideSession(string question, nint window, bool manual, GuideMode mode = GuideMode.Controls)
{
    internal const int MaxInteractions = 12;
    private readonly List<GuideInteraction> _interactions = [];
    private readonly HashSet<int> _consumedTokens = [];
    internal string Question { get; } = question;
    internal nint Window { get; set; } = window;
    internal bool Manual { get; } = manual;
    internal GuideMode Mode { get; } = !manual && Enum.IsDefined(mode) ? mode : GuideMode.Controls;
    internal ControlReadFailure? ImageFallbackReason { get; private set; }
    internal bool Images => Mode is GuideMode.DirectImage or GuideMode.Hybrid || ImageFallbackReason != null;
    internal bool ReadsControls => Mode == GuideMode.Hybrid || !Images;
    internal bool Paused { get; set; }
    internal bool Complete { get; set; }
    internal int Count => _interactions.Count;
    internal GuideMemory Memory => new(_interactions.ToArray());
    internal bool TryActivateImageFallback(ControlReadFailure reason)
    {
        if (Mode != GuideMode.ImageOnControlFailure || Manual || Paused || Complete || Images ||
            Count >= MaxInteractions || !Enum.IsDefined(reason)) return false;
        ImageFallbackReason = reason;
        return true;
    }
    internal bool Record(GuidanceFrame frame, string interaction)
    {
        if (Paused || Complete || Count >= MaxInteractions ||
            (interaction is not ("clicked" or "hover_revealed") && !(Images && interaction == "hover_changed")) ||
            !_consumedTokens.Add(frame.Token)) return false;
        _interactions.Add(new(Count + 1, frame.Option.Name, frame.Option.Role, frame.Option.Context, frame.Action, interaction));
        return true;
    }
    internal bool RepeatsLast(ControlOption option) => _interactions.LastOrDefault() is { } last &&
        last.Name == option.Name && ((last.Role == option.Role && last.Context == option.Context) ||
            last.Role == "ImageEntry" || option.Role == "ImageEntry");
    internal static string EntryKey(ControlOption option) => option.Role + "\u001f" + option.Name + "\u001f" + option.Context;
    internal static HashSet<string> Entries(SelectionContext context) => context.Controls.Select(EntryKey).ToHashSet();
    internal static bool HasNewEntries(IReadOnlySet<string> before, SelectionContext after) =>
        after.Controls.Any(c => c.Enabled && !before.Contains(EntryKey(c)));
}

// A press plus release is one click, not two steps. Reject drags, outside releases and stale tokens.
internal sealed class PointerClickTracker
{
    private int? _token;
    private Point _down;
    private Rect _bounds;
    private long _time;
    internal bool Pressed => _token != null;
    internal void Down(int token, Rect bounds, Point point, long time)
    {
        Reset();
        if (!bounds.Contains(point)) return;
        _token = token; _down = point; _bounds = bounds; _time = time;
    }
    internal int? Up(Point point, long time)
    {
        var token = _token;
        bool clicked = token != null && time >= _time && time - _time <= 1500 && _bounds.Contains(point) &&
            Math.Abs(point.X - _down.X) <= 6 && Math.Abs(point.Y - _down.Y) <= 6;
        Reset(); return clicked ? token : null;
    }
    internal void Reset() => _token = null;
}
