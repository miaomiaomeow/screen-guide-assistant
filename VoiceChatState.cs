namespace ScreenGuide;

internal enum VoiceChatPhase { Off, Waiting, Listening, Transcribing, Guiding }

// A generation belongs to one explicitly enabled conversation. Old callbacks cannot revive it.
internal sealed class VoiceChatState
{
    internal long Generation { get; private set; }
    internal nint Target { get; private set; }
    internal VoiceChatPhase Phase { get; private set; }
    internal bool Enabled => Phase != VoiceChatPhase.Off;

    internal long Start(nint target)
    {
        if (target == 0) throw new ArgumentOutOfRangeException(nameof(target));
        Target = target; Phase = VoiceChatPhase.Waiting;
        return ++Generation;
    }

    internal void Stop() { ++Generation; Target = 0; Phase = VoiceChatPhase.Off; }
    internal bool Current(long generation) => Enabled && generation == Generation;

    internal bool Move(long generation, VoiceChatPhase from, VoiceChatPhase to)
    {
        if (!Current(generation) || Phase != from || !Allowed(from, to)) return false;
        Phase = to; return true;
    }

    private static bool Allowed(VoiceChatPhase from, VoiceChatPhase to) => (from, to) is
        (VoiceChatPhase.Waiting, VoiceChatPhase.Listening) or
        (VoiceChatPhase.Listening, VoiceChatPhase.Transcribing) or
        (VoiceChatPhase.Transcribing, VoiceChatPhase.Guiding) or
        (VoiceChatPhase.Guiding, VoiceChatPhase.Waiting);
}
