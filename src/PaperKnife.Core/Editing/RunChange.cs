namespace PaperKnife.Core.Editing;

/// <summary>What an applied run edit did to the page's objects.</summary>
/// <param name="Removed">How many text objects were removed.</param>
/// <param name="ShiftX">How far the rest of the run moved, in page space.</param>
/// <param name="End">Where the new text ends along the run's baseline; <paramref name="TrimmedEnd"/> leaves out trailing spaces.</param>
/// <param name="HiddenBefore">Objects to hide in the original page to compare everything else.</param>
/// <param name="HiddenAfter">The same objects in the edited page.</param>
internal sealed record RunChange(
    TextRunLayout Layout,
    TextRunEdit Edit,
    int Removed,
    double ShiftX,
    double ShiftY,
    double End,
    double TrimmedEnd,
    IReadOnlyList<int> HiddenBefore,
    IReadOnlyList<int> HiddenAfter)
{
    public bool MovedRest => ShiftX != 0 || ShiftY != 0;
}
