using System.Text;
using PaperKnife.Core.Content;
using PaperKnife.Core.Syntax;

namespace PaperKnife.Core.Tests;

/// <summary>PaperKnife's own reading and writing of PDF syntax, used for content edits.</summary>
public sealed class SyntaxTests
{
    [Fact]
    public void Lexer_ReadsStringsNamesNumbersAndReferences()
    {
        var lexer = new PdfLexer(Encoding.Latin1.GetBytes(@"(a\(b\)c\\d\101(e)) <48 65 6C 6C 6F> /A#20B -.5 12 0 R"));

        Assert.Equal(@"a(b)c\dA(e)", Encoding.Latin1.GetString(lexer.Next().Bytes!));
        Assert.Equal("Hello", Encoding.Latin1.GetString(lexer.Next().Bytes!));
        Assert.Equal("A B", lexer.Next().Text);
        Assert.Equal(-0.5, lexer.Next().Number);
        var reference = Assert.IsType<CosRef>(PdfObjectParser.Read(lexer));
        Assert.Equal(12, reference.Number);
    }

    [Fact]
    public void PdfFile_ReadsObjectStreamsAndCrossReferenceStreams()
    {
        var file = PdfFile.TryOpen(RawPdf.Build([[RawPdf.BaseContent]], useObjectStreams: true).Memory, out var problem);

        Assert.Null(problem);
        Assert.True(file!.HasXrefStream);
        var page = Assert.Single(file.GetPages()!);
        var content = Assert.IsType<CosStream>(file.GetObject(file.GetContentStreams(page.Dict)![0].Number));
        Assert.Equal(RawPdf.BaseContent, Encoding.Latin1.GetString(file.DecodeStream(content, out _)!));
    }

    [Fact]
    public void ContentParser_SkipsInlineImageData()
    {
        var ops = ContentParser.Parse(Encoding.Latin1.GetBytes(RawPdf.InlineImage + RawPdf.KernedText))!;

        Assert.Equal(new[] { "q", "cm", "BI", "Q", "BT", "Tf", "Td", "TJ", "ET" }, ops.Select(o => o.Operator));
    }

    [Fact(Timeout = 10_000)]
    public async Task ContentParser_SkipsInlineImageDataWhoseSizeOverflows()
    {
        // 536870910 gray bytes a row overflow 32 bits; wrapped, the size of 42 rows pointed back at
        // the first EI, so reading went back to it and read the image again, forever.
        var content = Encoding.Latin1.GetBytes("EI BI /W 536870910 /H 42 /CS /G /BPC 8 ID x EI");

        var ops = await Task.Run(() => ContentParser.Parse(content), TestContext.Current.CancellationToken);

        Assert.NotNull(ops);
        Assert.Equal(new[] { "EI", "BI" }, ops.Select(o => o.Operator));
        Assert.Equal(content.Length, ops[1].End);
    }

    [Fact]
    public void IncrementalUpdate_KeepsEveryOriginalByteAndReplacesTheStream()
    {
        var original = RawPdf.SinglePage(RawPdf.BaseContent);
        var file = PdfFile.TryOpen(original.Memory, out _)!;
        var content = file.GetContentStreams(file.GetPages()![0].Dict)![0];

        var updated = IncrementalUpdate.Append(file, [new StreamUpdate(content.Number, content.Generation, "BT /F1 24 Tf 72 700 Td (Updated) Tj ET"u8.ToArray())]);

        Assert.True(updated.AsSpan().StartsWith(original.Span));
        var reread = PdfFile.TryOpen(updated, out _)!;
        var stream = Assert.IsType<CosStream>(reread.GetObject(content.Number));
        Assert.Equal("BT /F1 24 Tf 72 700 Td (Updated) Tj ET", Encoding.Latin1.GetString(reread.DecodeStream(stream, out _)!));
        using var session = DocumentSession.FromSnapshot(PdfSnapshot.FromBytes(updated));
        Assert.Contains(session.GetTextRuns(0), r => r.Text == "Updated");
    }

    [Fact]
    public void StreamFilters_RefuseDataThatExpandsTooFar()
    {
        // Each two-byte RunLength run expands to 128 bytes; Flate shrinks the runs to almost nothing.
        var runs = new byte[2 * ((StreamFilters.MaxDecodedLength / 128) + 1)];
        for (var i = 0; i < runs.Length; i += 2)
        {
            runs[i] = 129;
            runs[i + 1] = (byte)'A';
        }

        var decoded = StreamFilters.Decode(StreamFilters.Deflate(runs), ["FlateDecode", "RunLengthDecode"], [], out var problem);

        Assert.Null(decoded);
        Assert.NotNull(problem);
    }

    [Fact]
    public void StreamFilters_TreatFlateDataThatNeedsAPresetDictionaryAsDamaged()
    {
        // A zlib header asking for a preset dictionary (FDICT), which a PDF can't give, then a
        // stored block: .NET's ZLibStream throws ZLibException for it, not InvalidDataException.
        byte[] data = [0x78, 0xBB, 0, 0, 0, 1, 0x01, 0x01, 0x00, 0xFE, 0xFF, (byte)'A'];

        Assert.Null(StreamFilters.Decode(data, ["FlateDecode"], [], out var problem));
        Assert.NotNull(problem);
        Assert.Null(StreamFilters.InflatePrefix(data, 1));
    }

    [Fact]
    public void FontInfo_GivesUpQuicklyOnWidthMapsTooLargeToRead()
    {
        var file = PdfFile.TryOpen(RawPdf.SinglePage(RawPdf.BaseContent).Memory, out _)!;
        var widths = new List<CosObject>();
        for (var i = 0; i < 100_000; i++)
        {
            widths.AddRange([Number(0), Number(131_000), Number(500)]);
        }

        var font = new CosDict(new Dictionary<string, CosObject>
        {
            ["Subtype"] = new CosName("Type0"),
            ["Encoding"] = new CosName("Identity-H"),
            ["DescendantFonts"] = new CosArray([new CosDict(new Dictionary<string, CosObject> { ["W"] = new CosArray(widths) })]),
        });
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var info = PdfFontInfo.Load(file, font);

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5));
        Assert.Null(info.Width(65));
    }

    [Fact]
    public void PdfFile_RefusesObjectStreamsInsideObjectStreams()
    {
        // Object n lives in object stream n + 1, 100 000 levels deep: following the chain would
        // overflow the stack. Like PDFium, PaperKnife only reads object streams that are ordinary objects.
        const int Count = 100_000;
        var rows = new List<byte>();
        rows.AddRange([0, 0, 0, 0, 0]);
        for (var n = 1; n <= Count; n++)
        {
            rows.AddRange([2, (byte)((n + 1) >> 16), (byte)((n + 1) >> 8), (byte)(n + 1), 0]);
        }

        var header = "%PDF-1.7\n";
        var xrefNumber = Count + 2;
        rows.AddRange([1, 0, (byte)(header.Length >> 8), (byte)header.Length, 0]);
        var data = StreamFilters.Deflate([.. rows]);
        var pdf = Encoding.Latin1.GetBytes(header +
            $"{xrefNumber} 0 obj\n<< /Type /XRef /Size {xrefNumber + 1} /Index [0 {Count + 1} {xrefNumber} 1] /W [1 3 1] /Root 1 0 R /Filter /FlateDecode /Length {data.Length} >>\nstream\n")
            .Concat(data)
            .Concat(Encoding.Latin1.GetBytes($"\nendstream\nendobj\nstartxref\n{header.Length}\n%%EOF\n"))
            .ToArray();

        var file = PdfFile.TryOpen(pdf, out var problem);

        Assert.Null(problem);
        Assert.Null(file!.GetPages());
    }

    private static CosNumber Number(int value) => new(value, value.ToString(System.Globalization.CultureInfo.InvariantCulture));
}
