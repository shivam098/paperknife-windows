namespace PaperKnife.Core.Tests.Conformance;

/// <summary>
/// The cases of the conformance corpus. Every test of this project whose scenario is a document
/// session has a case with the same document and the same steps: theories one case per row, and
/// tests that compare two sessions one case per session. A few cases keep scenarios of their own.
/// Each case names the shortcoming it guards against, so the other apps can't bring it back
/// unnoticed. Documents are generated here, as in the other tests, so the corpus contains no
/// document PaperKnife doesn't own. The cases are split by area, one part of this class each.
/// </summary>
internal static partial class CorpusCases
{
    public static IReadOnlyList<CorpusCase> All { get; } =
    [
        .. DocumentCases,
        .. EditCases,
        .. TrueTypeCases,
        .. PageCases,
        .. PermissionCases,
        .. RunCases,
        .. ContentCases,
        .. PdfiumCases,
        .. TexCases,
    ];

    private static ReplaceTextStep Replace(string run, string text, int page = 0, bool contentEditing = true) => new(page, run, text, contentEditing);

    private static ReplaceObjectStep ReplaceObject(int objectIndex, string text, int page = 0) => new(page, objectIndex, text);

    private static RotatePageStep Rotate(int page, int quarterTurns) => new(page, quarterTurns);

    private static DeletePageStep Delete(int page) => new(page);

    private static MovePageStep Move(int from, int to) => new(from, to);

    private static AppendDocumentStep Append(string document, string? password = null) => new(document, password);

    private static UndoStep Undo() => new();

    private static RedoStep Redo() => new();
}
