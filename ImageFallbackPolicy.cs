namespace ScreenGuide;

// The selected mode authorizes only this explicit set of outcomes, never arbitrary failure text.
internal static class ImageFallbackPolicy
{
    internal static ControlReadFailure? ForSelection(SelectionDecision decision)
    {
        if (decision.CanPoint || decision.Action == "done") return null;
        return decision.Failure switch
        {
            SelectionFailure.NotFound => ControlReadFailure.SelectionNotFound,
            SelectionFailure.Ambiguous => ControlReadFailure.SelectionAmbiguous,
            SelectionFailure.LowConfidence => ControlReadFailure.SelectionLowConfidence,
            _ => null
        };
    }
}
