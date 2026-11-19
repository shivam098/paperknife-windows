using System.Globalization;
using PaperKnife.Core.Syntax;

namespace PaperKnife.Core.Tests.Conformance;

internal static partial class CorpusCases
{
    /// <summary>LaTeX PDFs: subsets of TeX's Type 1 fonts, without a space glyph (TexFontTests).</summary>
    private static CorpusCase[] TexCases =>
    [
        new(
            "tex-space-as-gap",
            "Typing a space in a LaTeX PDF (Computer Modern, which has no space glyph) leaves a gap, as TeX does, without needing a copy of the font.",
            "TeX fonts have no space glyph, so every edit with a space was refused; spaces are drawn as TJ gaps (v0.3.0).",
            () => TexPdf.Build(),
            [Replace("Hello world", "Hello old world")]),

        new(
            "tex-new-letters-cmr10",
            "Typing letters a LaTeX PDF's Computer Modern subset lacks, on two lines.",
            "LaTeX PDFs contain only the letters they use, so new letters were refused (v0.3.0); the second edit reuses the font copy the first added.",
            () => TexPdf.Build(),
            [Replace("Hello world", "Hello Kitty"), Replace("older", "folder")]),

        new(
            "tex-new-letters-latin-modern",
            "Typing letters a Latin Modern subset (\\usepackage{lmodern}) lacks draws them with a complete copy of the font.",
            "New letters in LaTeX PDFs set in Latin Modern were refused; PaperKnife now adds a copy of the identical font (v0.5.0).",
            () => TexPdf.Build(subset: new TexSubset { Font = "LMRoman10-Regular", WithDifferences = true }),
            [Replace("Hello world", "Hello Kitty")]),

        new(
            "tex-new-letters-cm-super",
            "Typing letters a CM-Super subset (\\usepackage[T1]{fontenc}) lacks draws them with a complete copy of the font.",
            "New letters in LaTeX PDFs set in CM-Super were refused; PaperKnife now adds a copy of the identical font (v0.5.0).",
            () => TexPdf.Build(subset: new TexSubset { Font = "SFRM1000", WithDifferences = true }),
            [Replace("Hello world", "Hello Kitty")]),

        new(
            "tex-accented-letter-latin-modern",
            "Typing é, which the PDF never uses, in Latin Modern applies: the font's own encoding has é where T1 has it.",
            "Letters the PDF's /Differences don't name take their codes from the font's own encoding (v0.5.0).",
            () => TexPdf.Build(subset: new TexSubset { Font = "LMRoman10-Regular", WithDifferences = true }),
            [Replace("Hello world", "Hellé world")]),

        new(
            "tex-accented-letter-cm-super",
            "Typing é, which the PDF never uses, in CM-Super is refused, naming é: the font's own encoding (StandardEncoding) has no é.",
            "A letter no code reaches must be refused, not drawn with whatever glyph another code has (v0.5.0).",
            () => TexPdf.Build(subset: new TexSubset { Font = "SFRM1000", WithDifferences = true }),
            [Replace("Hello world", "Hellé world")]),

        new(
            "tex-t1-letters-latin-modern",
            "Typing T1 letters the PDF never uses (é, Æ, ľ) at codes where Latin Modern's own encoding has other glyphs draws what the character map says, on two lines sharing one copy.",
            "Letters of TeX's encoding (T1) were taken from the font's own encoding, which has other glyphs or none at those codes; the character map now decides (v0.5.0).",
            () => TexEncodingLetters("LMRoman10-Regular"),
            [Replace("Hello world", "Hellé wÆrľd"), Replace("older", "ľder")]),

        new(
            "tex-t1-letters-cm-super",
            "Typing T1 letters the PDF never uses (é, Æ, ľ) at codes where CM-Super's own encoding has other glyphs or none draws what the character map says, on two lines sharing one copy.",
            "Letters of TeX's encoding (T1) were taken from the font's own encoding, which has other glyphs or none at those codes; the character map now decides (v0.5.0).",
            () => TexEncodingLetters("SFRM1000"),
            [Replace("Hello world", "Hellé wÆrľd"), Replace("older", "ľder")]),

        new(
            "tex-width-for-another-glyph",
            "Typing Ć at a code whose width pdfTeX wrote for T1's Æ, with no character map to say so, gives Ć its own width.",
            "New letters took the width pdfTeX wrote for another glyph at their code, so they were spaced wrongly (v0.5.0).",
            () => TexPdf.Build(subset: new TexSubset { Font = "LMRoman10-Regular", WithDifferences = true }, widths: new Dictionary<int, int> { [198] = 903 }),
            [Replace("Hello world", "Hello Ćorld")]),

        new(
            "tex-shared-subset",
            "Typing letters in a font that describes only H and o of a subset it shares with another font: they get the widths they have in tex-shared-subset-own.",
            "pdfTeX embeds one subset for the fonts of a program, and the letters this font doesn't describe got its width of 0 (v0.5.0).",
            () => SharedSubset(describesOnlyItsOwn: true),
            [Replace("Ho", "Hold")]),

        new(
            "tex-shared-subset-own",
            "The edit of tex-shared-subset in a font that describes its whole subset, for comparing widths.",
            "Letters added to a font must get their own widths, whether or not the font shares its subset (v0.5.0).",
            () => SharedSubset(describesOnlyItsOwn: false),
            [Replace("Ho", "Hold")]),

        new(
            "tex-glyph-kept-whatever-the-character-map-says",
            "An edit that needs the font copy, in a font whose character map says code e is “o”, keeps drawing the PDF's glyph at that code.",
            "The font copy must draw every code the PDF draws with the same glyph, whatever the character map says (v0.5.0).",
            () => TexPdf.Build(subset: new TexSubset(), toUnicode: TexPdf.CharacterMap(new TexSubset(), ('e', "o"))),
            [Replace("Hollo world", "Hollo Kitty")]),

        new(
            "tex-code-the-subset-has-another-glyph-for",
            "Typing ľ, at a code where the shared subset has © for another font, draws ľ with the font copy.",
            "PDFium drew the subset's glyph (©) at a code the character map gives another letter (ľ), so the wrong glyph appeared (v0.5.0).",
            SubsetWithCopyright,
            [Replace("Hello world", "Hello worľd")]),

        new(
            "tex-quotation-mark-code-content-stream",
            "Typing ł, whose T1 code draws a quotation mark from the CM-Super subset, applies with the font copy, which draws ł there.",
            "At codes outside /Differences PDFium draws the subset's own glyph, here a quotation mark for ł (v0.5.0).",
            () => QuotationMarkCode(shipped: true),
            [Replace("Hello", "Hełlo")]),

        new(
            "tex-quotation-mark-code-pdfium-rewrite",
            "The edit of tex-quotation-mark-code-content-stream through PDFium's page rewrite is refused.",
            "PDFium's page rewrite can't add a font copy or check the codes it picks, so in TeX fonts it only removes text (v0.5.0).",
            () => QuotationMarkCode(shipped: true),
            [Replace("Hello", "Hełlo", contentEditing: false)]),

        new(
            "tex-quotation-mark-code-unshipped-font-content-stream",
            "The edit of tex-quotation-mark-code-content-stream in a font PaperKnife doesn't ship is refused, naming ł.",
            "In fonts PaperKnife doesn't ship, a character the PDF never uses could be drawn as another symbol, such as a quotation mark for ł; it must be refused (v0.5.0).",
            () => QuotationMarkCode(shipped: false),
            [Replace("Hello", "Hełlo")]),

        new(
            "tex-quotation-mark-code-unshipped-font-pdfium-rewrite",
            "The edit of tex-quotation-mark-code-unshipped-font-content-stream through PDFium's page rewrite is refused.",
            "PDFium's page rewrite can't check the codes it picks in TeX fonts, so it only removes text there (v0.5.0).",
            () => QuotationMarkCode(shipped: false),
            [Replace("Hello", "Hełlo", contentEditing: false)]),

        new(
            "tex-cmap-package-delta",
            "Typing Δ, which the PDF's character map (from the cmap package) gives as U+0394 where the Adobe Glyph List says U+2206, applies with the PDF's own font.",
            "Character maps of the cmap package disagree with the Adobe Glyph List (Δ as U+0394, not U+2206); the PDF's own map must be trusted, not the list (v0.5.0).",
            CmapPackageDelta,
            [Replace("Hello", "HeΔlo")]),

        new(
            "tex-cmap-package-delta-with-copy",
            "Typing Δ and y, which needs the font copy: the copy draws Δ too.",
            "Character maps of the cmap package disagree with the Adobe Glyph List (Δ as U+0394, not U+2206); the PDF's own map must be trusted, also by the font copy (v0.5.0).",
            CmapPackageDelta,
            [Replace("Hello", "HeΔly")]),

        new(
            "tex-pdfium-rewrite-only-removes",
            "With PDFium's page rewrite, text in a TeX font can only be removed: the edits of tex-pdfium-rewrite-only-removes-old, -o and -delete in one session, with an undo before the last.",
            "PDFium re-encodes the edited object's text and may pick a code that draws another glyph in TeX fonts (v0.5.0).",
            OnlyRemoves,
            [Replace("older", "old", contentEditing: false), Replace("older", "o", contentEditing: false), Undo(), Replace("older", string.Empty, contentEditing: false)]),

        new(
            "tex-pdfium-rewrite-only-removes-delete",
            "Through PDFium's page rewrite, deleting a line in a TeX font applies.",
            "PDFium re-encodes the edited object's text and may pick a code that draws another glyph in TeX fonts, so its rewrite only removes text there (v0.5.0).",
            OnlyRemoves,
            [Replace("older", string.Empty, contentEditing: false)]),

        new(
            "tex-pdfium-rewrite-only-removes-old",
            "Through PDFium's page rewrite, replacing “older” with “old” in a TeX font is refused.",
            "PDFium re-encodes the edited object's text and may pick a code that draws another glyph in TeX fonts, so its rewrite only removes text there (v0.5.0).",
            OnlyRemoves,
            [Replace("older", "old", contentEditing: false)]),

        new(
            "tex-pdfium-rewrite-only-removes-o",
            "Through PDFium's page rewrite, replacing “older” with “o” applies: it removes the object that drew “lder”, encoding nothing.",
            "PDFium re-encodes the edited object's text and may pick a code that draws another glyph in TeX fonts, so its rewrite only removes text there (v0.5.0).",
            OnlyRemoves,
            [Replace("older", "o", contentEditing: false)]),

        new(
            "tex-pdfium-rewrite-space-the-font-lacks",
            "Through PDFium's page rewrite, typing a space in a TeX font is refused, naming the space.",
            "PDFium's page rewrite can't leave gaps, so a space needs a glyph there; the message must name it (“doesn't contain: space”), not an invisible character (v0.3.0).",
            () => TexPdf.Build(),
            [Replace("Hello world", "Hello old world", contentEditing: false)]),

        new(
            "tex-letter-at-two-codes",
            "Typing ß, at 255 in the PDF's /Differences and at 251 in the font's own encoding, in a font PaperKnife doesn't ship, draws it at 255.",
            "With two codes for one letter, the edit could use the one where pdfTeX wrote the width of another glyph (û); the code the PDF names must be used (v0.5.0).",
            GermanDoubleS,
            [Replace("Hello", "Helßo")]),

        new(
            "tex-lookalike-character",
            "Typing ϵ, which the character map gives TeX's math italic glyph epsilon1, draws the PDF's own glyph, not a look-alike.",
            "The Adobe Glyph List has no epsilon1, so the copy could have drawn a look-alike such as epsilon (ε) instead of the PDF's own glyph (v0.5.0).",
            () => TexPdf.Build("BT /F1 24 Tf 72 700 Td (xy) Tj ET\n", MathItalic, toUnicode: TexPdf.CharacterMap(MathItalic, (15, "ϵ"))),
            [Replace("xy", "ϵxy")]),

        new(
            "tex-line-of-several-objects",
            "Typing new letters in a line drawn by two text objects: only the second needs the font copy, and the line stays one run of two objects.",
            "A line drawn by several text objects must be edited as one line, keeping its objects together (v0.3.0).",
            () => TexPdf.Build("BT /F1 24 Tf 72 700 Td [(Hello)] TJ 62 0 Td [(w) 27.8 (orld)] TJ ET\n"),
            [Replace("Hello world", "Hello Kitty")]),

        new(
            "tex-crafted-subset-glyph-differs",
            "Typing new letters is refused when the subset's “o” has another glyph's program, so the subset isn't part of PaperKnife's CMR10.",
            "The Type 1 reader is strict: a crafted subset that differs from the bundled font must not get a copy that draws other glyphs (v0.3.0).",
            () => TexPdf.Build(subset: new TexSubset { ReplaceGlyph = "o" }),
            [Replace("Hello world", "Hello Kitty")]),

        new(
            "tex-crafted-subset-stray-token",
            "Typing new letters is refused when the subset has a stray token among its glyph programs, which renderers skip.",
            "The Type 1 reader is strict: a crafted subset that differs from the bundled font must not get a copy that draws other glyphs (v0.3.0).",
            () => TexPdf.Build(subset: new TexSubset { StrayToken = true }),
            [Replace("Hello world", "Hello Kitty")]),

        new(
            "tex-crafted-subset-glyph-defined-twice",
            "Typing new letters is refused when the subset defines a glyph twice, first with another glyph's program, which renderers use.",
            "The Type 1 reader is strict: a crafted subset that differs from the bundled font must not get a copy that draws other glyphs (v0.3.0).",
            () => TexPdf.Build(subset: new TexSubset { DuplicateGlyph = true }),
            [Replace("Hello world", "Hello Kitty")]),

        new(
            "tex-crafted-subset-stub-subroutine",
            "Typing new letters is refused when a subroutine a glyph calls is a stub that only returns.",
            "The Type 1 reader is strict: a crafted subset that differs from the bundled font must not get a copy that draws other glyphs (v0.3.0).",
            () => TexPdf.Build(subset: new TexSubset { StubSubroutineOf = "d" }),
            [Replace("Hello world", "Hello Kitty")]),

        new(
            "tex-nonsymbolic-font-not-copied",
            "Typing letters a nonsymbolic TeX subset lacks is refused: such fonts aren't copied.",
            "In nonsymbolic fonts PDFium and the PDF specification disagree on which glyph a code draws, so a copy could draw other glyphs (v0.3.0).",
            () => TexPdf.Build(flags: 32),
            [Replace("Hello world", "Hello Kitty")]),

        new(
            "tex-ligatures-and-kerning-without-character-map",
            "Extending “Word file”, set with an fi ligature and kerning in a font without a character map, keeps the ligature and the kerning.",
            "Without a ToUnicode map, codes must be read through the font's built-in encoding as PDFium reads them, ligatures included, and unchanged text must keep its kerning (v0.3.0).",
            () => TexPdf.Build("BT /F1 24 Tf 72 700 Td [(W) 80 (ord) -333 (\\014le)] TJ ET\n", new TexSubset { Glyphs = ["W", "d", "e", "fi", "l", "o", "r", "s"] }),
            [Replace("Word file", "Word files")]),

        new(
            "tex-unused-widths-zero",
            "New letters in a subset whose missing letters have a width of 0, as some tools write, take their widths from the font, as in tex-unused-widths-pdftex.",
            "A width of 0 for the letters a subset leaves out made new letters overlap (v0.3.0).",
            () => TexPdf.Build(zeroUnusedWidths: true),
            [Replace("Hello world", "Hello Kitty")]),

        new(
            "tex-unused-widths-pdftex",
            "The edit of tex-unused-widths-zero in a subset with pdfTeX's widths, for comparing widths.",
            "New letters must get their own widths, whatever widths the PDF gives the letters its subset leaves out (v0.3.0).",
            () => TexPdf.Build(),
            [Replace("Hello world", "Hello Kitty")]),

        new(
            "tex-object-limit-room-for-copy",
            "Adding a copy of the font applies when the file has just enough object numbers left below PDFium's limit.",
            "PDFium reads no object past its object-number limit, so a copy must only be added where it can be read (v0.3.0).",
            () => TexPdf.Build(size: PdfFile.MaxObjectNumber - 2),
            [Replace("Hello world", "Hello Kitty")]),

        new(
            "tex-object-limit-no-room",
            "Adding a copy of the font is refused, with “too many objects”, when one object number fewer is left than tex-object-limit-room-for-copy has.",
            "PDFium reads no object past its object-number limit, so the copy would be lost and the new letters drawn wrongly (v0.3.0).",
            () => TexPdf.Build(size: PdfFile.MaxObjectNumber - 1),
            [Replace("Hello world", "Hello Kitty")]),

        new(
            "tex-font-with-many-names",
            "An edit on a page that gives one font a thousand names, with a large character map, takes less than 3 seconds.",
            "Every name read the font's character map again, so the edit took far too long (v0.3.0).",
            ManyNames,
            [Replace("Hello world", "Hello Kitty") with { MaxMilliseconds = 3000 }]),
    ];

    /// <summary>A subset of TeX's math italic font, CMMI10, with only x and y.</summary>
    private static TexSubset MathItalic => new() { Font = "CMMI10", Glyphs = ["x", "y"] };

    /// <summary>The PDF of tex-pdfium-rewrite-only-removes: "older" is drawn by two objects, "o" and "lder".</summary>
    private static PdfSnapshot OnlyRemoves() => TexPdf.Build("BT /F1 24 Tf 72 700 Td [(Hello)] TJ ET\nBT /F1 12 Tf 72 660 Td (o) Tj (lder) Tj ET\n");

    /// <summary>
    /// A PDF whose character map and widths follow TeX's encoding (T1) for codes its /Differences
    /// don't name: 169 is ľ and 198 Æ, where Latin Modern's own encoding has © and Ć; 233 is é,
    /// where CM-Super's (StandardEncoding) has Ø.
    /// </summary>
    private static PdfSnapshot TexEncodingLetters(string font)
    {
        var subset = new TexSubset { Font = font, WithDifferences = true };
        var full = TexPdf.Font(font);
        return TexPdf.Build(
            subset: subset,
            toUnicode: TexPdf.CharacterMap(subset, (169, "ľ"), (198, "Æ"), (233, "é")),
            widths: new Dictionary<int, int> { [169] = (int)Math.Round(full.Width("lcaron")!.Value), [198] = 900 });
    }

    /// <summary>A font that uses only H and o of a Latin Modern subset, describing only those or the whole subset.</summary>
    private static PdfSnapshot SharedSubset(bool describesOnlyItsOwn)
    {
        var subset = new TexSubset { Font = "LMRoman10-Regular", WithDifferences = true, Described = ["H", "o"] };
        return TexPdf.Build("BT /F1 24 Tf 72 700 Td (Ho) Tj ET\n", describesOnlyItsOwn ? subset : subset with { Described = null });
    }

    /// <summary>A Latin Modern subset that has © (at 169 in its own encoding) for another font; this font's map and width say 169 is ľ.</summary>
    private static PdfSnapshot SubsetWithCopyright()
    {
        var subset = new TexSubset
        {
            Font = "LMRoman10-Regular",
            WithDifferences = true,
            Glyphs = ["H", "d", "e", "l", "o", "r", "w", "copyright"],
            Described = ["H", "d", "e", "l", "o", "r", "w"],
        };
        var lcaron = (int)Math.Round(TexPdf.Font(subset.Font).Width("lcaron")!.Value);
        return TexPdf.Build(subset: subset, toUnicode: TexPdf.CharacterMap(subset, (169, "ľ")), widths: new Dictionary<int, int> { [169] = lcaron });
    }

    /// <summary>
    /// A CM-Super subset (renamed when it is to be a font PaperKnife doesn't ship) that has “ for
    /// another font, at 170 in its own encoding, where T1 and this font's map and width have ł.
    /// </summary>
    private static PdfSnapshot QuotationMarkCode(bool shipped)
    {
        var subset = new TexSubset
        {
            Font = "SFRM1000",
            Rename = shipped ? null : "SFZZ1000",
            WithDifferences = true,
            Glyphs = ["H", "e", "l", "o", "quotedblleft"],
            Described = ["H", "e", "l", "o"],
        };
        var lslash = (int)Math.Round(TexPdf.Font(subset.Font).Width("lslash")!.Value);
        return TexPdf.Build("BT /F1 24 Tf 72 700 Td (Hello) Tj ET\n", subset, toUnicode: TexPdf.CharacterMap(subset, (170, "ł")), widths: new Dictionary<int, int> { [170] = lslash });
    }

    /// <summary>Computer Modern's Delta at code 1, which the character map gives as U+0394, as the cmap package does.</summary>
    private static PdfSnapshot CmapPackageDelta()
    {
        var subset = new TexSubset { Glyphs = ["H", "e", "l", "o", "Delta"] };
        return TexPdf.Build("BT /F1 24 Tf 72 700 Td (Hello) Tj ET\nBT /F1 24 Tf 72 650 Td (\\001) Tj ET\n", subset, toUnicode: TexPdf.CharacterMap(subset, (1, "\u0394")));
    }

    /// <summary>
    /// ß at 255 in /Differences (T1), in a renamed CM-Super with no character map; 251, ß in the
    /// program's own encoding, has the width of T1's û.
    /// </summary>
    private static PdfSnapshot GermanDoubleS()
    {
        var subset = new TexSubset
        {
            Font = "SFRM1000",
            Rename = "SFZZ1000",
            WithDifferences = true,
            Glyphs = ["H", "e", "l", "o", "germandbls"],
            PdfCodes = new Dictionary<string, int> { ["germandbls"] = 255 },
        };
        var ucircumflex = (int)Math.Round(TexPdf.Font(subset.Font).Width("ucircumflex")!.Value);
        return TexPdf.Build("BT /F1 24 Tf 72 700 Td (Hello) Tj ET\nBT /F1 24 Tf 72 650 Td (\\377) Tj ET\n", subset, widths: new Dictionary<int, int> { [251] = ucircumflex });
    }

    /// <summary>The base TeX content and a thousand "o"s, each in another name for the same font, which has a large character map.</summary>
    private static PdfSnapshot ManyNames()
    {
        var names = Enumerable.Range(1, 1000).Select(i => $"N{i}").ToList();
        var content = TexPdf.Content + string.Concat(names.Select((name, i) =>
            string.Create(CultureInfo.InvariantCulture, $"BT /{name} 6 Tf {72 + (i % 40 * 12)} {600 - (i / 40 * 8)} Td (o) Tj ET\n")));
        return TexPdf.Build(content, aliases: names, toUnicode: TexFontTests.LargeCharacterMap());
    }
}
