namespace ScreenGuide;

// Structural ancestry only: no labels, document values, coordinates or app names.
internal readonly record struct LocatorScope(bool InTitleBar, bool InDocument, bool InEdit);

internal static class LocatorPolicy
{
    internal static LocatorScope Enter(LocatorScope parent, string role) => new(
        parent.InTitleBar || role == "TitleBar",
        parent.InDocument || role == "Document",
        parent.InEdit || role == "Edit");

    internal static bool IsCandidateRole(string role) => role is "Button" or "MenuItem" or
        "CheckBox" or "RadioButton" or "ComboBox" or "Hyperlink" or "SplitButton" or "TreeItem";

    internal static string? CandidateRejection(string role, LocatorScope scope, bool expandable)
    {
        if (!IsCandidateRole(role)) return "non-entry-role";
        if (role != "TreeItem") return null;
        if (scope.InDocument || scope.InEdit) return "tree-in-document";
        return expandable ? null : "tree-not-expandable";
    }
}
