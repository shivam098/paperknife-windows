using System.Globalization;
using System.Text;

namespace PaperKnife.Core.Syntax;

/// <summary>A new version of an existing stream object, with its decoded data.</summary>
internal sealed record StreamUpdate(int Number, int Generation, byte[] Data);

/// <summary>
/// Appends an incremental update to a PDF (ISO 32000-1, 7.5.6): new versions of some streams and a
/// cross-reference section pointing to them. Every byte of the original file stays as it was.
/// </summary>
internal static class IncrementalUpdate
{
    public static byte[] Append(PdfFile file, IReadOnlyList<StreamUpdate> updates)
    {
        var output = new MemoryStream(file.Data.Length + updates.Sum(u => u.Data.Length / 2) + 1024);
        output.Write(file.Data.Span);
        var last = file.Data.Span[^1];
        if (last is not ((byte)'\n' or (byte)'\r'))
        {
            Write(output, "\n");
        }

        var entries = new List<(int Number, int Generation, long Offset)>();
        foreach (var update in updates.OrderBy(u => u.Number))
        {
            var data = StreamFilters.Deflate(update.Data);
            entries.Add((update.Number, update.Generation, output.Position));
            Write(output, string.Create(CultureInfo.InvariantCulture, $"{update.Number} {update.Generation} obj\n<</Length {data.Length}/Filter/FlateDecode>>\nstream\n"));
            output.Write(data);
            Write(output, "\nendstream\nendobj\n");
        }

        var size = Math.Max(file.Size, entries.Max(e => e.Number) + 1);
        if (file.HasXrefStream)
        {
            WriteXrefStream(output, file, entries, size);
        }
        else
        {
            WriteXrefTable(output, file, entries, size);
        }

        return output.ToArray();
    }

    private static void WriteXrefTable(MemoryStream output, PdfFile file, List<(int Number, int Generation, long Offset)> entries, int size)
    {
        var start = output.Position;
        var text = new StringBuilder("xref\n");
        foreach (var (number, generation, offset) in entries)
        {
            text.Append(CultureInfo.InvariantCulture, $"{number} 1\n{offset:D10} {generation:D5} n\r\n");
        }

        text.Append("trailer\n");
        CosWriter.Write(text, Trailer(file, size));
        text.Append(CultureInfo.InvariantCulture, $"\nstartxref\n{start}\n%%EOF\n");
        Write(output, text.ToString());
    }

    private static void WriteXrefStream(MemoryStream output, PdfFile file, List<(int Number, int Generation, long Offset)> entries, int size)
    {
        // The cross-reference stream is a new object, listed in itself.
        var number = size;
        var start = output.Position;
        var all = entries.Append((number, 0, start)).ToList();
        var offsetWidth = all.Max(e => e.Item3) > uint.MaxValue ? 8 : 4;
        var data = new byte[all.Count * (1 + offsetWidth + 2)];
        var at = 0;
        foreach (var (_, generation, offset) in all)
        {
            data[at++] = 1;
            for (var i = offsetWidth - 1; i >= 0; i--)
            {
                data[at++] = (byte)(offset >> (8 * i));
            }

            data[at++] = (byte)(generation >> 8);
            data[at++] = (byte)generation;
        }

        var dict = Trailer(file, number + 1);
        dict.Entries["Type"] = new CosName("XRef");
        dict.Entries["Index"] = new CosArray([.. all.SelectMany(e => new CosObject[] { Integer(e.Item1), Integer(1) })]);
        dict.Entries["W"] = new CosArray([Integer(1), Integer(offsetWidth), Integer(2)]);
        dict.Entries["Length"] = Integer(data.Length);
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"{number} 0 obj\n");
        CosWriter.Write(text, dict);
        text.Append("\nstream\n");
        Write(output, text.ToString());
        output.Write(data);
        Write(output, string.Create(CultureInfo.InvariantCulture, $"\nendstream\nendobj\nstartxref\n{start}\n%%EOF\n"));
    }

    private static CosDict Trailer(PdfFile file, int size)
    {
        var dict = new CosDict(new Dictionary<string, CosObject>(StringComparer.Ordinal)
        {
            ["Size"] = Integer(size),
        });
        foreach (var key in new[] { "Root", "Info", "ID" })
        {
            if (file.Trailer[key] is { } value)
            {
                dict.Entries[key] = value;
            }
        }

        dict.Entries["Prev"] = new CosNumber(file.StartXref, file.StartXref.ToString(CultureInfo.InvariantCulture));
        return dict;
    }

    private static CosNumber Integer(long value) => new(value, value.ToString(CultureInfo.InvariantCulture));

    private static void Write(MemoryStream output, string text) => output.Write(Encoding.Latin1.GetBytes(text));
}
