using System.Text;

namespace PaperKnife.Core.Tests;

public sealed class PermissionTests
{
    // Printing allowed; changing, copying and annotating forbidden. Bit 11 ("assemble") is set,
    // which only has a meaning from security handler revision 3.
    private const int NoChangesNoCopying = -60;
    private const int AllPermissions = -4;

    [Fact]
    public void ProtectedDocument_OpensWithoutPasswordAndRefusesTextEdits()
    {
        using var session = DocumentSession.FromSnapshot(EncryptedPdf.SinglePage(RawPdf.BaseContent, NoChangesNoCopying, revision: 3));
        var before = session.CurrentSnapshot;
        var hello = session.GetTextRuns(0)[0];

        var result = session.ReplaceText(0, hello.ObjectIndex, "Hello PaperKnife");

        Assert.Equal("Hello World", hello.Text);
        Assert.Equal(EditRejection.EditingRestricted, result.Rejection);
        Assert.Same(before, session.CurrentSnapshot);
        Assert.NotEmpty(session.GetTextEditBlockers(0));
    }

    [Theory]
    [InlineData(2, EditStatus.Rejected)]
    [InlineData(3, EditStatus.Applied)]
    public void RotatePage_HonorsTheAssemblePermissionOnlyFromRevision3(int revision, EditStatus expected)
    {
        using var session = DocumentSession.FromSnapshot(EncryptedPdf.SinglePage(RawPdf.BaseContent, NoChangesNoCopying, revision));

        var result = session.RotatePage(0, 1);

        Assert.Equal(expected, result.Status);
        Assert.NotEmpty(session.GetTextEditBlockers(0)); // saving keeps the author's restrictions
    }

    [Fact]
    public void AppendDocument_RefusesPagesFromADocumentThatForbidsCopying()
    {
        using var session = DocumentSession.FromSnapshot(TestPdfs.NumberedPages(1));
        var before = session.CurrentSnapshot;

        var result = session.AppendDocument(EncryptedPdf.SinglePage(RawPdf.BaseContent, NoChangesNoCopying, revision: 3));

        Assert.Equal(EditRejection.EditingRestricted, result.Rejection);
        Assert.Same(before, session.CurrentSnapshot);
    }

    [Fact]
    public void PdfTools_RefuseToCopyPagesFromADocumentThatForbidsCopying()
    {
        var restricted = EncryptedPdf.SinglePage(RawPdf.BaseContent, NoChangesNoCopying, revision: 3);

        var extract = Assert.Throws<PdfException>(() => PdfTools.ExtractPages(restricted, [0]));
        var merge = Assert.Throws<PdfException>(() => PdfTools.Merge([TestPdfs.NumberedPages(1), restricted]));

        Assert.Equal(PdfErrorCode.Restricted, extract.ErrorCode);
        Assert.Equal(PdfErrorCode.Restricted, merge.ErrorCode);
    }

    [Fact]
    public void ProtectedDocumentWithFullPermissions_StaysEncryptedAfterAnEdit()
    {
        using var session = DocumentSession.FromSnapshot(EncryptedPdf.SinglePage(RawPdf.BaseContent, AllPermissions, revision: 3));

        var result = session.ReplaceText(0, session.GetTextRuns(0)[0].ObjectIndex, "Hello PaperKnife");

        Assert.Equal(EditStatus.Applied, result.Status);
        Assert.Contains("/Encrypt", Encoding.Latin1.GetString(session.CurrentSnapshot.Span), StringComparison.Ordinal);
    }
}
