namespace PaperKnife.Core.Tests.Conformance;

internal static partial class CorpusCases
{
    // The permission flags of PermissionTests: printing allowed; changing, copying and annotating
    // forbidden; assembling (bit 11) allowed, which means something only from security handler
    // revision 3. Or every permission.
    private const int NoChangesNoCopying = -60;
    private const int AllPermissions = -4;

    /// <summary>Rotating, deleting, moving and appending pages (PageOperationTests).</summary>
    private static CorpusCase[] PageCases =>
    [
        new(
            "pages-rotate",
            "Rotating the first of two pages a quarter turn swaps its displayed width and height; the second page keeps its size.",
            "Page commands must change exactly the page asked for, and nothing else (v0.1.0).",
            () => TestPdfs.NumberedPages(2),
            [Rotate(0, 1)]),

        new(
            "pages-delete",
            "Deleting the first of three pages leaves pages 2 and 3, in order.",
            "Page commands must change exactly the page asked for, and nothing else (v0.1.0).",
            () => TestPdfs.NumberedPages(3),
            [Delete(0)]),

        new(
            "pages-delete-last-page-refused",
            "Deleting the only page of a document is refused.",
            "A PDF must keep at least one page: deleting the last one must be refused, not produce a broken file (v0.1.0).",
            () => TestPdfs.NumberedPages(1),
            [Delete(0)]),

        new(
            "pages-move",
            "Moving the third of three pages to the front gives pages 3, 1, 2.",
            "Page commands must put pages exactly where asked, keeping every page's text (v0.1.0).",
            () => TestPdfs.NumberedPages(3),
            [Move(2, 0)]),

        new(
            "pages-append",
            "Appending a one-page document to a two-page one adds its page at the end.",
            "Pages added from another PDF must come after the existing pages, in order (v0.1.0).",
            () => TestPdfs.NumberedPages(2),
            [Append("other")])
        {
            Attachments = new Dictionary<string, Func<PdfSnapshot>> { ["other"] = () => TestPdfs.NumberedPages(1) },
        },

        new(
            "pages-rotate-delete-move-append",
            "Rotating, deleting, moving and appending pages in one session, then undoing the last two commands.",
            "Page commands must keep every page's text and order exactly as asked, and undo must restore the earlier files exactly (v0.1.0).",
            () => TestPdfs.NumberedPages(3),
            [Rotate(1, 1), Delete(0), Move(1, 0), Append("other"), Undo(), Undo()])
        {
            Attachments = new Dictionary<string, Func<PdfSnapshot>> { ["other"] = () => TestPdfs.NumberedPages(1) },
        },
    ];

    /// <summary>Encrypted files and their authors' restrictions (PermissionTests).</summary>
    private static CorpusCase[] PermissionCases =>
    [
        new(
            "permissions-text-edits-refused",
            "An encrypted PDF whose author forbids changes and copying opens without a password, shows why its text can't be edited, and refuses a text edit.",
            "Author restrictions in encrypted files must be honored (v0.1.0).",
            () => EncryptedPdf.SinglePage(RawPdf.BaseContent, NoChangesNoCopying, revision: 3),
            [Replace("Hello World", "Hello PaperKnife")]),

        new(
            "permissions-rotate-revision-2",
            "Rotating a page of an encrypted PDF (security handler revision 2) that forbids changes is refused.",
            "The permission to assemble pages has a meaning only from revision 3; before it, rotating a page is one of the forbidden changes (v0.1.0).",
            () => EncryptedPdf.SinglePage(RawPdf.BaseContent, NoChangesNoCopying, revision: 2),
            [Rotate(0, 1)]),

        new(
            "permissions-rotate-revision-3",
            "Rotating a page of an encrypted PDF (security handler revision 3) that forbids changes but allows assembling applies, and its text stays uneditable.",
            "The permission to assemble pages must be honored from revision 3, and saving must keep the author's restrictions (v0.1.0).",
            () => EncryptedPdf.SinglePage(RawPdf.BaseContent, NoChangesNoCopying, revision: 3),
            [Rotate(0, 1)]),

        new(
            "permissions-append-copy-forbidden",
            "Appending the page of an encrypted PDF whose author forbids copying is refused.",
            "Pages must not be copied out of a document whose author forbids copying (v0.1.0).",
            () => TestPdfs.NumberedPages(1),
            [Append("restricted")])
        {
            Attachments = new Dictionary<string, Func<PdfSnapshot>>
            {
                ["restricted"] = () => EncryptedPdf.SinglePage(RawPdf.BaseContent, NoChangesNoCopying, revision: 3),
            },
        },

        new(
            "permissions-edit-stays-encrypted",
            "A text edit in an encrypted PDF that allows everything applies, and the file stays encrypted, so PaperKnife's own reader still can't read its content.",
            "Saving an edit must keep the file's encryption, not silently remove it (v0.1.0).",
            () => EncryptedPdf.SinglePage(RawPdf.BaseContent, AllPermissions, revision: 3),
            [Replace("Hello World", "Hello PaperKnife")]),
    ];
}
