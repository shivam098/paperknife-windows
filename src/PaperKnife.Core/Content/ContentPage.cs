using PaperKnife.Core.Syntax;

namespace PaperKnife.Core.Content;

/// <summary>A page's decoded content and the text-showing operations in it, ready for content edits.</summary>
internal sealed class ContentPage
{
    public required PdfFile File { get; init; }

    public required PdfPageNode Page { get; init; }

    /// <summary>The page's content streams; an edit writes the whole content into the first.</summary>
    public required IReadOnlyList<CosRef> Streams { get; init; }

    /// <summary>The decoded content streams, joined by spaces as PDFium joins them.</summary>
    public required byte[] Content { get; init; }

    public required IReadOnlyList<ContentOp> Ops { get; init; }

    /// <summary>The text layout using only widths from font dictionaries.</summary>
    public required ContentTextLayout Text { get; init; }

    /// <summary>The page's content, or null when PaperKnife can't read it with certainty.</summary>
    public static ContentPage? TryLoad(PdfFile file, IReadOnlyList<PdfPageNode> pages, int pageIndex)
    {
        var page = pages[pageIndex];
        if (file.GetContentStreams(page.Dict) is not { } streams
            || streams.Select(s => s.Number).Distinct().Count() != streams.Count)
        {
            return null;
        }

        // Writing a new version of a stream that another page also draws would change that page too.
        var own = streams.Select(s => s.Number).ToHashSet();
        for (var i = 0; i < pages.Count; i++)
        {
            if (i != pageIndex && ContentNumbers(file, pages[i].Dict).Any(own.Contains))
            {
                return null;
            }
        }

        // PDFium joins a page's content streams with one space; anything else would let a
        // comment at the end of one stream end differently than PDFium reads it.
        var content = new MemoryStream();
        foreach (var reference in streams)
        {
            if (file.GetObject(reference.Number) is not CosStream stream || file.DecodeStream(stream, out _) is not { } data
                || content.Length + data.Length > StreamFilters.MaxDecodedLength)
            {
                return null;
            }

            content.Write(data);
            content.WriteByte((byte)' ');
        }

        var bytes = content.ToArray();
        if (ContentParser.Parse(bytes) is not { } ops)
        {
            return null;
        }

        return new ContentPage
        {
            File = file,
            Page = page,
            Streams = streams,
            Content = bytes,
            Ops = ops,
            Text = TextShowSimulator.Run(ops, file, page.Resources),
        };
    }

    private static IEnumerable<int> ContentNumbers(PdfFile file, CosDict page)
    {
        var contents = page["Contents"];
        if (contents is CosRef reference && file.GetObject(reference.Number) is CosArray indirect)
        {
            contents = indirect;
        }

        return contents switch
        {
            CosRef r => [r.Number],
            CosArray array => array.Items.OfType<CosRef>().Select(r => r.Number),
            _ => [],
        };
    }
}
