namespace PaperKnife.Core;

public enum EditStatus
{
    Applied,
    NoChange,
    Rejected,
}

public enum EditRejection
{
    None,
    InvalidTarget,
    UnsupportedCharacters,
    EditingRestricted,
    PageNotEditable,
    CollateralChange,
    EngineError,
}

/// <summary>The outcome of an edit. A rejected edit leaves the document exactly as it was.</summary>
public sealed class EditResult
{
    private EditResult(
        EditStatus status,
        EditRejection rejection,
        string message,
        IReadOnlyList<string> warnings,
        IReadOnlyList<string> unsupportedCharacters,
        PdfTextRun? textRun,
        IReadOnlyList<string>? details = null)
    {
        Status = status;
        Rejection = rejection;
        Message = message;
        Warnings = warnings;
        UnsupportedCharacters = unsupportedCharacters;
        TextRun = textRun;
        Details = details ?? [];
    }

    public EditStatus Status { get; }

    public EditRejection Rejection { get; }

    /// <summary>A user-facing explanation of what happened.</summary>
    public string Message { get; }

    public IReadOnlyList<string> Warnings { get; }

    /// <summary>For applied edits: more about what changed that isn't a warning, such as a font PaperKnife completed.</summary>
    public IReadOnlyList<string> Details { get; }

    /// <summary>For <see cref="EditRejection.UnsupportedCharacters"/>: the characters that can't be used.</summary>
    public IReadOnlyList<string> UnsupportedCharacters { get; }

    /// <summary>For applied text edits: the edited run as it exists in the committed document; null when the text was deleted.</summary>
    public PdfTextRun? TextRun { get; }

    internal static EditResult Applied(string message, IReadOnlyList<string>? warnings = null, PdfTextRun? textRun = null, IReadOnlyList<string>? details = null) =>
        new(EditStatus.Applied, EditRejection.None, message, warnings ?? [], [], textRun, details);

    internal static EditResult NoChange(string message) =>
        new(EditStatus.NoChange, EditRejection.None, message, [], [], null);

    internal static EditResult Rejected(EditRejection rejection, string message, IReadOnlyList<string>? unsupportedCharacters = null) =>
        new(EditStatus.Rejected, rejection, message, [], unsupportedCharacters ?? [], null);
}
