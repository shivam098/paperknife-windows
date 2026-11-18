using System.Text;
using PaperKnife.Core.Syntax;

namespace PaperKnife.Core.Content;

/// <summary>
/// What PaperKnife needs to know about a font to read and write its character codes: how codes
/// are split, their advance widths (as PDFium computes them), what text each code stands for, and,
/// for Type 3 fonts, which codes have a glyph.
/// </summary>
internal sealed class PdfFontInfo
{
    private const int MaxMappedCodes = 1 << 17;

    private readonly Dictionary<uint, string> _unicode = [];
    private readonly Dictionary<string, List<uint>> _codes = new(StringComparer.Ordinal);
    private readonly Dictionary<uint, string> _glyphNames = [];
    private readonly HashSet<string>? _charProcs;
    private readonly int[]? _simpleWidths;
    private readonly Dictionary<uint, int>? _cidWidths;
    private readonly int _defaultCidWidth = 1000;

    private PdfFontInfo(string subtype, int codeLength)
    {
        Subtype = subtype;
        CodeLength = codeLength;
    }

    public string Subtype { get; }

    /// <summary>Bytes per character code: 1 for simple fonts, 2 for Identity-H fonts, 0 when not supported.</summary>
    public int CodeLength { get; }

    public bool IsType3 => Subtype == "Type3";

    public static PdfFontInfo Load(PdfFile file, CosDict font)
    {
        var subtype = file.ResolveName(font["Subtype"]) ?? string.Empty;
        PdfFontInfo info;
        if (subtype == "Type0")
        {
            var identity = file.Resolve(font["Encoding"]) is CosName { Value: "Identity-H" };
            info = new PdfFontInfo(subtype, identity ? 2 : 0);
            var descendant = file.ResolveDict(file.ResolveArray(font["DescendantFonts"]) is { Count: > 0 } d ? d[0] : null);
            if (descendant is not null)
            {
                info = new PdfFontInfo(subtype, identity ? 2 : 0, (int)(file.ResolveNumber(descendant["DW"]) ?? 1000), CidWidths(file, file.ResolveArray(descendant["W"])));
            }
        }
        else
        {
            info = new PdfFontInfo(subtype, 1, file, font);
        }

        if (file.Resolve(font["ToUnicode"]) is CosStream map && file.DecodeStream(map, out _) is { } data)
        {
            ReadToUnicode(data, info._unicode);
        }

        foreach (var (code, name) in info._glyphNames)
        {
            if (!info._unicode.ContainsKey(code) && Encodings.GlyphToUnicode(name) is { } text)
            {
                info._unicode[code] = text;
            }
        }

        foreach (var (code, text) in info._unicode.OrderBy(e => e.Key))
        {
            if (!info._codes.TryGetValue(text, out var codes))
            {
                info._codes[text] = codes = [];
            }

            codes.Add(code);
        }

        return info;
    }

    private PdfFontInfo(string subtype, int codeLength, int defaultWidth, Dictionary<uint, int>? widths)
        : this(subtype, codeLength)
    {
        _defaultCidWidth = defaultWidth;
        _cidWidths = widths;
    }

    private PdfFontInfo(string subtype, int codeLength, PdfFile file, CosDict font)
        : this(subtype, codeLength)
    {
        ReadGlyphNames(file, font, subtype, _glyphNames);
        if (subtype == "Type3")
        {
            _charProcs = file.ResolveDict(font["CharProcs"])?.Entries.Keys.ToHashSet(StringComparer.Ordinal);
        }

        if (file.ResolveArray(font["Widths"]) is not { } widths)
        {
            return;
        }

        // Mirrors PDFium: Type 3 widths are scaled by the font matrix and rounded; other simple
        // fonts take whole numbers, and codes outside the array take /MissingWidth if present.
        _simpleWidths = Enumerable.Repeat(-1, 256).ToArray();
        var first = (int)(file.ResolveNumber(font["FirstChar"]) ?? 0);
        if (first is < 0 or > 255)
        {
            return;
        }

        if (subtype == "Type3")
        {
            var matrix = file.ResolveArray(font["FontMatrix"]);
            var scale = matrix is { Count: 6 } ? file.ResolveNumber(matrix[0]) ?? 0.001 : 0.001;
            for (var i = 0; i < widths.Count && first + i < 256; i++)
            {
                var width = (int)Math.Round((file.ResolveNumber(widths[i]) ?? 0) * scale * 1000, MidpointRounding.AwayFromZero);
                _simpleWidths[first + i] = width == 0 ? -1 : width;
            }

            return;
        }

        if (file.ResolveNumber(file.ResolveDict(font["FontDescriptor"])?["MissingWidth"]) is { } missing)
        {
            Array.Fill(_simpleWidths, (int)missing);
        }

        var last = (int)(file.ResolveNumber(font["LastChar"]) ?? 0);
        if (last == 0 || last >= first + widths.Count)
        {
            last = first + widths.Count - 1;
        }

        for (var code = first; code <= Math.Min(last, 255); code++)
        {
            _simpleWidths[code] = (int)(file.ResolveNumber(widths[code - first]) ?? 0);
        }
    }

    /// <summary>The advance width of a code for a font size of 1, or null when PaperKnife can't tell it.</summary>
    public double? Width(uint code)
    {
        if (_cidWidths is not null)
        {
            return (_cidWidths.TryGetValue(code, out var width) ? width : _defaultCidWidth) / 1000.0;
        }

        return _simpleWidths is not null && code < 256 && _simpleWidths[code] >= 0 ? _simpleWidths[code] / 1000.0 : null;
    }

    public string? Unicode(uint code) => _unicode.GetValueOrDefault(code);

    /// <summary>Whether a code draws a glyph. Only Type 3 fonts can tell; for others it is checked elsewhere.</summary>
    public bool HasGlyph(uint code) =>
        _charProcs is null || (_glyphNames.TryGetValue(code, out var name) && _charProcs.Contains(name));

    public List<uint> SplitCodes(ReadOnlySpan<byte> bytes)
    {
        var codes = new List<uint>(bytes.Length);
        if (CodeLength == 2)
        {
            for (var i = 0; i + 1 < bytes.Length; i += 2)
            {
                codes.Add((uint)((bytes[i] << 8) | bytes[i + 1]));
            }
        }
        else
        {
            foreach (var b in bytes)
            {
                codes.Add(b);
            }
        }

        return codes;
    }

    public byte[] JoinCodes(IReadOnlyList<uint> codes)
    {
        var bytes = new byte[codes.Count * CodeLength];
        for (var i = 0; i < codes.Count; i++)
        {
            if (CodeLength == 2)
            {
                bytes[2 * i] = (byte)(codes[i] >> 8);
                bytes[(2 * i) + 1] = (byte)codes[i];
            }
            else
            {
                bytes[i] = (byte)codes[i];
            }
        }

        return bytes;
    }

    /// <summary>
    /// Character codes that draw <paramref name="text"/>, preferring codes already used nearby.
    /// Characters without a code are reported in <paramref name="missing"/>.
    /// </summary>
    public List<uint> Encode(string text, IReadOnlySet<uint> preferred, out List<string> missing)
    {
        var codes = new List<uint>(text.Length);
        missing = [];
        foreach (var c in text)
        {
            var candidates = CodesFor(c.ToString());
            if (candidates.Count == 0 && c is ' ' or '\u00A0')
            {
                // Fonts often map their one space glyph to only one of the two.
                candidates = CodesFor(c == ' ' ? "\u00A0" : " ");
            }

            var usable = candidates.Where(HasGlyph).ToList();
            if (usable.Count == 0)
            {
                if (!missing.Contains(c.ToString()))
                {
                    missing.Add(c.ToString());
                }

                continue;
            }

            codes.Add(usable.FirstOrDefault(preferred.Contains, usable[0]));
        }

        return codes;
    }

    private IReadOnlyList<uint> CodesFor(string text) => _codes.TryGetValue(text, out var codes) ? codes : [];

    private static void ReadGlyphNames(PdfFile file, CosDict font, string subtype, Dictionary<uint, string> names)
    {
        string? baseEncoding = null;
        CosArray? differences = null;
        switch (file.Resolve(font["Encoding"]))
        {
            case CosName name:
                baseEncoding = name.Value;
                break;
            case CosDict dict:
                baseEncoding = file.ResolveName(dict["BaseEncoding"]);
                differences = file.ResolveArray(dict["Differences"]);
                break;
        }

        var baseFont = file.ResolveName(font["BaseFont"]) ?? string.Empty;
        Func<int, string?>? table = baseEncoding switch
        {
            "WinAnsiEncoding" => Encodings.WinAnsiName,
            "MacRomanEncoding" => Encodings.MacRomanName,
            "StandardEncoding" => Encodings.StandardName,
            null when subtype is "Type1" or "MMType1" && !baseFont.Contains("Symbol", StringComparison.Ordinal)
                && !baseFont.Contains("Dingbats", StringComparison.Ordinal) => Encodings.StandardName,
            _ => null,
        };
        if (table is not null)
        {
            for (var code = 0; code < 256; code++)
            {
                if (table(code) is { } name)
                {
                    names[(uint)code] = name;
                }
            }
        }

        var next = 0;
        foreach (var item in differences?.Items ?? [])
        {
            switch (item)
            {
                case CosNumber number:
                    next = (int)number.Value;
                    break;
                case CosName name when next is >= 0 and < 256:
                    names[(uint)next++] = name.Value;
                    break;
            }
        }
    }

    /// <summary>The widths in a /W array, or null when it is too large to read, so they count as unknown.</summary>
    private static Dictionary<uint, int>? CidWidths(PdfFile file, CosArray? array)
    {
        var widths = new Dictionary<uint, int>();
        if (array is null)
        {
            return widths;
        }

        // Counts steps rather than entries, so repeated ranges can't make this run for minutes.
        var budget = 2 * MaxMappedCodes;
        var i = 0;
        while (i + 1 < array.Count)
        {
            if (budget <= 0)
            {
                return null;
            }

            var first = (uint)(file.ResolveNumber(array[i]) ?? 0);
            if (file.Resolve(array[i + 1]) is CosArray list)
            {
                for (var j = 0; j < list.Count && budget-- > 0; j++)
                {
                    widths.TryAdd(first + (uint)j, (int)(file.ResolveNumber(list[j]) ?? 0));
                }

                i += 2;
            }
            else if (i + 2 < array.Count)
            {
                var last = (uint)(file.ResolveNumber(array[i + 1]) ?? 0);
                var width = (int)(file.ResolveNumber(array[i + 2]) ?? 0);
                if (last >= first && last - first < MaxMappedCodes)
                {
                    for (var k = 0u; k <= last - first && budget-- > 0; k++)
                    {
                        widths.TryAdd(first + k, width);
                    }
                }

                i += 3;
            }
            else
            {
                break;
            }
        }

        return widths;
    }

    /// <summary>Reads the bfchar and bfrange sections of a ToUnicode CMap (ISO 32000-1, 9.10.3).</summary>
    private static void ReadToUnicode(byte[] data, Dictionary<uint, string> map)
    {
        var lexer = new PdfLexer(data);
        var budget = 2 * MaxMappedCodes;
        while (budget > 0)
        {
            var token = lexer.Next();
            if (token.Kind == PdfTokenKind.End)
            {
                return;
            }

            if (token.IsKeyword("beginbfchar"))
            {
                while (true)
                {
                    var source = lexer.Next();
                    if (source.Kind != PdfTokenKind.String)
                    {
                        break;
                    }

                    var target = lexer.Next();
                    if (target.Kind == PdfTokenKind.String && source.Bytes!.Length is >= 1 and <= 4)
                    {
                        map.TryAdd(Code(source.Bytes), Text(target.Bytes!));
                    }
                }
            }
            else if (token.IsKeyword("beginbfrange"))
            {
                while (true)
                {
                    var low = lexer.Next();
                    if (low.Kind != PdfTokenKind.String)
                    {
                        break;
                    }

                    var high = lexer.Next();
                    var target = lexer.Next();
                    var from = Code(low.Bytes!);
                    var to = high.Kind == PdfTokenKind.String ? Code(high.Bytes!) : from;
                    var valid = to >= from && to - from < MaxMappedCodes && low.Bytes!.Length <= 4;
                    if (target.Kind == PdfTokenKind.String)
                    {
                        var units = Text(target.Bytes!).ToCharArray();
                        for (var k = 0u; valid && units.Length > 0 && k <= to - from && budget-- > 0; k++)
                        {
                            var copy = (char[])units.Clone();
                            copy[^1] = (char)(units[^1] + k);
                            map.TryAdd(from + k, new string(copy));
                        }
                    }
                    else if (target.Kind == PdfTokenKind.ArrayStart)
                    {
                        var code = from;
                        for (var item = lexer.Next(); item.Kind is not (PdfTokenKind.ArrayEnd or PdfTokenKind.End); item = lexer.Next())
                        {
                            if (valid && item.Kind == PdfTokenKind.String && code <= to)
                            {
                                map.TryAdd(code, Text(item.Bytes!));
                            }

                            code++;
                        }
                    }
                }
            }
        }
    }

    private static uint Code(byte[] bytes)
    {
        uint code = 0;
        foreach (var b in bytes)
        {
            code = (code << 8) | b;
        }

        return code;
    }

    private static string Text(byte[] utf16) => Encoding.BigEndianUnicode.GetString(utf16);
}
