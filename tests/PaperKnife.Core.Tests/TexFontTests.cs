using System.Diagnostics;
using System.Globalization;
using System.Text;
using PaperKnife.Core.Content;
using PaperKnife.Core.Fonts;
using PaperKnife.Core.Syntax;

namespace PaperKnife.Core.Tests;

/// <summary>Editing text in PDFs made with TeX, whose fonts have no space glyph and only the glyphs a document uses.</summary>
public sealed class TexFontTests
{
    [Fact]
    public void ReplaceText_WithASpace_LeavesAGapLikeTeX()
    {
        using var session = DocumentSession.FromSnapshot(TexPdf.Build());

        var result = session.ReplaceText(0, Find(session, "Hello world").ObjectIndex, "Hello old world");

        Assert.Equal(EditStatus.Applied, result.Status);
        Assert.Equal("Hello old world", result.TextRun?.Text);
        Assert.Empty(result.Details);
    }

    [Fact]
    public void ReplaceText_WithLettersTheSubsetLacks_DrawsThemWithACompleteCopyOfTheFont()
    {
        using var session = DocumentSession.FromSnapshot(TexPdf.Build());
        var original = session.CurrentSnapshot;
        var other = Find(session, "older");

        // "K", "i" and "t" have widths in the PDF already; "y" (121) is past /LastChar.
        var result = session.ReplaceText(0, Find(session, "Hello world").ObjectIndex, "Hello Kitty");

        Assert.Equal(EditStatus.Applied, result.Status);
        Assert.Equal("Hello Kitty", result.TextRun?.Text);
        Assert.Contains("CMR10", Assert.Single(result.Details), StringComparison.Ordinal);
        Assert.True(session.CurrentSnapshot.Span.StartsWith(original.Span));
        Assert.Equal(other.Bounds, Find(session, "older").Bounds);

        // The PDF's own font is untouched; the page has a complete copy for the new text.
        var fonts = Fonts(session);
        Assert.Equal(new TexSubset().Glyphs.Count + 1, fonts["F1"].Program.CharStrings.Count);
        var copy = Assert.Single(fonts, f => f.Key != "F1").Value;
        Assert.StartsWith("PK", copy.BaseFont, StringComparison.Ordinal);
        Assert.Equal(TexPdf.Full.CharStrings.Count, copy.Program.CharStrings.Count);

        // Another line that needs letters the subset lacks uses the same copy.
        var second = session.ReplaceText(0, Find(session, "older").ObjectIndex, "folder");
        Assert.Equal(EditStatus.Applied, second.Status);
        Assert.Equal("folder", second.TextRun?.Text);
        Assert.Empty(second.Details);
        Assert.Equal(2, Fonts(session).Count);
    }

    [Fact]
    public void ReplaceText_InALineOfSeveralTextObjects_KeepsTheLineTogether()
    {
        using var session = DocumentSession.FromSnapshot(TexPdf.Build("BT /F1 24 Tf 72 700 Td [(Hello)] TJ 62 0 Td [(w) 27.8 (orld)] TJ ET\n"));
        var line = Find(session, "Hello world");
        Assert.Equal(2, line.ObjectCount);

        // Only the second object needs the copy of the font; it still belongs to the line.
        var result = session.ReplaceText(0, line.ObjectIndex, "Hello Kitty");

        Assert.Equal(EditStatus.Applied, result.Status);
        Assert.Equal("Hello Kitty", result.TextRun?.Text);
        Assert.Equal(2, result.TextRun!.ObjectCount);
    }

    [Theory]
    [InlineData("a glyph differs")]
    [InlineData("a stray token among the glyphs")]
    [InlineData("a glyph defined twice")]
    [InlineData("a subroutine a glyph calls is a stub")]
    public void ReplaceText_WhenTheSubsetIsNotPartOfPaperKnifesFont_IsRejected(string difference)
    {
        var subset = difference switch
        {
            "a glyph differs" => new TexSubset { ReplaceGlyph = "o" },
            "a stray token among the glyphs" => new TexSubset { StrayToken = true },
            "a glyph defined twice" => new TexSubset { DuplicateGlyph = true },
            _ => new TexSubset { StubSubroutineOf = "d" },
        };
        using var session = DocumentSession.FromSnapshot(TexPdf.Build(subset: subset));

        var result = session.ReplaceText(0, Find(session, "Hello world").ObjectIndex, "Hello Kitty");

        Assert.Equal(EditRejection.UnsupportedCharacters, result.Rejection);
        Assert.Contains("can't use it here", result.Message, StringComparison.Ordinal);
        Assert.False(session.IsModified);
    }

    [Fact]
    public void ReplaceText_InANonsymbolicFont_DoesNotCopyIt()
    {
        // There PDFium and the PDF specification disagree on which glyph a code draws.
        using var session = DocumentSession.FromSnapshot(TexPdf.Build(flags: 32));

        var result = session.ReplaceText(0, Find(session, "Hello world").ObjectIndex, "Hello Kitty");

        Assert.Equal(EditRejection.UnsupportedCharacters, result.Rejection);
        Assert.Contains("can't add letters to yet", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReplaceText_InFontWithoutCharacterMap_KeepsLigaturesAndKerning()
    {
        // Without a ToUnicode map, PDFium reads codes through the program's own encoding, where
        // code 12 is the "fi" ligature; it reports the ligature as its two letters.
        var subset = new TexSubset { Glyphs = ["W", "d", "e", "fi", "l", "o", "r", "s"] };
        using var session = DocumentSession.FromSnapshot(TexPdf.Build("BT /F1 24 Tf 72 700 Td [(W) 80 (ord) -333 (\\014le)] TJ ET\n", subset));

        var result = session.ReplaceText(0, Find(session, "Word file").ObjectIndex, "Word files");

        Assert.Equal(EditStatus.Applied, result.Status);
        Assert.Contains("[<57> 80 <6F7264> -333 <0C6C6573>] TJ", PageContent(session), StringComparison.Ordinal);
    }

    [Fact]
    public void ReplaceText_WhenTheFontGivesTheLettersItLacksNoWidth_TakesTheirWidthsFromTheFont()
    {
        // Some tools write a width of 0 for the codes whose glyphs they leave out of a subset.
        using var tex = DocumentSession.FromSnapshot(TexPdf.Build());
        using var lua = DocumentSession.FromSnapshot(TexPdf.Build(zeroUnusedWidths: true));

        var expected = tex.ReplaceText(0, Find(tex, "Hello world").ObjectIndex, "Hello Kitty");
        var result = lua.ReplaceText(0, Find(lua, "Hello world").ObjectIndex, "Hello Kitty");

        Assert.Equal(EditStatus.Applied, expected.Status);
        Assert.Equal(EditStatus.Applied, result.Status);
        Assert.Equal(expected.TextRun!.Bounds.Width, result.TextRun!.Bounds.Width, 0.5);
    }

    [Theory]
    [InlineData(PdfFile.MaxObjectNumber - 2, true)]
    [InlineData(PdfFile.MaxObjectNumber - 1, false)]
    public void ReplaceText_AddsACopyOfTheFontOnlyWhenPdfiumCanReadItsObjects(int size, bool applies)
    {
        // The copy takes the next three object numbers, and PDFium reads none past its limit.
        using var session = DocumentSession.FromSnapshot(TexPdf.Build(size: size));

        var result = session.ReplaceText(0, Find(session, "Hello world").ObjectIndex, "Hello Kitty");

        if (applies)
        {
            Assert.Equal(EditStatus.Applied, result.Status);
            Assert.Equal("Hello Kitty", result.TextRun?.Text);
        }
        else
        {
            Assert.Equal(EditRejection.UnsupportedCharacters, result.Rejection);
            Assert.Contains("too many objects", result.Message, StringComparison.Ordinal);
            Assert.False(session.IsModified);
        }
    }

    [Fact]
    public void ReplaceText_OnAPageThatGivesAFontManyNames_ReadsTheFontOnce()
    {
        // Every name used to read the font's character map again.
        var names = Enumerable.Range(1, 1000).Select(i => $"N{i}").ToList();
        var content = TexPdf.Content + string.Concat(names.Select((name, i) =>
            string.Create(CultureInfo.InvariantCulture, $"BT /{name} 6 Tf {72 + (i % 40 * 12)} {600 - (i / 40 * 8)} Td (o) Tj ET\n")));
        using var session = DocumentSession.FromSnapshot(TexPdf.Build(content, aliases: names, toUnicode: LargeCharacterMap()));
        var watch = Stopwatch.StartNew();

        var result = session.ReplaceText(0, Find(session, "Hello world").ObjectIndex, "Hello Kitty");

        Assert.Equal(EditStatus.Applied, result.Status);
        Assert.InRange(watch.ElapsedMilliseconds, 0, 3000);
    }

    [Fact]
    public void Type1Font_ReadsAProgramFullOfJunkQuickly()
    {
        // Comments full of half-written keys made an earlier reader take time quadratic in their length.
        var (clear, encrypted, trailer) = TexPdf.Subset(new TexSubset { Junk = 2_000_000 });
        var data = Encoding.Latin1.GetBytes(clear + encrypted + trailer);
        var watch = Stopwatch.StartNew();

        var font = Type1Font.FromEmbedded(data, clear.Length, encrypted.Length);

        Assert.NotNull(font);
        Assert.Null(font.DifferenceFrom(TexPdf.Full));
        Assert.InRange(watch.ElapsedMilliseconds, 0, 3000);
    }

    [Fact]
    public void PdfiumRewrite_NamesTheSpaceTheFontLacks()
    {
        // PDFium's page rewrite can't leave gaps, so there a space needs a glyph.
        using var session = DocumentSession.FromSnapshot(TexPdf.Build());
        session.UseContentEditing = false;

        var result = session.ReplaceText(0, Find(session, "Hello world").ObjectIndex, "Hello old world");

        Assert.Equal(EditRejection.UnsupportedCharacters, result.Rejection);
        Assert.Equal([" "], result.UnsupportedCharacters);
        Assert.EndsWith("doesn't contain: space", result.Message, StringComparison.Ordinal);
    }

    /// <summary>The first page's fonts: each one's /BaseFont and embedded program.</summary>
    private static Dictionary<string, (string BaseFont, Type1Font Program)> Fonts(DocumentSession session)
    {
        var file = PdfFile.TryOpen(session.CurrentSnapshot.Memory, out _)!;
        var fonts = new Dictionary<string, (string, Type1Font)>(StringComparer.Ordinal);
        foreach (var (name, value) in file.ResolveDict(file.GetPages()![0].Resources!["Font"])!.Entries)
        {
            var font = file.ResolveDict(value)!;
            var program = (CosStream)file.Resolve(file.ResolveDict(font["FontDescriptor"])!["FontFile"])!;
            fonts[name] = (file.ResolveName(font["BaseFont"])!, Type1Font.FromEmbedded(
                file.DecodeStream(program, out _)!, (int)file.ResolveNumber(program.Dict["Length1"])!, (int)file.ResolveNumber(program.Dict["Length2"])!)!);
        }

        return fonts;
    }

    /// <summary>A ToUnicode CMap of 50,000 two-byte codes, which a simple font never shows: it only takes time to read.</summary>
    private static string LargeCharacterMap()
    {
        var map = new StringBuilder("/CIDInit /ProcSet findresource begin 12 dict begin begincmap /CMapName /Large def /CMapType 2 def\n");
        for (var block = 0; block < 500; block++)
        {
            map.Append("100 beginbfchar\n");
            for (var i = 0; i < 100; i++)
            {
                map.Append(CultureInfo.InvariantCulture, $"<{0x100 + (block * 100) + i:X4}> <4E00>\n");
            }

            map.Append("endbfchar\n");
        }

        return map.Append("endcmap CMapName currentdict /CMap defineresource pop end end").ToString();
    }

    private static string PageContent(DocumentSession session)
    {
        var file = PdfFile.TryOpen(session.CurrentSnapshot.Memory, out _)!;
        return Encoding.Latin1.GetString(ContentPage.TryLoad(file, file.GetPages()!, 0)!.Content);
    }

    private static PdfTextRun Find(DocumentSession session, string text) =>
        session.GetTextRuns(0).Single(r => Editing.TextEquivalence.AreEquivalent(r.Text, text));
}
