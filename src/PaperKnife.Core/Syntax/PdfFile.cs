namespace PaperKnife.Core.Syntax;

/// <summary>Where an object lives: free (type 0), at an offset (1), or inside an object stream (2).</summary>
internal readonly record struct XrefEntry(int Type, long Offset, int Generation, int StreamNumber, int Index);

/// <summary>A page of the page tree, with the resources it inherits.</summary>
internal sealed record PdfPageNode(int Number, int Generation, CosDict Dict, CosDict? Resources);

/// <summary>
/// A read-only view of a PDF file's structure (ISO 32000-1, 7.5): cross-reference tables and
/// streams, the trailer, indirect objects, object streams and the page tree. It reads only what it
/// is asked for, and returns null for anything it doesn't fully understand instead of guessing the
/// way PDFium's recovery does; callers then leave the file to PDFium.
/// </summary>
internal sealed class PdfFile
{
    /// <summary>The highest object number PDFium accepts (cpdf_parser.h, kMaxObjectNumber).</summary>
    public const int MaxObjectNumber = 24 * 1024 * 1024;

    private const int MaxSections = 1000;
    private const int MaxNesting = 32;

    private readonly Dictionary<int, XrefEntry> _xref = [];
    private readonly Dictionary<int, CosObject?> _objects = [];
    private readonly Dictionary<int, ObjectStream?> _objectStreams = [];
    private readonly Dictionary<(string Kind, int Number), object?> _derived = [];
    private readonly HashSet<int> _loading = [];

    private PdfFile(ReadOnlyMemory<byte> data) => Data = data;

    public ReadOnlyMemory<byte> Data { get; }

    /// <summary>The newest trailer dictionary (for a cross-reference stream, its dictionary).</summary>
    public CosDict Trailer { get; private set; } = new([]);

    /// <summary>The offset of the newest cross-reference section, which an update's /Prev names.</summary>
    public long StartXref { get; private set; }

    /// <summary>Whether the newest cross-reference section is a stream rather than a table.</summary>
    public bool HasXrefStream { get; private set; }

    /// <summary>One more than the highest object number in use.</summary>
    public int Size { get; private set; }

    public bool IsEncrypted => Trailer["Encrypt"] is not null;

    public static PdfFile? TryOpen(ReadOnlyMemory<byte> data, out string? problem)
    {
        var file = new PdfFile(data);
        problem = file.ReadCrossReferences();
        return problem is null ? file : null;
    }

    public CosObject? GetObject(int number)
    {
        if (_objects.TryGetValue(number, out var cached))
        {
            return cached;
        }

        // A chain of references (a stream length, an object stream) mustn't recurse without limit.
        if (_loading.Count > MaxNesting || !_loading.Add(number))
        {
            return null;
        }

        try
        {
            CosObject? value = null;
            if (_xref.TryGetValue(number, out var entry))
            {
                value = entry.Type switch
                {
                    1 => ReadObjectAt(entry.Offset) is { } read && read.Number == number ? read.Value : null,
                    2 => ReadFromObjectStream(entry.StreamNumber, entry.Index, number),
                    _ => null,
                };
            }

            _objects[number] = value;
            return value;
        }
        finally
        {
            _loading.Remove(number);
        }
    }

    /// <summary>Follows references; null for null objects, missing objects and reference loops.</summary>
    public CosObject? Resolve(CosObject? value)
    {
        for (var depth = 0; value is CosRef reference && depth < 32; depth++)
        {
            value = GetObject(reference.Number);
        }

        return value is CosNull or CosRef ? null : value;
    }

    public CosDict? ResolveDict(CosObject? value) => Resolve(value) as CosDict;

    public CosArray? ResolveArray(CosObject? value) => Resolve(value) as CosArray;

    public double? ResolveNumber(CosObject? value) => Resolve(value) is CosNumber n ? n.Value : null;

    public string? ResolveName(CosObject? value) => Resolve(value) is CosName n ? n.Value : null;

    /// <summary>
    /// A value derived from an indirect object, such as a font's character map, computed once
    /// however many resource names, fonts or pages refer to the object, until
    /// <see cref="ForgetDerived"/>. A reader kept open while pages are viewed calls that to
    /// bound what it keeps.
    /// </summary>
    public T? Derived<T>(string kind, int number, Func<T?> compute)
        where T : class
    {
        if (!_derived.TryGetValue((kind, number), out var value))
        {
            value = compute();
            _derived[(kind, number)] = value;
        }

        return value as T;
    }

    /// <summary>Forgets the values <see cref="Derived{T}"/> computed; they are computed again when needed.</summary>
    public void ForgetDerived() => _derived.Clear();

    /// <summary>
    /// The first <paramref name="length"/> bytes of the stream's decoded data, or null when it is
    /// shorter or can't be decoded. Flate data is decoded only that far.
    /// </summary>
    public byte[]? DecodeStreamPrefix(CosStream stream, int length, int limit)
    {
        var filter = Resolve(stream.Dict["Filter"]);
        if (stream.Dict["F"] is null && stream.Dict["DecodeParms"] is null
            && (filter is null || filter is CosName { Value: "FlateDecode" or "Fl" }))
        {
            var raw = Data.Span.Slice(stream.DataStart, stream.DataLength);
            var prefix = filter is null ? raw[..Math.Min(length, raw.Length)].ToArray() : StreamFilters.InflatePrefix(raw, length);
            return prefix is { } p && p.Length == length ? p : null;
        }

        return DecodeStream(stream, out _, limit) is { } data && data.Length >= length ? data[..length] : null;
    }

    /// <summary>The stream's decoded data, or null when it can't be decoded or would be longer than <paramref name="limit"/> bytes.</summary>
    public byte[]? DecodeStream(CosStream stream, out string? problem, int limit = StreamFilters.MaxDecodedLength)
    {
        var filters = new List<string>();
        var parameters = new List<CosDict?>();
        var filter = Resolve(stream.Dict["Filter"]);
        var decodeParameters = Resolve(stream.Dict["DecodeParms"]);
        if (stream.Dict["F"] is not null)
        {
            problem = "The stream's data is in an external file.";
            return null;
        }

        switch (filter)
        {
            case null:
                break;
            case CosName name:
                filters.Add(name.Value);
                parameters.Add(decodeParameters as CosDict);
                break;
            case CosArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    if (Resolve(array[i]) is not CosName name)
                    {
                        problem = "The stream has an unknown filter.";
                        return null;
                    }

                    filters.Add(name.Value);
                    parameters.Add(decodeParameters is CosArray all && i < all.Count ? ResolveDict(all[i]) : null);
                }

                break;
            default:
                problem = "The stream has an unknown filter.";
                return null;
        }

        return StreamFilters.Decode(Data.Span.Slice(stream.DataStart, stream.DataLength), filters, parameters, out problem, limit);
    }

    /// <summary>The pages in document order, or null when the page tree isn't sound.</summary>
    public IReadOnlyList<PdfPageNode>? GetPages()
    {
        if (ResolveDict(Trailer["Root"]) is not { } catalog || catalog["Pages"] is not CosRef root)
        {
            return null;
        }

        var pages = new List<PdfPageNode>();
        return CollectPages(root, null, pages, [], 0) ? pages : null;
    }

    /// <summary>The page's content streams, in order, or null when /Contents isn't a stream or an array of streams.</summary>
    public IReadOnlyList<CosRef>? GetContentStreams(CosDict page)
    {
        var contents = page["Contents"];
        if (contents is null)
        {
            return [];
        }

        if (contents is CosRef reference)
        {
            switch (GetObject(reference.Number))
            {
                case CosStream:
                    return [reference];
                case CosArray array:
                    contents = array;
                    break;
                default:
                    return null;
            }
        }

        if (contents is not CosArray items)
        {
            return null;
        }

        var streams = new List<CosRef>();
        foreach (var item in items.Items)
        {
            if (item is not CosRef r || GetObject(r.Number) is not CosStream)
            {
                return null;
            }

            streams.Add(r);
        }

        return streams;
    }

    private bool CollectPages(CosRef node, CosDict? inherited, List<PdfPageNode> pages, HashSet<int> visited, int depth)
    {
        if (depth > 64 || !visited.Add(node.Number) || GetObject(node.Number) is not CosDict dict)
        {
            return false;
        }

        var resources = dict["Resources"] is { } own ? ResolveDict(own) : inherited;
        var kids = ResolveArray(dict["Kids"]);
        var type = ResolveName(dict["Type"]);
        if (type == "Pages" || (type != "Page" && kids is not null))
        {
            return kids is not null && kids.Items.All(kid => kid is CosRef r && CollectPages(r, resources, pages, visited, depth + 1));
        }

        pages.Add(new PdfPageNode(node.Number, node.Generation, dict, resources));
        return true;
    }

    private string? ReadCrossReferences()
    {
        var span = Data.Span;
        var tail = Math.Max(0, span.Length - 4096);
        var at = span[tail..].LastIndexOf("startxref"u8);
        if (at < 0)
        {
            return "The file has no cross-reference offset.";
        }

        var offset = new PdfLexer(Data, tail + at + 9).Next();
        if (offset.Kind != PdfTokenKind.Number || offset.Number < 0)
        {
            return "The file's cross-reference offset is damaged.";
        }

        StartXref = (long)offset.Number;
        var pending = new Queue<long>([StartXref]);
        var visited = new HashSet<long>();
        var newest = true;
        while (pending.TryDequeue(out var position))
        {
            if (position < 0 || position >= span.Length || !visited.Add(position) || visited.Count > MaxSections)
            {
                return "The file's cross-reference sections are damaged.";
            }

            var isStream = !new PdfLexer(Data, (int)position).Peek().IsKeyword("xref");
            var problem = isStream ? ReadXrefStream(position, out var trailer) : ReadXrefTable(position, out trailer);
            if (problem is not null)
            {
                return problem;
            }

            if (newest)
            {
                Trailer = trailer!;
                HasXrefStream = isStream;
                newest = false;
            }

            if (!isStream && trailer!["XRefStm"] is CosNumber hybrid && (problem = ReadXrefStream((long)hybrid.Value, out _)) is not null)
            {
                return problem;
            }

            if (trailer!["Prev"] is CosNumber prev)
            {
                pending.Enqueue((long)prev.Value);
            }
        }

        var size = Trailer["Size"] is CosNumber s && s.IsInteger && s.Value is >= 0 and <= int.MaxValue ? (long)s.Value : 0;
        Size = (int)Math.Min(int.MaxValue, Math.Max(size, _xref.Count == 0 ? 1 : _xref.Keys.Max() + 1L));
        return Trailer["Root"] is CosRef ? null : "The file has no catalog.";
    }

    private string? ReadXrefTable(long position, out CosDict? trailer)
    {
        trailer = null;
        var lexer = new PdfLexer(Data, (int)position);
        lexer.Next();
        while (true)
        {
            var first = lexer.Next();
            if (first.IsKeyword("trailer"))
            {
                break;
            }

            var count = lexer.Next();
            if (!IsCount(first) || !IsCount(count))
            {
                return "The file's cross-reference table is damaged.";
            }

            for (var i = 0; i < (int)count.Number; i++)
            {
                var offset = lexer.Next();
                var generation = lexer.Next();
                var kind = lexer.Next();
                if (!IsCount(offset) || !IsCount(generation) || kind.Kind != PdfTokenKind.Keyword || kind.Text is not ("n" or "f"))
                {
                    return "The file's cross-reference table is damaged.";
                }

                _xref.TryAdd((int)first.Number + i, kind.Text == "n"
                    ? new XrefEntry(1, (long)offset.Number, (int)generation.Number, 0, 0)
                    : new XrefEntry(0, 0, (int)generation.Number, 0, 0));
            }
        }

        trailer = PdfObjectParser.Read(lexer) as CosDict;
        return trailer is null ? "The file's trailer is damaged." : null;
    }

    private string? ReadXrefStream(long position, out CosDict? trailer)
    {
        trailer = null;
        if (position < 0 || position >= Data.Length || ReadObjectAt(position)?.Value is not CosStream stream
            || ResolveName(stream.Dict["Type"]) != "XRef")
        {
            return "The file's cross-reference stream is damaged.";
        }

        var data = DecodeStream(stream, out var problem);
        if (data is null)
        {
            return problem;
        }

        if (stream.Dict["W"] is not CosArray { Count: 3 } w || w.Items.Any(x => x is not CosNumber { Value: >= 0 and <= 8 }))
        {
            return "The file's cross-reference stream is damaged.";
        }

        var widths = w.Items.Select(x => (int)((CosNumber)x).Value).ToArray();
        var size = stream.Dict["Size"] is CosNumber s ? (int)s.Value : 0;
        var index = stream.Dict["Index"] is CosArray i ? i.Items.Select(x => x is CosNumber n ? (int)n.Value : -1).ToArray() : [0, size];
        var entrySize = widths.Sum();
        if (entrySize == 0)
        {
            return "The file's cross-reference stream is damaged.";
        }

        var at = 0;
        for (var pair = 0; pair + 1 < index.Length; pair += 2)
        {
            if (index[pair] < 0 || index[pair + 1] < 0)
            {
                return "The file's cross-reference stream is damaged.";
            }

            for (var n = 0; n < index[pair + 1]; n++)
            {
                if (at + entrySize > data.Length)
                {
                    return "The file's cross-reference stream is damaged.";
                }

                var type = widths[0] == 0 ? 1 : Field(data, at, widths[0]);
                var second = Field(data, at + widths[0], widths[1]);
                var third = Field(data, at + widths[0] + widths[1], widths[2]);
                at += entrySize;
                var number = index[pair] + n;
                switch (type)
                {
                    case 0:
                        _xref.TryAdd(number, new XrefEntry(0, 0, (int)third, 0, 0));
                        break;
                    case 1:
                        _xref.TryAdd(number, new XrefEntry(1, second, (int)third, 0, 0));
                        break;
                    case 2:
                        _xref.TryAdd(number, new XrefEntry(2, 0, 0, (int)second, (int)third));
                        break;
                }
            }
        }

        trailer = stream.Dict;
        return null;
    }

    private static long Field(byte[] data, int start, int width)
    {
        long value = 0;
        for (var i = 0; i < width; i++)
        {
            value = (value << 8) | data[start + i];
        }

        return value;
    }

    private static bool IsCount(PdfToken token) =>
        token.Kind == PdfTokenKind.Number && token.Number >= 0 && !token.Text.Contains('.', StringComparison.Ordinal);

    /// <summary>Reads "n g obj ... endobj" at an offset; null when it isn't one or a stream's length is wrong.</summary>
    private (int Number, int Generation, CosObject Value)? ReadObjectAt(long position)
    {
        var span = Data.Span;
        if (position < 0 || position >= span.Length)
        {
            return null;
        }

        var lexer = new PdfLexer(Data, (int)position);
        var number = lexer.Next();
        var generation = lexer.Next();
        if (!IsCount(number) || !IsCount(generation) || !lexer.Next().IsKeyword("obj") || PdfObjectParser.Read(lexer) is not { } value)
        {
            return null;
        }

        if (value is CosDict dict && lexer.Peek().IsKeyword("stream"))
        {
            var start = lexer.Next().End;
            if (start < span.Length && span[start] == '\r')
            {
                start++;
            }

            if (start < span.Length && span[start] == '\n')
            {
                start++;
            }

            if (ResolveNumber(dict["Length"]) is not { } length || length < 0 || start + length > span.Length
                || !new PdfLexer(Data, start + (int)length).Next().IsKeyword("endstream"))
            {
                return null;
            }

            value = new CosStream(dict, start, (int)length);
        }

        return ((int)number.Number, (int)generation.Number, value);
    }

    private CosObject? ReadFromObjectStream(int streamNumber, int index, int number)
    {
        if (!_objectStreams.TryGetValue(streamNumber, out var parsed))
        {
            parsed = ParseObjectStream(streamNumber);
            _objectStreams[streamNumber] = parsed;
        }

        if (parsed is not { } objects)
        {
            return null;
        }

        var i = index >= 0 && index < objects.Numbers.Length && objects.Numbers[index] == number
            ? index
            : Array.IndexOf(objects.Numbers, number);
        return i < 0 ? null : PdfObjectParser.Read(new PdfLexer(objects.Data, objects.First + objects.Offsets[i]));
    }

    private ObjectStream? ParseObjectStream(int number)
    {
        // Like PDFium, an object stream must itself be an ordinary object, not inside another one.
        if (!_xref.TryGetValue(number, out var entry) || entry.Type != 1
            || GetObject(number) is not CosStream stream || ResolveName(stream.Dict["Type"]) != "ObjStm"
            || ResolveNumber(stream.Dict["N"]) is not { } count || ResolveNumber(stream.Dict["First"]) is not { } first
            || count < 0 || first < 0 || DecodeStream(stream, out _) is not { } data || count > data.Length / 4)
        {
            return null;
        }

        var lexer = new PdfLexer(data);
        var numbers = new int[(int)count];
        var offsets = new int[(int)count];
        for (var i = 0; i < numbers.Length; i++)
        {
            var objectNumber = lexer.Next();
            var offset = lexer.Next();
            if (!IsCount(objectNumber) || !IsCount(offset) || first + offset.Number >= data.Length)
            {
                return null;
            }

            numbers[i] = (int)objectNumber.Number;
            offsets[i] = (int)offset.Number;
        }

        return new ObjectStream(data, (int)first, numbers, offsets);
    }

    private sealed record ObjectStream(byte[] Data, int First, int[] Numbers, int[] Offsets);
}
