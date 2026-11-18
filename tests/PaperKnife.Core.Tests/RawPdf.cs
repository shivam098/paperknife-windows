using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace PaperKnife.Core.Tests;

/// <summary>
/// Hand-written PDFs (Latin-1) for content and file structures PDFium can't create itself, such as
/// inline images, shadings, character spacing, Type 3 fonts and cross-reference streams.
/// Every page has these fonts:
/// <list type="bullet">
/// <item><c>/F1</c>: Helvetica.</item>
/// <item><c>/F2</c>: Helvetica in which code 66 also draws "A", with a wider advance (1000) than
/// code 65 (667), so a glyph's Unicode value doesn't tell its width.</item>
/// <item><c>/F3</c>: a Type 3 font whose glyphs "a", "b" and "c" are boxes 500, 600 and 700 units wide.</item>
/// </list>
/// </summary>
internal static class RawPdf
{
    public const string BaseContent =
        "BT /F1 24 Tf 72 700 Td (Hello World) Tj ET\n" +
        "BT /F1 12 Tf 72 660 Td (This line must not move) Tj ET\n" +
        "0.86 0.16 0.16 rg 72 500 200 100 re f\n";

    public const string ShadedArea = "q 300 500 200 100 re W n /Sh1 sh Q\n";

    public const string InlineImage =
        "q 60 0 0 60 400 300 cm BI /W 2 /H 2 /CS /RGB /BPC 8 ID " +
        "\u00FF\u0000\u0000\u0000\u00FF\u0000\u0000\u0000\u00FF\u00FF\u00FF\u0000\nEI Q\n";

    public const string CharacterSpacing = "BT /F1 14 Tf 4 Tc 72 300 Td (Spaced out letters) Tj ET\n";

    public const string KernedText = "BT /F1 18 Tf 72 250 Td [(Ke) -300 (rned text)] TJ ET\n";

    public const string Type3Text = "BT /F3 24 Tf 72 400 Td (abc) Tj ET\n";

    private static readonly string[] Shared =
    [
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
        "<< /ShadingType 2 /ColorSpace /DeviceRGB /Coords [300 0 500 0] /Extend [true true] " +
            "/Function << /FunctionType 2 /Domain [0 1] /C0 [1 0 0] /C1 [0 0 1] /N 1 >> >>",
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /FirstChar 65 /LastChar 68 /Widths [667 1000 722 722] " +
            "/Encoding << /Type /Encoding /BaseEncoding /WinAnsiEncoding /Differences [66 /A] >> >>",
        "<< /Type /Font /Subtype /Type3 /FontBBox [0 0 700 700] /FontMatrix [0.001 0 0 0.001 0 0] " +
            "/CharProcs << /a 7 0 R /b 8 0 R /c 9 0 R >> /Encoding << /Type /Encoding /Differences [97 /a /b /c] >> " +
            "/FirstChar 97 /LastChar 99 /Widths [500 600 700] /ToUnicode 10 0 R /Resources << >> >>",
        Stream("500 0 0 0 450 700 d1 0 0 450 700 re f"),
        Stream("600 0 0 0 550 700 d1 0 0 550 350 re f"),
        Stream("700 0 0 0 650 700 d1 0 350 650 350 re f"),
        Stream("/CIDInit /ProcSet findresource begin 12 dict begin begincmap /CMapName /T3 def /CMapType 2 def\n" +
            "1 begincodespacerange <00> <FF> endcodespacerange\n1 beginbfrange <61> <63> <0061> endbfrange\n" +
            "endcmap CMapName currentdict /CMap defineresource pop end end"),
    ];

    /// <summary>One page drawing <paramref name="content"/>, with a cross-reference table.</summary>
    public static PdfSnapshot SinglePage(string content) => Build([[content]]);

    /// <summary>
    /// A document whose pages draw the given content streams. With <paramref name="shareContent"/>,
    /// the second page draws the first page's first stream instead of its own. With
    /// <paramref name="useObjectStreams"/>, dictionaries are stored in an object stream and the
    /// cross-reference data in a compressed stream with a PNG predictor.
    /// </summary>
    public static PdfSnapshot Build(IReadOnlyList<IReadOnlyList<string>> pages, bool shareContent = false, bool useObjectStreams = false)
    {
        // Objects 1 and 2 are the catalog and page tree; 3 to 10 are shared; pages follow.
        var objects = new List<string> { "<< /Type /Catalog /Pages 2 0 R >>", string.Empty };
        objects.AddRange(Shared);
        var kids = new List<int>();
        var firstStream = 0;
        foreach (var contents in pages)
        {
            var pageNumber = objects.Count + 1;
            kids.Add(pageNumber);
            objects.Add(string.Empty);
            var streams = new List<int>();
            foreach (var content in contents)
            {
                objects.Add(Stream(content));
                streams.Add(objects.Count);
            }

            if (firstStream == 0)
            {
                firstStream = streams[0];
            }
            else if (shareContent)
            {
                streams = [firstStream];
            }

            var contentsEntry = streams.Count == 1 ? $"{streams[0]} 0 R" : $"[{string.Join(' ', streams.Select(s => $"{s} 0 R"))}]";
            objects[pageNumber - 1] = "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                $"/Contents {contentsEntry} /Resources << /Font << /F1 3 0 R /F2 5 0 R /F3 6 0 R >> /Shading << /Sh1 4 0 R >> >> >>";
        }

        objects[1] = $"<< /Type /Pages /Kids [{string.Join(' ', kids.Select(k => $"{k} 0 R"))}] /Count {kids.Count} >>";
        return useObjectStreams ? WriteWithStreams(objects) : WriteWithTable(objects);
    }

    private static string Stream(string data) => $"<< /Length {Encoding.Latin1.GetByteCount(data)} >>\nstream\n{data}\nendstream";

    private static PdfSnapshot WriteWithTable(List<string> objects)
    {
        var pdf = new StringBuilder("%PDF-1.7\n%\u00E2\u00E3\u00CF\u00D3\n");
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

        pdf.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return PdfSnapshot.FromBytes(Encoding.Latin1.GetBytes(pdf.ToString()));
    }

    /// <summary>Stores every dictionary in one object stream and writes a cross-reference stream (PDF 1.5).</summary>
    private static PdfSnapshot WriteWithStreams(List<string> objects)
    {
        var compressed = Enumerable.Range(1, objects.Count).Where(n => !objects[n - 1].Contains("stream", StringComparison.Ordinal)).ToList();
        var objectStream = objects.Count + 1;
        var xrefStream = objects.Count + 2;
        var header = new StringBuilder();
        var bodies = new StringBuilder();
        foreach (var number in compressed)
        {
            header.Append(CultureInfo.InvariantCulture, $"{number} {bodies.Length} ");
            bodies.Append(objects[number - 1]).Append('\n');
        }

        var packed = Deflate(header + bodies.ToString());
        var pdf = new StringBuilder("%PDF-1.7\n%\u00E2\u00E3\u00CF\u00D3\n");
        var offsets = new Dictionary<int, int>();
        for (var number = 1; number <= objects.Count; number++)
        {
            if (!compressed.Contains(number))
            {
                offsets[number] = pdf.Length;
                pdf.Append(CultureInfo.InvariantCulture, $"{number} 0 obj\n{objects[number - 1]}\nendobj\n");
            }
        }

        offsets[objectStream] = pdf.Length;
        pdf.Append(CultureInfo.InvariantCulture,
            $"{objectStream} 0 obj\n<< /Type /ObjStm /N {compressed.Count} /First {header.Length} /Filter /FlateDecode /Length {packed.Length} >>\nstream\n{packed}\nendstream\nendobj\n");
        offsets[xrefStream] = pdf.Length;

        // Rows of [type, offset (4 bytes), field 3 (2 bytes)], PNG "Up"-filtered.
        var rows = new List<byte[]>();
        for (var number = 0; number <= xrefStream; number++)
        {
            var index = compressed.IndexOf(number);
            rows.Add(number == 0 ? Row(0, 0, 65535) : index >= 0 ? Row(2, objectStream, index) : Row(1, offsets[number], 0));
        }

        var filtered = new List<byte>();
        var previous = new byte[7];
        foreach (var row in rows)
        {
            filtered.Add(2);
            filtered.AddRange(row.Select((b, i) => (byte)(b - previous[i])));
            previous = row;
        }

        var data = Deflate(Encoding.Latin1.GetString([.. filtered]));
        pdf.Append(CultureInfo.InvariantCulture,
            $"{xrefStream} 0 obj\n<< /Type /XRef /Size {xrefStream + 1} /Root 1 0 R /W [1 4 2] /Filter /FlateDecode " +
            $"/DecodeParms << /Predictor 12 /Columns 7 >> /Length {data.Length} >>\nstream\n{data}\nendstream\nendobj\n" +
            $"startxref\n{offsets[xrefStream]}\n%%EOF\n");
        return PdfSnapshot.FromBytes(Encoding.Latin1.GetBytes(pdf.ToString()));

        static byte[] Row(int type, int second, int third) =>
            [(byte)type, (byte)(second >> 24), (byte)(second >> 16), (byte)(second >> 8), (byte)second, (byte)(third >> 8), (byte)third];
    }

    private static string Deflate(string text)
    {
        using var output = new MemoryStream();
        using (var z = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            z.Write(Encoding.Latin1.GetBytes(text));
        }

        return Encoding.Latin1.GetString(output.ToArray());
    }
}
