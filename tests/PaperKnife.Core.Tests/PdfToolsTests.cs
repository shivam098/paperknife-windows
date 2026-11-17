namespace PaperKnife.Core.Tests;

public sealed class PdfToolsTests
{
    [Fact]
    public void Merge_CombinesDocumentsInOrder()
    {
        var merged = PdfTools.Merge([TestPdfs.NumberedPages(2), TestPdfs.NumberedPages(1)]);

        Assert.Equal(new[] { "Page 1", "Page 2", "Page 1" }, PageTexts(merged));
    }

    [Fact]
    public void ExtractPages_CopiesSelectedPagesInTheRequestedOrder()
    {
        var extracted = PdfTools.ExtractPages(TestPdfs.NumberedPages(3), [2, 0]);

        Assert.Equal(new[] { "Page 3", "Page 1" }, PageTexts(extracted));
    }

    [Fact]
    public void ExtractPages_RejectsPagesThatDoNotExist()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfTools.ExtractPages(TestPdfs.NumberedPages(1), [1]));
    }

    private static string[] PageTexts(PdfSnapshot snapshot)
    {
        using var session = DocumentSession.FromSnapshot(snapshot);
        return Enumerable.Range(0, session.PageCount).Select(i => session.GetTextRuns(i)[0].Text).ToArray();
    }
}
