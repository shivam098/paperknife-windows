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

    [Theory]
    [InlineData("LMRoman10-Regular")] // Latin Modern, with \usepackage{lmodern}
    [InlineData("SFRM1000")] // CM-Super, with \usepackage[T1]{fontenc}
    public void ReplaceText_InOtherLaTeXFonts_DrawsNewLettersWithACompleteCopy(string font)
    {
        // pdfTeX describes these fonts with /Differences for the codes the document uses; new
        // letters take their codes from the font's own encoding.
        using var session = DocumentSession.FromSnapshot(TexPdf.Build(subset: new TexSubset { Font = font, WithDifferences = true }));

        var result = session.ReplaceText(0, Find(session, "Hello world").ObjectIndex, "Hello Kitty");

        Assert.Equal(EditStatus.Applied, result.Status);
        Assert.Equal("Hello Kitty", result.TextRun?.Text);
        Assert.Contains(font, Assert.Single(result.Details), StringComparison.Ordinal);
        var copy = Assert.Single(Fonts(session), f => f.Key != "F1").Value;
        Assert.Equal(TexPdf.Font(font).CharStrings.Count, copy.Program.CharStrings.Count);
    }

    [Theory]
    [InlineData("LMRoman10-Regular", true)] // Latin Modern's own encoding has é where T1 has it
    [InlineData("SFRM1000", false)] // CM-Super's is StandardEncoding, which has no é
    public void ReplaceText_WithAnAccentedLetterThePdfNeverUses_NeedsItInTheFontsOwnEncoding(string font, bool applies)
    {
        using var session = DocumentSession.FromSnapshot(TexPdf.Build(subset: new TexSubset { Font = font, WithDifferences = true }));

        var result = session.ReplaceText(0, Find(session, "Hello world").ObjectIndex, "Hellé world");

        if (applies)
        {
            Assert.Equal(EditStatus.Applied, result.Status);
            Assert.Equal("Hellé world", result.TextRun?.Text);
        }
        else
        {
            Assert.Equal(EditRejection.UnsupportedCharacters, result.Rejection);
            Assert.Equal(["é"], result.UnsupportedCharacters);
            Assert.False(session.IsModified);
        }
    }

    [Theory]
    [InlineData("LMRoman10-Regular")]
    [InlineData("SFRM1000")]
    public void ReplaceText_WithLettersOfTeXsEncodingThePdfNeverUses_DrawsWhatTheCharacterMapSays(string font)
    {
        // pdfTeX's character map and widths follow TeX's encoding (T1 here) for every code, but its
        // /Differences name only the codes the document uses. At the others the full font's own
        // encoding has other glyphs, or none: Latin Modern's has © at 169 (ľ in T1) and Ć at 198
        // (Æ), CM-Super's (StandardEncoding) Ø at 233 (é).
        var subset = new TexSubset { Font = font, WithDifferences = true };
        var full = TexPdf.Font(font);
        var pdf = TexPdf.Build(
            subset: subset,
            toUnicode: TexPdf.CharacterMap(subset, (169, "ľ"), (198, "Æ"), (233, "é")),
            widths: new Dictionary<int, int> { [169] = (int)Math.Round(full.Width("lcaron")!.Value), [198] = 900 });
        using var session = DocumentSession.FromSnapshot(pdf);

        var result = session.ReplaceText(0, Find(session, "Hello world").ObjectIndex, "Hellé wÆrľd");

        Assert.Equal(EditStatus.Applied, result.Status);
        Assert.Equal("Hellé wÆrľd", result.TextRun?.Text);
        var (glyphs, widths) = Copy(session);
        Assert.Equal("lcaron", glyphs[169]);
        Assert.Equal("AE", glyphs[198]);
        Assert.Equal("eacute", glyphs.GetValueOrDefault(233) ?? full.Encoding.GetValueOrDefault(233));

        // TeX's width for Æ, as the PDF gives it; é is past /LastChar, so it takes the font's.
        Assert.Equal(900, widths[198]);
        Assert.Equal((int)Math.Round(full.Width("eacute")!.Value), widths[233]);

        // Another line that needs the copy uses the same one.
        var second = session.ReplaceText(0, Find(session, "older").ObjectIndex, "ľder");
        Assert.Equal(EditStatus.Applied, second.Status);
        Assert.Equal("ľder", second.TextRun?.Text);
        Assert.Equal(2, Fonts(session).Count);
    }

    [Fact]
    public void ReplaceText_WithALetterAtACodeWhoseWidthIsTeXsForAnotherGlyph_TakesItsOwnWidth()
    {
        // Without a character map to say which glyph TeX's width at 198 is for (Æ in T1), the copy
        // gives Ć, the glyph Latin Modern's own encoding has there, its own width.
        var subset = new TexSubset { Font = "LMRoman10-Regular", WithDifferences = true };
        var full = TexPdf.Font(subset.Font);
        using var session = DocumentSession.FromSnapshot(TexPdf.Build(subset: subset, widths: new Dictionary<int, int> { [198] = 903 }));

        var result = session.ReplaceText(0, Find(session, "Hello world").ObjectIndex, "Hello Ćorld");

        Assert.Equal(EditStatus.Applied, result.Status);
        Assert.Equal("Hello Ćorld", result.TextRun?.Text);
        Assert.Equal((int)Math.Round(full.Width("Cacute")!.Value), Copy(session).Widths[198]);
    }

    [Fact]
    public void ReplaceText_WithLettersOfAnotherFontSharingTheSubset_GivesThemTheirWidths()
    {
        // pdfTeX embeds one subset for the fonts that use a program in several encodings. This
        // font uses only H and o; it gives the other codes in its range a width of 0, though the
        // subset has the glyphs the program's encoding has there, for another font.
        const string Content = "BT /F1 24 Tf 72 700 Td (Ho) Tj ET\n";
        var subset = new TexSubset { Font = "LMRoman10-Regular", WithDifferences = true, Described = ["H", "o"] };
        using var shared = DocumentSession.FromSnapshot(TexPdf.Build(Content, subset));
        using var own = DocumentSession.FromSnapshot(TexPdf.Build(Content, subset with { Described = null }));

        var result = shared.ReplaceText(0, Find(shared, "Ho").ObjectIndex, "Hold");
        var expected = own.ReplaceText(0, Find(own, "Ho").ObjectIndex, "Hold");

        Assert.Equal(EditStatus.Applied, result.Status);
        Assert.Equal("Hold", result.TextRun?.Text);
        Assert.Single(result.Details);
        Assert.Equal(EditStatus.Applied, expected.Status);
        Assert.Equal(expected.TextRun!.Bounds.Width, result.TextRun!.Bounds.Width, 0.5);
    }

    [Fact]
    public void ReplaceText_KeepsTheGlyphOfEveryCodeThePdfDraws_WhateverItsCharacterMapSays()
    {
        // The letters an edit keeps are drawn with the copy, with the codes they had. So each code
        // the PDF's font draws keeps its glyph in the copy, even where the character map says the
        // code stands for another character.
        var subset = new TexSubset();
        using var session = DocumentSession.FromSnapshot(TexPdf.Build(subset: subset, toUnicode: TexPdf.CharacterMap(subset, ('e', "o"))));

        var result = session.ReplaceText(0, Find(session, "Hollo world").ObjectIndex, "Hollo Kitty");

        Assert.Equal(EditStatus.Applied, result.Status);
        Assert.Equal("Hollo Kitty", result.TextRun?.Text);
        Assert.DoesNotContain('e', Copy(session).Glyphs.Keys);
    }

    [Fact]
    public void ReplaceText_WithALetterAtACodeTheSubsetHasAnotherGlyphFor_DrawsItWithTheCopy()
    {
        // pdfTeX embeds one subset for the fonts of a program, so this one has © for another font
        // (a symbol font), at 169 in Latin Modern's own encoding. This font doesn't use 169, which
        // T1 and the character map give ľ, but PDFium draws © for it from the subset.
        var subset = new TexSubset
        {
            Font = "LMRoman10-Regular",
            WithDifferences = true,
            Glyphs = ["H", "d", "e", "l", "o", "r", "w", "copyright"],
            Described = ["H", "d", "e", "l", "o", "r", "w"],
        };
        var lcaron = (int)Math.Round(TexPdf.Font(subset.Font).Width("lcaron")!.Value);
        var pdf = TexPdf.Build(subset: subset, toUnicode: TexPdf.CharacterMap(subset, (169, "ľ")), widths: new Dictionary<int, int> { [169] = lcaron });
        using var session = DocumentSession.FromSnapshot(pdf);

        var result = session.ReplaceText(0, Find(session, "Hello world").ObjectIndex, "Hello worľd");

        Assert.Equal(EditStatus.Applied, result.Status);
        Assert.Equal("Hello worľd", result.TextRun?.Text);
        Assert.Single(result.Details);
        Assert.Equal("lcaron", Copy(session).Glyphs[169]);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)] // PDFium rewrites the page, as in encrypted PDFs: no copy, and no new codes
    [InlineData(true, false)] // a pdfTeX font PaperKnife doesn't ship, such as Times
    [InlineData(false, false)]
    public void ReplaceText_WithALetterWhoseCodeDrawsAQuotationMark_NeverDrawsTheQuotationMark(bool contentEditing, bool shipped)
    {
        // CM-Super's own encoding (StandardEncoding) has “ at 170, where T1 and the character map
        // have ł. A document that uses “ (at T1's 16) has it in its subset, so PDFium draws “ for 170.
        var subset = new TexSubset
        {
            Font = "SFRM1000",
            Rename = shipped ? null : "SFZZ1000",
            WithDifferences = true,
            Glyphs = ["H", "e", "l", "o", "quotedblleft"],
            Described = ["H", "e", "l", "o"],
        };
        var lslash = (int)Math.Round(TexPdf.Font(subset.Font).Width("lslash")!.Value);
        var pdf = TexPdf.Build("BT /F1 24 Tf 72 700 Td (Hello) Tj ET\n", subset, toUnicode: TexPdf.CharacterMap(subset, (170, "ł")), widths: new Dictionary<int, int> { [170] = lslash });
        using var session = DocumentSession.FromSnapshot(pdf);
        session.UseContentEditing = contentEditing;

        var result = session.ReplaceText(0, Find(session, "Hello").ObjectIndex, "Hełlo");

        if (contentEditing && shipped)
        {
            Assert.Equal(EditStatus.Applied, result.Status);
            Assert.Equal("Hełlo", result.TextRun?.Text);
            Assert.Equal("lslash", Copy(session).Glyphs[170]);
            return;
        }

        Assert.Equal(contentEditing ? EditRejection.UnsupportedCharacters : EditRejection.PageNotEditable, result.Rejection);
        Assert.False(session.IsModified);
    }

    [Theory]
    [InlineData("HeΔlo", false)] // drawn with the PDF's font
    [InlineData("HeΔly", true)] // y needs the copy, which draws Δ too
    public void ReplaceText_WithALetterTheCharacterMapNamesOtherwiseThanTheGlyphList_TrustsThePdf(string newText, bool copies)
    {
        // The cmap package maps Computer Modern's Delta to U+0394; the Adobe Glyph List says U+2206
        // (increment). The PDF names the glyph and the page draws it, so its own word stands.
        var subset = new TexSubset { Glyphs = ["H", "e", "l", "o", "Delta"] };
        const string Content = "BT /F1 24 Tf 72 700 Td (Hello) Tj ET\nBT /F1 24 Tf 72 650 Td (\\001) Tj ET\n";
        using var session = DocumentSession.FromSnapshot(TexPdf.Build(Content, subset, toUnicode: TexPdf.CharacterMap(subset, (1, "\u0394"))));

        var result = session.ReplaceText(0, Find(session, "Hello").ObjectIndex, newText);

        Assert.Equal(EditStatus.Applied, result.Status);
        Assert.Equal(newText, result.TextRun?.Text);
        Assert.Equal(copies, result.Details.Count > 0);
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("old", false)]
    [InlineData("o", true)] // removes the objects that drew "lder", encoding nothing
    public void PdfiumRewrite_InATeXFont_OnlyRemovesText(string newText, bool applies)
    {
        // PDFium's page rewrite encodes the edited object's text again, and in TeX's fonts
        // PaperKnife can't check the codes it picks. Removing objects encodes nothing.
        using var session = DocumentSession.FromSnapshot(TexPdf.Build("BT /F1 24 Tf 72 700 Td [(Hello)] TJ ET\nBT /F1 12 Tf 72 660 Td (o) Tj (lder) Tj ET\n"));
        session.UseContentEditing = false;

        var result = session.ReplaceText(0, Find(session, "older").ObjectIndex, newText);

        if (applies)
        {
            Assert.Equal(EditStatus.Applied, result.Status);
            Assert.DoesNotContain(session.GetTextRuns(0), r => r.Text == "older");
        }
        else
        {
            Assert.Equal(EditRejection.PageNotEditable, result.Rejection);
            Assert.False(session.IsModified);
        }
    }

    [Fact]
    public void ReplaceText_WithALetterTheFontHasAtTwoCodes_UsesTheOneThePdfNames()
    {
        // This PDF has no character map, and its font is one PaperKnife doesn't ship. ß is 255 in
        // its /Differences (T1), and 251 in its program's own encoding (StandardEncoding), where
        // pdfTeX gives the width of T1's û. The edit draws ß at 255, with ß's width.
        var subset = new TexSubset
        {
            Font = "SFRM1000",
            Rename = "SFZZ1000",
            WithDifferences = true,
            Glyphs = ["H", "e", "l", "o", "germandbls"],
            PdfCodes = new Dictionary<string, int> { ["germandbls"] = 255 },
        };
        var ucircumflex = (int)Math.Round(TexPdf.Font(subset.Font).Width("ucircumflex")!.Value);
        const string Content = "BT /F1 24 Tf 72 700 Td (Hello) Tj ET\nBT /F1 24 Tf 72 650 Td (\\377) Tj ET\n";
        using var session = DocumentSession.FromSnapshot(TexPdf.Build(Content, subset, widths: new Dictionary<int, int> { [251] = ucircumflex }));

        var result = session.ReplaceText(0, Find(session, "Hello").ObjectIndex, "Helßo");

        Assert.Equal(EditStatus.Applied, result.Status);
        Assert.Equal("Helßo", result.TextRun?.Text);
        Assert.Contains("<48656CFF6F>", PageContent(session), StringComparison.Ordinal);
    }

    [Fact]
    public void ReplaceText_WithACharacterTheFontHasOnlyALookalikeOf_DrawsThePdfsOwnGlyph()
    {
        // pdfTeX's character map for TeX's math italic font says code 15 is ϵ, which is its glyph
        // epsilon1, a name the Adobe Glyph List doesn't have. The font has no /Differences, so the
        // copy draws the program's own glyph there, as the PDF's font would, not a look-alike such
        // as epsilon (ε).
        var subset = new TexSubset { Font = "CMMI10", Glyphs = ["x", "y"] };
        var map = TexPdf.CharacterMap(subset, (15, "ϵ"));
        using var session = DocumentSession.FromSnapshot(TexPdf.Build("BT /F1 24 Tf 72 700 Td (xy) Tj ET\n", subset, toUnicode: map));

        var result = session.ReplaceText(0, Find(session, "xy").ObjectIndex, "ϵxy");

        Assert.Equal(EditStatus.Applied, result.Status);
        Assert.Equal("ϵxy", result.TextRun?.Text);
        Assert.DoesNotContain(15, Copy(session).Glyphs.Keys);
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

    /// <summary>The copy of the font an edit added: the glyphs its /Differences name, and its width for each code.</summary>
    private static (Dictionary<int, string> Glyphs, Dictionary<int, int> Widths) Copy(DocumentSession session)
    {
        var file = PdfFile.TryOpen(session.CurrentSnapshot.Memory, out _)!;
        var fonts = file.ResolveDict(file.GetPages()![0].Resources!["Font"])!;
        var copy = file.ResolveDict(Assert.Single(fonts.Entries, f => f.Key != "F1").Value)!;
        var glyphs = new Dictionary<int, string>();
        var next = 0;
        foreach (var item in file.ResolveArray(file.ResolveDict(copy["Encoding"])?["Differences"])?.Items ?? [])
        {
            switch (file.Resolve(item))
            {
                case CosNumber number:
                    next = (int)number.Value;
                    break;
                case CosName name:
                    glyphs[next++] = name.Value;
                    break;
            }
        }

        var first = (int)file.ResolveNumber(copy["FirstChar"])!;
        var widths = file.ResolveArray(copy["Widths"])!.Items.Select((w, i) => (Code: first + i, Width: (int)file.ResolveNumber(w)!))
            .ToDictionary(w => w.Code, w => w.Width);
        return (glyphs, widths);
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
    internal static string LargeCharacterMap()
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
