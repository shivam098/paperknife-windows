using System.Globalization;
using System.Text;
using PaperKnife.Core.Content;
using PaperKnife.Core.Fonts;

namespace PaperKnife.Core.Tests;

/// <summary>Which bundled font <see cref="TexPdf"/> cuts its subset from and how, and the ways a crafted subset can differ.</summary>
internal sealed record TexSubset
{
    /// <summary>The bundled font: CMR10 as pdfLaTeX embeds it by default, LMRoman10-Regular with lmodern, SFRM1000 with T1 encoding.</summary>
    public string Font { get; init; } = "CMR10";

    /// <summary>
    /// Describes the encoding with /Differences for the codes the subset uses, as pdfTeX does for
    /// Latin Modern and CM-Super, rather than leaving it to the font's own encoding, as for Computer Modern.
    /// </summary>
    public bool WithDifferences { get; init; }

    /// <summary>The font's name in the PDF, when it isn't the bundled font's: as for a font PaperKnife doesn't ship.</summary>
    public string? Rename { get; init; }

    /// <summary>Codes the PDF gives glyphs where TeX's encoding differs from the font's own (in T1, ß is 255).</summary>
    public IReadOnlyDictionary<string, int>? PdfCodes { get; init; }

    public IReadOnlyList<string> Glyphs { get; init; } = ["H", "d", "e", "l", "o", "r", "w"];

    /// <summary>
    /// The glyphs the font dictionary's /Differences and /Widths describe, when only some of the
    /// subset's: pdfTeX embeds one subset for the fonts that use a program in several encodings,
    /// such as the letters' and the symbols' (T1 and TS1).
    /// </summary>
    public IReadOnlyList<string>? Described { get; init; }

    /// <summary>A glyph whose program is replaced by "e"'s, so the subset no longer matches the full font.</summary>
    public string? ReplaceGlyph { get; init; }

    /// <summary>A token among the glyph programs, before the last one. Renderers skip it.</summary>
    public bool StrayToken { get; init; }

    /// <summary>The first glyph defined twice: first with "e"'s program, which renderers use, then with its own.</summary>
    public bool DuplicateGlyph { get; init; }

    /// <summary>A glyph whose last subroutine becomes a stub that only returns, as subsetters do for unused ones.</summary>
    public string? StubSubroutineOf { get; init; }

    /// <summary>Bytes of comments full of half-written keys in the private part (and a tenth in the cleartext).</summary>
    public int Junk { get; init; }
}

/// <summary>
/// PDFs set the way pdfTeX sets them: /F1 is a subset of a Type 1 font PaperKnife ships (CMR10
/// unless the subset says otherwise, cut from PaperKnife's copy), symbolic, with the font's own
/// encoding or /Differences and, unless one is given, no ToUnicode map; words are separated by TJ
/// adjustments, as TeX has no space glyph.
/// </summary>
internal static class TexPdf
{
    public const string Content =
        "BT /F1 24 Tf 72 700 Td [(Hello) -333 (w) 27.8 (orld)] TJ ET\n" +
        "BT /F1 12 Tf 72 660 Td (older) Tj ET\n";

    public static Type1Font Full => Font("CMR10");

    public static Type1Font Font(string name) => BundledFonts.FindType1(name)!;

    /// <param name="flags">The font descriptor's /Flags: 4 is symbolic, as pdfTeX writes; 32 nonsymbolic.</param>
    /// <param name="size">The trailer's /Size, when it is to be larger than the objects need.</param>
    /// <param name="zeroUnusedWidths">Gives the codes whose glyphs the subset leaves out a width of 0, as some tools do.</param>
    /// <param name="aliases">More names for the font in the page's resources.</param>
    /// <param name="toUnicode">A ToUnicode CMap for the font (see <see cref="CharacterMap"/>).</param>
    /// <param name="widths">Widths for more codes, as pdfTeX gives every code in range the width of TeX's glyph for it.</param>
    public static PdfSnapshot Build(
        string content = Content,
        TexSubset? subset = null,
        int flags = 4,
        int size = 0,
        bool zeroUnusedWidths = false,
        IReadOnlyList<string>? aliases = null,
        string? toUnicode = null,
        IReadOnlyDictionary<int, int>? widths = null)
    {
        subset ??= new TexSubset();
        var full = Font(subset.Font);
        var (clear, encrypted, trailer) = Subset(subset);

        // Like pdfTeX, /Widths covers the codes from the first to the last one the subset uses.
        var described = subset.Described ?? subset.Glyphs;
        var codes = Codes(subset, full, described);
        var byCode = codes.Zip(described).ToDictionary(c => c.First, c => c.Second);
        var first = Math.Min(codes.Min(), widths?.Keys.Min() ?? 255);
        var last = Math.Max(codes.Max(), widths?.Keys.Max() ?? 0);
        var widthArray = Enumerable.Range(first, last - first + 1)
            .Select(code => widths is not null && widths.TryGetValue(code, out var given) ? given
                : (byCode.GetValueOrDefault(code) ?? (subset.Described is null ? GlyphAt(full, code) : null)) is { } name
                    && (!zeroUnusedWidths || subset.Glyphs.Contains(name))
                ? full.Width(name) ?? 0 : 0)
            .Select(w => w.ToString(CultureInfo.InvariantCulture));
        var encoding = subset.WithDifferences ? $" /Encoding << /Type /Encoding /Differences [{Differences(byCode)}] >>" : string.Empty;
        var program = clear + encrypted + trailer;
        var fonts = string.Concat((aliases ?? []).Select(name => $" /{name} 5 0 R"));
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R{fonts} >> >> >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            $"<< /Type /Font /Subtype /Type1 /BaseFont /AAAAAA+{subset.Rename ?? full.FontName} /FirstChar {first} /LastChar {last} " +
                $"/Widths [{string.Join(' ', widthArray)}] /FontDescriptor 6 0 R{encoding}{(toUnicode is null ? string.Empty : " /ToUnicode 8 0 R")} >>",
            $"<< /Type /FontDescriptor /FontName /AAAAAA+{subset.Rename ?? full.FontName} /Flags {flags} /FontBBox [-40 -250 1009 750] /ItalicAngle 0 " +
                $"/Ascent 694 /Descent -194 /CapHeight 683 /XHeight 431 /StemV 69 /CharSet ({string.Concat(subset.Glyphs.Select(g => "/" + g))}) /FontFile 7 0 R >>",
            $"<< /Length1 {clear.Length} /Length2 {encrypted.Length} /Length3 {trailer.Length} /Length {program.Length} >>\nstream\n{program}\nendstream",
        };
        if (toUnicode is not null)
        {
            objects.Add($"<< /Length {toUnicode.Length} >>\nstream\n{toUnicode}\nendstream");
        }

        var pdf = new StringBuilder("%PDF-1.5\n%\u00E2\u00E3\u00CF\u00D3\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(pdf.Length);
            pdf.Append(CultureInfo.InvariantCulture, $"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        var xref = pdf.Length;
        pdf.Append(CultureInfo.InvariantCulture, $"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            pdf.Append(CultureInfo.InvariantCulture, $"{offset:D10} 00000 n \n");
        }

        pdf.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {Math.Max(size, objects.Count + 1)} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return PdfSnapshot.FromBytes(Encoding.Latin1.GetBytes(pdf.ToString()));
    }

    /// <summary>
    /// The three parts of a subset of a bundled font, as Latin-1 text: only some glyphs, and
    /// .notdef, written with the font's own spelling of the charstring delimiters (RD and ND, or
    /// -| and |-), as subsetters keep them.
    /// </summary>
    public static (string Clear, string Encrypted, string Trailer) Subset(TexSubset subset)
    {
        var full = Font(subset.Font);
        var clear = Encoding.Latin1.GetString(full.Program, 0, full.Length1)
            .Replace($"/FontName /{full.FontName}", $"/FontName /AAAAAA+{subset.Rename ?? full.FontName}", StringComparison.Ordinal);
        var trailer = Encoding.Latin1.GetString(full.Program, full.Length1 + full.Length2, full.Length3);

        // The private part keeps its random first bytes, so it encrypts back to the same start.
        var plain = Encoding.Latin1.GetString(Type1Font.Decrypt(full.Program.AsSpan(full.Length1, full.Length2), 55665, 0));
        var begin = plain.IndexOf("begin", plain.IndexOf("/CharStrings", StringComparison.Ordinal), StringComparison.Ordinal) + 5;
        var entries = new Dictionary<string, string>(StringComparer.Ordinal);
        var (rd, nd) = ("RD", "ND");
        var position = begin;
        while (true)
        {
            while (char.IsWhiteSpace(plain[position]))
            {
                position++;
            }

            if (plain[position] != '/')
            {
                break;
            }

            var (name, data, end, read) = Entry(plain, position, 3);
            entries[name[1..]] = data;
            rd = read;
            (nd, position) = NextToken(plain, end);
        }

        var kept = new StringBuilder("\n");
        var glyphs = subset.Glyphs.Append(".notdef").ToList();
        if (subset.DuplicateGlyph)
        {
            kept.Append(CultureInfo.InvariantCulture, $"/{glyphs[0]} {entries["e"].Length} {rd} {entries["e"]} {nd}\n");
        }

        for (var i = 0; i < glyphs.Count; i++)
        {
            if (subset.StrayToken && i == glyphs.Count - 1)
            {
                kept.Append("0 pop\n");
            }

            var data = glyphs[i] == subset.ReplaceGlyph ? entries["e"] : entries[glyphs[i]];
            kept.Append(CultureInfo.InvariantCulture, $"/{glyphs[i]} {data.Length} {rd} {data} {nd}\n");
        }

        var result = string.Concat(plain.AsSpan(0, begin), kept.ToString(), plain.AsSpan(position));
        if (subset.StubSubroutineOf is { } glyph)
        {
            // "dup <index> <length> RD <data>" becomes a stub that only returns (after four random bytes).
            var index = CharStringAnalyzer.Analyze(full.CharStrings[glyph], full.Subrs, new CharStringAnalyzer.Budget(100_000))!.Value.Subrs.Max();
            var start = result.IndexOf($"dup {index} ", result.IndexOf("/Subrs", StringComparison.Ordinal), StringComparison.Ordinal);
            var (_, _, end, read) = Entry(result, start + 4, 3);
            var stub = Encoding.Latin1.GetString(Encrypt([0, 0, 0, 0, 11], 4330));
            result = string.Concat(result.AsSpan(0, start), $"dup {index} 5 {read} {stub}", result.AsSpan(end));
        }

        if (subset.Junk > 0)
        {
            clear = clear.Insert(clear.IndexOf('\n') + 1, "%" + Repeat("/FontMatrix[", subset.Junk / 10) + "\n");
            result = result.Insert(result.IndexOf("/CharStrings", StringComparison.Ordinal), "%" + Repeat("/BlueValues[", subset.Junk) + "\n");
        }

        return (clear, Encoding.Latin1.GetString(Encrypt(Encoding.Latin1.GetBytes(result), 55665)), trailer);
    }

    /// <summary>
    /// A ToUnicode CMap, as pdfTeX writes one for TeX's encoding: the text of the glyphs the font
    /// describes, at their codes, and of <paramref name="more"/> codes (which take precedence), as
    /// TeX's encoding gives characters the document doesn't use.
    /// </summary>
    public static string CharacterMap(TexSubset subset, params (int Code, string Text)[] more)
    {
        var full = Font(subset.Font);
        var glyphs = subset.Described ?? subset.Glyphs;
        var entries = new SortedDictionary<int, string>();
        foreach (var (code, glyph) in Codes(subset, full, glyphs).Zip(glyphs))
        {
            if (Encodings.GlyphToUnicode(glyph) is { } text)
            {
                entries[code] = text;
            }
        }

        foreach (var (code, text) in more)
        {
            entries[code] = text;
        }

        var map = new StringBuilder("/CIDInit /ProcSet findresource begin 12 dict begin begincmap /CMapName /TeX def /CMapType 2 def\n");
        map.Append("1 begincodespacerange <00> <FF> endcodespacerange\n").Append(CultureInfo.InvariantCulture, $"{entries.Count} beginbfchar\n");
        foreach (var (code, text) in entries)
        {
            map.Append(CultureInfo.InvariantCulture, $"<{code:X2}> <{string.Concat(text.Select(c => ((int)c).ToString("X4", CultureInfo.InvariantCulture)))}>\n");
        }

        return map.Append("endbfchar endcmap CMapName currentdict /CMap defineresource pop end end").ToString();
    }

    /// <summary>The glyph a code selects through the font's own encoding.</summary>
    private static string? GlyphAt(Type1Font font, int code) =>
        font.UsesStandardEncoding ? Encodings.FullStandardName(code) : font.Encoding.GetValueOrDefault(code);

    /// <summary>The codes the PDF gives glyphs: those of the font's own encoding, unless the subset says otherwise.</summary>
    private static List<int> Codes(TexSubset subset, Type1Font font, IEnumerable<string> glyphs) =>
        [.. glyphs.Select(g => subset.PdfCodes is not null && subset.PdfCodes.TryGetValue(g, out var code) ? code : Enumerable.Range(0, 256).First(c => GlyphAt(font, c) == g))];

    /// <summary>A /Differences array naming the glyph of each code, as pdfTeX writes for the codes a document uses.</summary>
    private static string Differences(Dictionary<int, string> glyphs)
    {
        var text = new StringBuilder();
        var previous = -2;
        foreach (var (code, glyph) in glyphs.OrderBy(g => g.Key))
        {
            if (code != previous + 1)
            {
                text.Append(CultureInfo.InvariantCulture, $" {code}");
            }

            text.Append(" /").Append(glyph);
            previous = code;
        }

        return text.ToString().Trim();
    }

    // "<first> <length> <RD> <data>" at a position: the first token, the data, where the data ends and how RD is spelled.
    private static (string First, string Data, int End, string Rd) Entry(string text, int position, int tokens)
    {
        var parts = text.Substring(position, Math.Min(80, text.Length - position)).Split(' ', tokens + 1);
        var length = int.Parse(parts[1], CultureInfo.InvariantCulture);
        var dataStart = position + parts.Take(tokens).Sum(p => p.Length + 1);
        return (parts[0], text.Substring(dataStart, length), dataStart + length, parts[tokens - 1]);
    }

    // The token after a position (such as ND after a glyph's data), and where it ends.
    private static (string Token, int End) NextToken(string text, int position)
    {
        while (char.IsWhiteSpace(text[position]))
        {
            position++;
        }

        var end = position;
        while (end < text.Length && !char.IsWhiteSpace(text[end]))
        {
            end++;
        }

        return (text[position..end], end);
    }

    private static string Repeat(string text, int bytes) => string.Concat(Enumerable.Repeat(text, Math.Max(1, bytes / text.Length)));

    private static byte[] Encrypt(byte[] plain, int key)
    {
        var cipher = new byte[plain.Length];
        var r = key;
        for (var i = 0; i < plain.Length; i++)
        {
            var c = (byte)(plain[i] ^ (r >> 8));
            cipher[i] = c;
            r = ((c + r) * 52845 + 22719) & 0xFFFF;
        }

        return cipher;
    }
}
