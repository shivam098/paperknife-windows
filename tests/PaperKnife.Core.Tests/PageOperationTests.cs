namespace PaperKnife.Core.Tests;

public sealed class PageOperationTests
{
    [Fact]
    public void RotatePage_SwapsTheDisplayedSize()
    {
        using var session = DocumentSession.FromSnapshot(TestPdfs.NumberedPages(2));

        var result = session.RotatePage(0, 1);

        Assert.Equal(EditStatus.Applied, result.Status);
        Assert.Equal(new PageSize(792, 612), session.GetPageSize(0));
        Assert.Equal(new PageSize(612, 792), session.GetPageSize(1));
    }

    [Fact]
    public void DeletePage_RemovesOnlyThatPage()
    {
        using var session = DocumentSession.FromSnapshot(TestPdfs.NumberedPages(3));

        var result = session.DeletePage(0);

        Assert.Equal(EditStatus.Applied, result.Status);
        Assert.Equal(new[] { "Page 2", "Page 3" }, PageTexts(session));
    }

    [Fact]
    public void DeletePage_RefusesToDeleteTheLastPage()
    {
        using var session = DocumentSession.FromSnapshot(TestPdfs.NumberedPages(1));

        var result = session.DeletePage(0);

        Assert.Equal(EditRejection.InvalidTarget, result.Rejection);
        Assert.Equal(1, session.PageCount);
    }

    [Fact]
    public void MovePage_ReordersPages()
    {
        using var session = DocumentSession.FromSnapshot(TestPdfs.NumberedPages(3));

        var result = session.MovePage(2, 0);

        Assert.Equal(EditStatus.Applied, result.Status);
        Assert.Equal(new[] { "Page 3", "Page 1", "Page 2" }, PageTexts(session));
    }

    [Fact]
    public void AppendDocument_AddsPagesAtTheEnd()
    {
        using var session = DocumentSession.FromSnapshot(TestPdfs.NumberedPages(2));

        var result = session.AppendDocument(TestPdfs.NumberedPages(1));

        Assert.Equal(EditStatus.Applied, result.Status);
        Assert.Equal(new[] { "Page 1", "Page 2", "Page 1" }, PageTexts(session));
    }

    [Fact]
    public void SaveAs_WritesTheVerifiedSnapshotAndLeavesNoTemporaryFiles()
    {
        var folder = Directory.CreateTempSubdirectory("paperknife-tests-");
        try
        {
            using var session = DocumentSession.FromSnapshot(TestPdfs.NumberedPages(2));
            session.RotatePage(1, 1);
            var path = Path.Combine(folder.FullName, "out.pdf");

            session.SaveAs(path);

            Assert.False(session.IsModified);
            Assert.Equal(path, session.FilePath);
            Assert.True(File.ReadAllBytes(path).AsSpan().SequenceEqual(session.CurrentSnapshot.Span));
            Assert.Equal(new[] { "out.pdf" }, folder.GetFiles().Select(f => f.Name));
            using var reopened = DocumentSession.Open(path);
            Assert.Equal(new PageSize(792, 612), reopened.GetPageSize(1));
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    private static string[] PageTexts(DocumentSession session) =>
        Enumerable.Range(0, session.PageCount).Select(i => session.GetTextRuns(i)[0].Text).ToArray();
}
