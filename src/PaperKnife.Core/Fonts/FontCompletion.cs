using System.Globalization;
using System.Text;
using PaperKnife.Core.Content;
using PaperKnife.Core.Syntax;

namespace PaperKnife.Core.Fonts;

/// <summary>
/// A complete copy of a subset font, for new text that needs letters the subset lacks. TeX embeds
/// only the glyphs a document uses. When PaperKnife ships the font the subset was made from (see
/// <see cref="BundledFonts"/>), the edited text is drawn with a copy of the PDF's font that embeds
/// the whole, unmodified font program, added to the edited page as a font of its own. The PDF's
/// own font, and everything drawn with it on any page, stays exactly as it was.
/// </summary>
/// <remarks>
/// The copy keeps the font's character map and the glyph of every code its /Differences name or
/// the page's text draws with it; the subset must be identical to PaperKnife's font for every
/// glyph it has (see <see cref="Type1Font.DifferenceFrom"/>), so the letters an edit keeps look and
/// read the same. Other codes select the glyph the character map says they stand for (see
/// <see cref="GlyphNames"/>), and codes take the PDF's widths only where the PDF says they are
/// those glyphs' (see <see cref="CopyWidths"/>). Only symbolic fonts whose codes select glyphs
/// through the program's own encoding (and /Differences) qualify, as TeX's fonts do: there,
/// PDFium and the PDF specification agree on which glyph each code draws.
/// </remarks>
internal sealed class FontCompletion
{
    private const int Symbolic = 4;
    private const int Nonsymbolic = 32;
    private const int AllCap = 1 << 16;
    private const int MaxNameAttempts = 32;

    private readonly ContentPage _page;
    private readonly IReadOnlyDictionary<int, string> _glyphNames;

    // The codes whose glyph is the PDF's own, which stand for what its character map says.
    private readonly IReadOnlySet<int> _trusted;
    private readonly Dictionary<string, CosObject> _fontEntries;
    private readonly Dictionary<string, CosObject> _descriptorEntries;
    private readonly int _programNumber;
    private readonly int _descriptorNumber;
    private readonly int _fontNumber;

    private FontCompletion(
        ContentPage page,
        Type1Font full,
        string resourceName,
        PdfFontInfo info,
        bool reused,
        IReadOnlyDictionary<int, string> glyphNames,
        IReadOnlySet<int> trusted,
        Dictionary<string, CosObject> fontEntries,
        Dictionary<string, CosObject> descriptorEntries,
        int firstNumber)
    {
        _page = page;
        Full = full;
        ResourceName = resourceName;
        Info = info;
        Reused = reused;
        _glyphNames = glyphNames;
        _trusted = trusted;
        _fontEntries = fontEntries;
        _descriptorEntries = descriptorEntries;
        _programNumber = firstNumber;
        _descriptorNumber = firstNumber + 1;
        _fontNumber = firstNumber + 2;
    }

    /// <summary>The font's name without its subset tag, for example "CMBX10".</summary>
    public string FontName => Full.FontName;

    public Type1Font Full { get; }

    /// <summary>The name of the copy in the page's font resources, which the edited text selects.</summary>
    public string ResourceName { get; }

    /// <summary>The copy's character codes and widths.</summary>
    public PdfFontInfo Info { get; }

    /// <summary>Whether the page already has such a copy, from an earlier edit, so nothing needs to be added.</summary>
    public bool Reused { get; }

    /// <summary>
    /// A complete copy of the font the page's resource <paramref name="resourceName"/> names, or
    /// null when PaperKnife can't make one. <paramref name="difference"/> says why when PaperKnife
    /// ships a font of that name but can't use it.
    /// </summary>
    public static FontCompletion? TryCreate(ContentPage page, string resourceName, out string? difference)
    {
        difference = null;
        var file = page.File;
        var fonts = file.ResolveDict(page.Page.Resources?["Font"]);
        if (fonts?[resourceName] is not { } entry || file.ResolveDict(entry) is not { } font
            || Qualify(file, font, out difference) is not var (full, descriptor, flags))
        {
            return null;
        }

        if (ReadProgram(file, descriptor) is not { } embedded)
        {
            difference = "it couldn't read the font in this PDF";
            return null;
        }

        if (embedded.DifferenceFrom(full) is { } reason)
        {
            difference = $"in this PDF {reason}";
            return null;
        }

        var differences = Differences(file, font);
        var toUnicode = PdfFontInfo.ToUnicode(file, font["ToUnicode"]);
        var drawn = CodesDrawn(page, resourceName);
        var glyphNames = GlyphNames(full, differences, toUnicode, drawn);
        // The codes whose glyph is the PDF's own: those its /Differences name and those the page draws,
        // or, when it has no /Differences, every code the copy doesn't give another glyph.
        var trusted = differences.Count > 0
            ? drawn.Concat(differences.Keys).ToHashSet()
            : [.. Enumerable.Range(0, 256).Where(code => glyphNames.GetValueOrDefault(code) == Builtin(full, code))];
        if (CopyWidths(file, font, full, embedded, glyphNames, differences, toUnicode) is not { } widths)
        {
            return null;
        }

        var encoding = CopyEncoding(font, full, differences, glyphNames);

        // A copy an earlier edit added to this page serves as well, if it is exactly what this one would be.
        var programs = new Dictionary<int, bool>();
        foreach (var (name, value) in fonts.Entries)
        {
            if (name != resourceName && file.ResolveDict(value) is { } other && IsSameCopy(file, other, font, encoding, flags, full, widths, programs))
            {
                return new FontCompletion(page, full, name, PdfFontInfo.Load(file, other, code => Builtin(full, code)), reused: true, glyphNames, trusted, [], [], 0);
            }
        }

        // The copy takes the next three object numbers, which must stay within those PDFium reads.
        if (file.Size > PdfFile.MaxObjectNumber - 2)
        {
            difference = "the PDF has too many objects to add it";
            return null;
        }

        if (FreeName(page, fonts, $"PK{full.FontName}") is not { } resource)
        {
            return null;
        }

        var number = file.Size;
        var baseFont = $"{Tag(entry is CosRef reference ? reference.Number : 0)}+{full.FontName}";
        var descriptorEntries = new Dictionary<string, CosObject>(descriptor.Entries, StringComparer.Ordinal)
        {
            ["FontName"] = new CosName(baseFont),
            ["FontFile"] = new CosRef(number, 0),
        };
        descriptorEntries.Remove("CharSet");

        var fontEntries = new Dictionary<string, CosObject>(StringComparer.Ordinal)
        {
            ["Type"] = new CosName("Font"),
            ["Subtype"] = new CosName("Type1"),
            ["BaseFont"] = new CosName(baseFont),
            ["FirstChar"] = Integer(widths.First),
            ["LastChar"] = Integer(widths.First + widths.Values.Count - 1),
            ["Widths"] = new CosArray([.. widths.Values]),
        };
        if (encoding is not null)
        {
            fontEntries["Encoding"] = encoding;
        }

        if (font["ToUnicode"] is { } map)
        {
            fontEntries["ToUnicode"] = map;
        }

        // The copy as PdfFontInfo reads it, with its descriptor inline: the new objects don't exist yet.
        var readable = new Dictionary<string, CosObject>(fontEntries, StringComparer.Ordinal) { ["FontDescriptor"] = new CosDict(descriptorEntries) };
        fontEntries["FontDescriptor"] = new CosRef(number + 1, 0);
        var info = PdfFontInfo.Load(file, new CosDict(readable), code => Builtin(full, code));
        return new FontCompletion(page, full, resource, info, reused: false, glyphNames, trusted, fontEntries, descriptorEntries, number);
    }

    /// <summary>
    /// Whether the copy draws the glyph for <paramref name="text"/> for <paramref name="code"/>. A
    /// glyph that is the PDF's own (one it names or the page draws, or any it didn't remap in a font
    /// without /Differences) stands for what the PDF's character map says; any other is judged by
    /// its name.
    /// </summary>
    public bool Draws(uint code, string text) =>
        _glyphNames.TryGetValue((int)code, out var name) && Full.CharStrings.ContainsKey(name) && (_trusted.Contains((int)code) || StandsFor(name, text));

    /// <summary>
    /// Whether the font the page's resource <paramref name="resourceName"/> names selects its glyphs
    /// by name in a way PaperKnife reads exactly: a symbolic Type 1 font, embedded as a Type 1
    /// program, whose codes select glyphs through the program's own encoding and /Differences, as
    /// pdfTeX's fonts do. Its glyph names then say which glyph each code draws, in PDFium as in
    /// PaperKnife, whether or not PaperKnife ships the font.
    /// </summary>
    public static bool SelectsGlyphsByName(ContentPage page, string resourceName)
    {
        var file = page.File;
        return file.ResolveDict(file.ResolveDict(page.Page.Resources?["Font"])?[resourceName]) is { } font
            && file.ResolveName(font["Subtype"]) == "Type1" && Describes(file, font) is var (descriptor, _)
            && descriptor["FontFile"] is not null && descriptor["FontFile2"] is null && descriptor["FontFile3"] is null;
    }

    /// <summary>Whether a glyph name stands for <paramref name="text"/> (see <see cref="Comparable"/>).</summary>
    private static bool StandsFor(string name, string text) =>
        Encodings.GlyphToUnicode(name) is { } unicode && Comparable(unicode) == Comparable(text);

    /// <summary>
    /// The objects to append: the font program, the copy's descriptor and font dictionary, and a
    /// new version of the page that lists the copy among its fonts. Nothing when the copy exists.
    /// </summary>
    public IReadOnlyList<ObjectUpdate> Updates()
    {
        if (Reused)
        {
            return [];
        }

        var file = _page.File;
        var page = _page.Page;
        var fonts = new Dictionary<string, CosObject>(file.ResolveDict(page.Resources?["Font"])!.Entries, StringComparer.Ordinal)
        {
            [ResourceName] = new CosRef(_fontNumber, 0),
        };

        // The page gets resources of its own, so pages that share or inherit them don't change.
        var resources = new Dictionary<string, CosObject>(page.Resources!.Entries, StringComparer.Ordinal) { ["Font"] = new CosDict(fonts) };
        var pageEntries = new Dictionary<string, CosObject>(page.Dict.Entries, StringComparer.Ordinal) { ["Resources"] = new CosDict(resources) };
        return
        [
            new StreamUpdate(_programNumber, 0, Full.Program, new Dictionary<string, CosObject>
            {
                ["Length1"] = Integer(Full.Length1),
                ["Length2"] = Integer(Full.Length2),
                ["Length3"] = Integer(Full.Length3),
            }),
            new ValueUpdate(_descriptorNumber, 0, new CosDict(_descriptorEntries)),
            new ValueUpdate(_fontNumber, 0, new CosDict(_fontEntries)),
            new ValueUpdate(page.Number, page.Generation, new CosDict(pageEntries)),
        ];
    }

    /// <summary>Whether the font has no /Encoding, or one that only lists /Differences, so its program's encoding is the base.</summary>
    private static bool HasProgramEncoding(PdfFile file, CosDict font) =>
        font["Encoding"] is null
        || (file.Resolve(font["Encoding"]) is CosDict encoding && encoding.Entries.Keys.All(k => k is "Type" or "Differences"));

    private static Type1Font? ReadProgram(PdfFile file, CosDict descriptor)
    {
        if (descriptor["FontFile2"] is not null || descriptor["FontFile3"] is not null
            || file.Resolve(descriptor["FontFile"]) is not CosStream stream
            || file.ResolveNumber(stream.Dict["Length1"]) is not { } length1 || length1 is < 1 or > Type1Font.MaxProgram
            || file.ResolveNumber(stream.Dict["Length2"]) is not { } length2 || length2 is < 1 or > Type1Font.MaxProgram
            || file.DecodeStream(stream, out _, Type1Font.MaxProgram) is not { } data)
        {
            return null;
        }

        return Type1Font.FromEmbedded(data, (int)length1, (int)length2);
    }

    /// <summary>The glyph names the font's /Differences give codes, in order, as PDFium reads them.</summary>
    private static Dictionary<int, string> Differences(PdfFile file, CosDict font)
    {
        var names = new Dictionary<int, string>();
        var next = 0;
        foreach (var item in file.ResolveArray(file.ResolveDict(font["Encoding"])?["Differences"])?.Items ?? [])
        {
            switch (file.Resolve(item))
            {
                case CosNumber number:
                    next = (int)number.Value;
                    break;
                case CosName name when next is >= 0 and < 256:
                    names[next++] = name.Value;
                    break;
            }
        }

        return names;
    }

    /// <summary>
    /// The glyph each code of the copy selects. Every code the page's text draws with the font, and
    /// every code its /Differences name, keeps its glyph: the copy is only for this page, and the
    /// letters an edit keeps are drawn with it, with the codes they had. pdfTeX names only the codes
    /// a document uses, though its character map (and its widths) follow TeX's encoding for every
    /// code; at the others the full program draws the glyphs of its own encoding, which differs.
    /// So when the character map says such a code stands for a character the program's glyph
    /// isn't, the copy selects the program's glyph for exactly that character, if there is one.
    /// </summary>
    /// <param name="drawn">The codes the page's text draws with the font.</param>
    private static Dictionary<int, string> GlyphNames(
        Type1Font full, Dictionary<int, string> differences, IReadOnlyDictionary<uint, string>? toUnicode, HashSet<int> drawn)
    {
        var names = new Dictionary<int, string>();
        Dictionary<string, string>? byText = null;
        for (var code = 0; code < 256; code++)
        {
            var name = differences.GetValueOrDefault(code) ?? Builtin(full, code);
            if (!differences.ContainsKey(code) && !drawn.Contains(code)
                && toUnicode?.GetValueOrDefault((uint)code) is { Length: > 0 } text
                && !(name is not null && full.CharStrings.ContainsKey(name) && StandsFor(name, text))
                && (byText ??= GlyphsByText(full)).GetValueOrDefault(text.Normalize(NormalizationForm.FormC)) is { } glyph)
            {
                name = glyph;
            }

            if (name is not null)
            {
                names[code] = name;
            }
        }

        return names;
    }

    /// <summary>
    /// The codes the page's text draws with the font a resource names, under that name or any
    /// other the page gives the same font.
    /// </summary>
    private static HashSet<int> CodesDrawn(ContentPage page, string resourceName)
    {
        var font = page.Text.Shows.FirstOrDefault(s => s.FontName == resourceName)?.Font;
        return page.Text.Shows.Where(s => s.Font is not null && (s.FontName == resourceName || ReferenceEquals(s.Font, font)))
            .SelectMany(s => s.Codes()).Select(c => (int)c.Code).ToHashSet();
    }

    /// <summary>
    /// The full program, descriptor and flags of a font PaperKnife may make a copy of: a Type 1 font
    /// it ships, symbolic, whose /Encoding is absent or lists only /Differences. Null otherwise;
    /// <paramref name="difference"/> then says why when PaperKnife ships a font of that name.
    /// </summary>
    private static (Type1Font Full, CosDict Descriptor, int Flags)? Qualify(PdfFile file, CosDict font, out string? difference)
    {
        difference = null;
        if (file.ResolveName(font["Subtype"]) != "Type1"
            || BundledFonts.FindType1(Type1Font.BaseName(file.ResolveName(font["BaseFont"]) ?? string.Empty)) is not { } full)
        {
            return null;
        }

        if (Describes(file, font) is not var (descriptor, flags))
        {
            difference = "the PDF describes the font in a way PaperKnife can't add letters to yet";
            return null;
        }

        return (full, descriptor, flags);
    }

    /// <summary>
    /// The descriptor and flags of a symbolic font whose /Encoding is absent or lists only
    /// /Differences, so that its codes select glyphs through its program's own encoding. Null for
    /// any other font.
    /// </summary>
    private static (CosDict Descriptor, int Flags)? Describes(PdfFile file, CosDict font)
    {
        var descriptor = file.ResolveDict(font["FontDescriptor"]);
        var flags = (int)(file.ResolveNumber(descriptor?["Flags"]) ?? 0);
        return descriptor is null || (flags & Symbolic) == 0 || (flags & (Nonsymbolic | AllCap)) != 0 || !HasProgramEncoding(file, font)
            ? null
            : (descriptor, flags);
    }

    /// <summary>
    /// The full program's glyphs by the exact text they stand for: a look-alike, such as ε for ϵ,
    /// won't do. Variants (names with a suffix, such as "a.sc") are left out: a character map can't
    /// ask for them. The first name in ordinal order wins, so the copy is the same every time.
    /// </summary>
    private static Dictionary<string, string> GlyphsByText(Type1Font full)
    {
        var glyphs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in full.CharStrings.Keys.Where(n => !n.Contains('.', StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            if (Encodings.GlyphToUnicode(name) is { Length: > 0 } text)
            {
                glyphs.TryAdd(text.Normalize(NormalizationForm.FormC), name);
            }
        }

        return glyphs;
    }

    /// <summary>
    /// The copy's /Encoding: the font's own when the copy selects the same glyphs as it would, or
    /// else its /Differences with the codes <see cref="GlyphNames"/> gave other glyphs.
    /// </summary>
    private static CosObject? CopyEncoding(CosDict font, Type1Font full, Dictionary<int, string> differences, Dictionary<int, string> names)
    {
        var added = names.Where(n => !differences.ContainsKey(n.Key) && n.Value != Builtin(full, n.Key)).ToList();
        if (added.Count == 0)
        {
            return font["Encoding"];
        }

        var items = new List<CosObject>();
        var previous = -2;
        foreach (var (code, name) in differences.Concat(added).OrderBy(n => n.Key))
        {
            if (code != previous + 1)
            {
                items.Add(Integer(code));
            }

            items.Add(new CosName(name));
            previous = code;
        }

        return new CosDict(new Dictionary<string, CosObject>(StringComparer.Ordinal)
        {
            ["Type"] = new CosName("Encoding"),
            ["Differences"] = new CosArray(items),
        });
    }

    /// <summary>
    /// The copy's /Widths. A code keeps the font's width when the PDF says it is that of the glyph
    /// the copy selects: /Differences names the glyph, or the font has none, so its codes select
    /// its program's glyphs; or the character map says the code stands for the glyph's character,
    /// as pdfTeX gives every code the width of TeX's glyph for it. Other codes take the glyph's
    /// width in the full program, so new letters advance as the font intends: some tools give the
    /// glyphs they leave out of a subset a width of 0, and pdfTeX's widths may be for another glyph.
    /// </summary>
    private static (int First, List<CosNumber> Values)? CopyWidths(
        PdfFile file,
        CosDict font,
        Type1Font full,
        Type1Font embedded,
        Dictionary<int, string> glyphNames,
        Dictionary<int, string> differences,
        IReadOnlyDictionary<uint, string>? toUnicode)
    {
        if (file.ResolveArray(font["Widths"]) is not { Count: > 0 } array || file.ResolveNumber(font["FirstChar"]) is not { } firstChar
            || firstChar is < 0 or > 255)
        {
            return null;
        }

        // The codes /Widths covers, as PdfFontInfo and PDFium read them.
        var first = (int)firstChar;
        var lastChar = (int)(file.ResolveNumber(font["LastChar"]) ?? 0);
        var last = Math.Min(255, lastChar == 0 || lastChar >= first + array.Count ? first + array.Count - 1 : lastChar);
        var drawn = glyphNames.Where(g => full.CharStrings.ContainsKey(g.Value)).Select(g => g.Key).ToList();
        var low = drawn.Count > 0 ? Math.Min(first, drawn.Min()) : first;
        var high = drawn.Count > 0 ? Math.Max(last, drawn.Max()) : last;
        var values = new List<CosNumber>();
        for (var code = low; code <= high; code++)
        {
            var name = glyphNames.GetValueOrDefault(code);
            var own = code >= first && code <= last ? file.Resolve(array[code - first]) as CosNumber ?? Integer(0) : null;
            bool keep;
            if (own is null)
            {
                keep = false;
            }
            else if (differences.Count == 0 || differences.ContainsKey(code))
            {
                // A glyph the subset has keeps its width, even 0: the PDF's text is drawn with it.
                keep = (int)own.Value > 0 || name is null || embedded.CharStrings.ContainsKey(name);
            }
            else
            {
                keep = (int)own.Value > 0 && name is not null && toUnicode?.GetValueOrDefault((uint)code) is { } text && StandsFor(name, text);
            }

            if (keep)
            {
                values.Add(own!);
            }
            else
            {
                var width = name is null ? null : full.Width(name);
                values.Add(Integer(width is { } w && double.IsFinite(w) ? (int)Math.Round(w) : 0));
            }
        }

        return (low, values);
    }

    /// <summary>
    /// Whether a font of the page is exactly the copy this completion would add: the same full
    /// program, encoding, character map and flags, and the same widths for every code the copy covers.
    /// </summary>
    /// <param name="encoding">The copy's /Encoding (see <see cref="CopyEncoding"/>).</param>
    /// <param name="programs">Font programs already compared with the full one, by object number.</param>
    private static bool IsSameCopy(
        PdfFile file,
        CosDict other,
        CosDict font,
        CosObject? encoding,
        int flags,
        Type1Font full,
        (int First, List<CosNumber> Values) widths,
        Dictionary<int, bool> programs)
    {
        var descriptor = file.ResolveDict(other["FontDescriptor"]);
        if (file.ResolveName(other["Subtype"]) != "Type1" || Type1Font.BaseName(file.ResolveName(other["BaseFont"]) ?? string.Empty) != full.FontName
            || descriptor is null || (int)(file.ResolveNumber(descriptor["Flags"]) ?? 0) != flags
            || !SameEntry(file, other["Encoding"], encoding) || !SameEntry(file, other["ToUnicode"], font["ToUnicode"])
            || descriptor["FontFile2"] is not null || descriptor["FontFile3"] is not null
            || !IsFullProgram(file, descriptor["FontFile"], full, programs))
        {
            return false;
        }

        var theirs = ReadWidths(file, other);
        for (var i = 0; i < widths.Values.Count; i++)
        {
            if (!theirs.TryGetValue(widths.First + i, out var width) || width != (int)widths.Values[i].Value)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Whether an embedded program is exactly the full font's. Each stream is decoded once, and
    /// no further than the full program's length.
    /// </summary>
    private static bool IsFullProgram(PdfFile file, CosObject? entry, Type1Font full, Dictionary<int, bool> programs)
    {
        if (entry is CosRef known && programs.TryGetValue(known.Number, out var same))
        {
            return same;
        }

        same = file.Resolve(entry) is CosStream stream
            && file.ResolveNumber(stream.Dict["Length1"]) == full.Length1 && file.ResolveNumber(stream.Dict["Length2"]) == full.Length2
            && file.DecodeStream(stream, out _, full.Program.Length) is { } data && data.AsSpan().SequenceEqual(full.Program);
        if (entry is CosRef reference)
        {
            programs[reference.Number] = same;
        }

        return same;
    }

    // Whole-number widths by code, as PDFium reads /Widths.
    private static Dictionary<int, int> ReadWidths(PdfFile file, CosDict font)
    {
        var widths = new Dictionary<int, int>();
        if (file.ResolveArray(font["Widths"]) is { } array && file.ResolveNumber(font["FirstChar"]) is { } first and >= 0 and <= 255)
        {
            var lastChar = (int)(file.ResolveNumber(font["LastChar"]) ?? 0);
            var last = Math.Min(255, lastChar == 0 || lastChar >= first + array.Count ? (int)first + array.Count - 1 : lastChar);
            for (var code = (int)first; code <= last; code++)
            {
                widths[code] = (int)(file.ResolveNumber(array[code - (int)first]) ?? 0);
            }
        }

        return widths;
    }

    // The same object, or the same value.
    private static bool SameEntry(PdfFile file, CosObject? a, CosObject? b)
    {
        if (a is null || b is null)
        {
            return a is null && b is null;
        }

        if (a is CosRef x && b is CosRef y)
        {
            return x.Number == y.Number && x.Generation == y.Generation;
        }

        return file.Resolve(a) is { } first and not CosStream && file.Resolve(b) is { } second and not CosStream
            && CosWriter.ToText(first) == CosWriter.ToText(second);
    }

    /// <summary>A name for the copy that neither the page's fonts nor its content use yet.</summary>
    private static string? FreeName(ContentPage page, CosDict fonts, string stem)
    {
        var used = page.Ops.SelectMany(op => op.Operands).OfType<CosName>().Select(n => n.Value).ToHashSet(StringComparer.Ordinal);
        for (var attempt = 0; attempt < MaxNameAttempts; attempt++)
        {
            var name = attempt == 0 ? stem : string.Create(CultureInfo.InvariantCulture, $"{stem}_{attempt}");
            if (fonts[name] is null && !used.Contains(name))
            {
                return name;
            }
        }

        return null;
    }

    /// <summary>A subset tag for the copy, "PK" and four letters from the original font's object number.</summary>
    private static string Tag(int number)
    {
        var letters = new char[4];
        for (var i = 3; i >= 0; i--)
        {
            letters[i] = (char)('A' + (Math.Abs(number) % 26));
            number /= 26;
        }

        return "PK" + new string(letters);
    }

    /// <summary>The glyph a font program's own encoding gives a code.</summary>
    private static string? Builtin(Type1Font font, int code) =>
        font.UsesStandardEncoding ? Encodings.FullStandardName(code) : font.Encoding.GetValueOrDefault(code);

    private static string Comparable(string text) => text.Normalize(NormalizationForm.FormKC).Replace('\u00A0', ' ');

    private static CosNumber Integer(int value) => new(value, value.ToString(CultureInfo.InvariantCulture));
}
