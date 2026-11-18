using System.Globalization;
using System.Text;
using PaperKnife.Core.Fonts;

namespace PaperKnife.Core.Tests;

/// <summary>How <see cref="TexPdf"/> cuts its CMR10 subset, and the ways a crafted subset can differ.</summary>
internal sealed record TexSubset
{
    public IReadOnlyList<string> Glyphs { get; init; } = ["H", "d", "e", "l", "o", "r", "w"];

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
/// PDFs set the way pdfTeX sets them: /F1 is a subset of the Type 1 font CMR10 (cut from the copy
/// PaperKnife ships), symbolic, with the font's builtin encoding and, unless one is given, no
/// ToUnicode map; words are separated by TJ adjustments, as TeX has no space glyph.
/// </summary>
internal static class TexPdf
{
    public const string Content =
        "BT /F1 24 Tf 72 700 Td [(Hello) -333 (w) 27.8 (orld)] TJ ET\n" +
        "BT /F1 12 Tf 72 660 Td (older) Tj ET\n";

    public static Type1Font Full => BundledFonts.FindType1("CMR10")!;

    /// <param name="flags">The font descriptor's /Flags: 4 is symbolic, as pdfTeX writes; 32 nonsymbolic.</param>
    /// <param name="size">The trailer's /Size, when it is to be larger than the objects need.</param>
    /// <param name="zeroUnusedWidths">Gives the codes whose glyphs the subset leaves out a width of 0, as some tools do.</param>
    /// <param name="aliases">More names for the font in the page's resources.</param>
    /// <param name="toUnicode">A ToUnicode CMap for the font.</param>
    public static PdfSnapshot Build(
        string content = Content,
        TexSubset? subset = null,
        int flags = 4,
        int size = 0,
        bool zeroUnusedWidths = false,
        IReadOnlyList<string>? aliases = null,
        string? toUnicode = null)
    {
        subset ??= new TexSubset();
        var (clear, encrypted, trailer) = Subset(subset);

        // Like pdfTeX, /Widths covers the codes from the first to the last one the subset uses.
        var codes = subset.Glyphs.Select(g => Full.Encoding.First(e => e.Value == g).Key).ToList();
        var first = codes.Min();
        var last = codes.Max();
        var widths = Enumerable.Range(first, last - first + 1)
            .Select(code => Full.Encoding.TryGetValue(code, out var name) && (!zeroUnusedWidths || subset.Glyphs.Contains(name)) ? Full.Width(name) ?? 0 : 0)
            .Select(w => w.ToString(CultureInfo.InvariantCulture));
        var program = clear + encrypted + trailer;
        var fonts = string.Concat((aliases ?? []).Select(name => $" /{name} 5 0 R"));
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R{fonts} >> >> >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            $"<< /Type /Font /Subtype /Type1 /BaseFont /AAAAAA+CMR10 /FirstChar {first} /LastChar {last} " +
                $"/Widths [{string.Join(' ', widths)}] /FontDescriptor 6 0 R{(toUnicode is null ? string.Empty : " /ToUnicode 8 0 R")} >>",
            $"<< /Type /FontDescriptor /FontName /AAAAAA+CMR10 /Flags {flags} /FontBBox [-40 -250 1009 750] /ItalicAngle 0 " +
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

    /// <summary>The three parts of a subset of the bundled CMR10, as Latin-1 text: only some glyphs, and .notdef.</summary>
    public static (string Clear, string Encrypted, string Trailer) Subset(TexSubset subset)
    {
        var full = Full;
        var clear = Encoding.Latin1.GetString(full.Program, 0, full.Length1).Replace("/FontName /CMR10", "/FontName /AAAAAA+CMR10", StringComparison.Ordinal);
        var trailer = Encoding.Latin1.GetString(full.Program, full.Length1 + full.Length2, full.Length3);

        // The private part keeps its random first bytes, so it encrypts back to the same start.
        var plain = Encoding.Latin1.GetString(Type1Font.Decrypt(full.Program.AsSpan(full.Length1, full.Length2), 55665, 0));
        var begin = plain.IndexOf("begin", plain.IndexOf("/CharStrings", StringComparison.Ordinal), StringComparison.Ordinal) + 5;
        var entries = new Dictionary<string, string>(StringComparer.Ordinal);
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

            var (name, data, end) = Entry(plain, position, 3);
            entries[name[1..]] = data;
            position = plain.IndexOf("ND", end, StringComparison.Ordinal) + 2;
        }

        var kept = new StringBuilder("\n");
        var glyphs = subset.Glyphs.Append(".notdef").ToList();
        if (subset.DuplicateGlyph)
        {
            kept.Append(CultureInfo.InvariantCulture, $"/{glyphs[0]} {entries["e"].Length} RD {entries["e"]} ND\n");
        }

        for (var i = 0; i < glyphs.Count; i++)
        {
            if (subset.StrayToken && i == glyphs.Count - 1)
            {
                kept.Append("0 pop\n");
            }

            var data = glyphs[i] == subset.ReplaceGlyph ? entries["e"] : entries[glyphs[i]];
            kept.Append(CultureInfo.InvariantCulture, $"/{glyphs[i]} {data.Length} RD {data} ND\n");
        }

        var result = string.Concat(plain.AsSpan(0, begin), kept.ToString(), plain.AsSpan(position));
        if (subset.StubSubroutineOf is { } glyph)
        {
            // "dup <index> <length> RD <data>" becomes a stub that only returns (after four random bytes).
            var index = CharStringAnalyzer.Analyze(full.CharStrings[glyph], full.Subrs, new CharStringAnalyzer.Budget(100_000))!.Value.Subrs.Max();
            var start = result.IndexOf($"dup {index} ", result.IndexOf("/Subrs", StringComparison.Ordinal), StringComparison.Ordinal);
            var (_, _, end) = Entry(result, start + 4, 3);
            var stub = Encoding.Latin1.GetString(Encrypt([0, 0, 0, 0, 11], 4330));
            result = string.Concat(result.AsSpan(0, start), $"dup {index} 5 RD {stub}", result.AsSpan(end));
        }

        if (subset.Junk > 0)
        {
            clear = clear.Insert(clear.IndexOf('\n') + 1, "%" + Repeat("/FontMatrix[", subset.Junk / 10) + "\n");
            result = result.Insert(result.IndexOf("/CharStrings", StringComparison.Ordinal), "%" + Repeat("/BlueValues[", subset.Junk) + "\n");
        }

        return (clear, Encoding.Latin1.GetString(Encrypt(Encoding.Latin1.GetBytes(result), 55665)), trailer);
    }

    // "<first> <length> RD <data>" at a position: the first token, the data and where the data ends.
    private static (string First, string Data, int End) Entry(string text, int position, int tokens)
    {
        var parts = text[position..].Split(' ', tokens + 1);
        var length = int.Parse(parts[1], CultureInfo.InvariantCulture);
        var dataStart = position + parts.Take(tokens).Sum(p => p.Length + 1);
        return (parts[0], text.Substring(dataStart, length), dataStart + length);
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
