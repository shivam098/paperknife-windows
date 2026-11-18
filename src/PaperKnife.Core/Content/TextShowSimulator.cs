using PaperKnife.Core.Syntax;

namespace PaperKnife.Core.Content;

/// <summary>A text-showing operation (Tj, TJ, ' or ") and the text state it runs in.</summary>
internal sealed class ShowOp
{
    public required int OpIndex { get; init; }

    /// <summary>The BT/ET block it belongs to, or -1 outside one.</summary>
    public required int Block { get; init; }

    public required PdfMatrix Ctm { get; init; }

    /// <summary>The matrix last set by Tm (or BT); positions are relative to it, as in PDFium.</summary>
    public required PdfMatrix TextMatrix { get; init; }

    /// <summary>Where the operation starts in the text matrix's space, before any initial TJ adjustment.</summary>
    public required double X { get; init; }

    public required double Y { get; init; }

    public required bool PositionKnown { get; init; }

    public required string? FontName { get; init; }

    public required PdfFontInfo? Font { get; init; }

    public required double FontSize { get; init; }

    public required double CharSpace { get; init; }

    public required double WordSpace { get; init; }

    public required double HorizontalScale { get; init; }

    public required double Rise { get; init; }

    /// <summary>The TJ adjustment before the first string, in thousandths of an em.</summary>
    public required double InitialKerning { get; init; }

    /// <summary>The non-empty strings shown, each with the TJ adjustment that follows it.</summary>
    public required IReadOnlyList<(byte[] Bytes, double KerningAfter)> Segments { get; init; }

    /// <summary>Whether marked content around it replaces its text (/ActualText) or can't be checked.</summary>
    public required bool InReplacedText { get; init; }

    /// <summary>The index of the text object PDFium creates for it, or -1 when it creates none.</summary>
    public int Ordinal { get; set; } = -1;

    public bool ProducesObject => Ordinal >= 0;

    /// <summary>Where the first glyph starts, after the initial TJ adjustment.</summary>
    public double GlyphX => X - (InitialKerning / 1000 * FontSize * HorizontalScale);

    /// <summary>The text matrix to set before repeating this operation unchanged.</summary>
    public PdfMatrix OpMatrix => PdfMatrix.Translation(X, Y).Then(TextMatrix);

    /// <summary>The text matrix to set before showing new text in its place.</summary>
    public PdfMatrix GlyphMatrix => PdfMatrix.Translation(GlyphX, Y).Then(TextMatrix);

    /// <summary>The first glyph's origin in page space, where PDFium places the text object.</summary>
    public (double X, double Y) PageStart => TextMatrix.Then(Ctm).Transform(GlyphX, Y + Rise);

    /// <summary>The matrix PDFium reports for the text object, without its translation.</summary>
    public PdfMatrix ObjectMatrix => new PdfMatrix(HorizontalScale, 0, 0, 1, 0, 0).Then(TextMatrix).Then(Ctm);

    /// <summary>The codes shown, in order, each with the TJ adjustment that follows it (0 inside a string).</summary>
    public List<(uint Code, double KerningAfter)> Codes()
    {
        var codes = new List<(uint, double)>();
        if (Font is null)
        {
            return codes;
        }

        foreach (var (bytes, kerning) in Segments)
        {
            var split = Font.SplitCodes(bytes);
            for (var i = 0; i < split.Count; i++)
            {
                codes.Add((split[i], i == split.Count - 1 ? kerning : 0));
            }
        }

        return codes;
    }
}

internal sealed class TextBlock(int begin)
{
    /// <summary>The index of the BT operation.</summary>
    public int Begin { get; } = begin;

    /// <summary>The index of the ET operation, or -1 when the block isn't closed.</summary>
    public int End { get; set; } = -1;

    /// <summary>Whether it changes the graphics state in ways text blocks shouldn't (q, Q, cm).</summary>
    public bool Irregular { get; set; }
}

/// <summary>The text-showing operations of a page's content, as PDFium interprets them.</summary>
internal sealed class ContentTextLayout
{
    public required IReadOnlyList<ShowOp> Shows { get; init; }

    public required IReadOnlyList<TextBlock> Blocks { get; init; }

    /// <summary>The operations that create text objects, in the order PDFium creates them.</summary>
    public required IReadOnlyList<ShowOp> ObjectShows { get; init; }
}

/// <summary>
/// Follows the graphics and text state through page content the way PDFium's content parser does
/// (core/fpdfapi/page/cpdf_streamcontentparser.cpp): which text-showing operations create text
/// objects, and where each starts. Advances need glyph widths; where a width is unknown, positions
/// that depend on it are marked unknown.
/// </summary>
internal static class TextShowSimulator
{
    public static ContentTextLayout Run(
        IReadOnlyList<ContentOp> ops,
        PdfFile file,
        CosDict? resources,
        Func<ShowOp, uint, double?>? width = null)
    {
        width ??= (show, code) => show.Font?.Width(code);
        var fontResources = file.ResolveDict(resources?["Font"]);
        var properties = file.ResolveDict(resources?["Properties"]);
        var fonts = new Dictionary<string, PdfFontInfo?>(StringComparer.Ordinal);
        var shows = new List<ShowOp>();
        var objectShows = new List<ShowOp>();
        var blocks = new List<TextBlock>();
        var saved = new Stack<GraphicsState>();
        var marks = new Stack<bool>();
        var state = new GraphicsState();
        var textMatrix = PdfMatrix.Identity;
        double lineX = 0, lineY = 0, x = 0, y = 0;
        var known = true;
        var block = -1;

        for (var i = 0; i < ops.Count; i++)
        {
            var o = ops[i].Operands;
            switch (ops[i].Operator)
            {
                case "q":
                    saved.Push(state with { });
                    MarkIrregular();
                    break;
                case "Q":
                    if (saved.Count > 0)
                    {
                        state = saved.Pop();
                    }

                    MarkIrregular();
                    break;
                case "cm":
                    if (PdfMatrix.FromOperands(o) is { } m)
                    {
                        state = state with { Ctm = m.Then(state.Ctm) };
                    }

                    MarkIrregular();
                    break;
                case "BT":
                    block = blocks.Count;
                    blocks.Add(new TextBlock(i));
                    textMatrix = PdfMatrix.Identity;
                    lineX = lineY = x = y = 0;
                    known = true;
                    break;
                case "ET":
                    if (block >= 0)
                    {
                        blocks[block].End = i;
                    }

                    block = -1;
                    break;
                case "Tf" when o.Length == 2 && o[0] is CosName name && o[1] is CosNumber size:
                    state = state with { FontName = name.Value, FontSize = size.Value };
                    break;
                case "Tc" when Number(o, 0) is { } value:
                    state = state with { CharSpace = value };
                    break;
                case "Tw" when Number(o, 0) is { } value:
                    state = state with { WordSpace = value };
                    break;
                case "Tz" when o.Length == 1 && Number(o, 0) is { } value:
                    state = state with { HorizontalScale = value / 100 };
                    break;
                case "TL" when Number(o, 0) is { } value:
                    state = state with { Leading = value };
                    break;
                case "Ts" when Number(o, 0) is { } value:
                    state = state with { Rise = value };
                    break;
                case "Tm" when PdfMatrix.FromOperands(o) is { } matrix:
                    textMatrix = matrix;
                    lineX = lineY = x = y = 0;
                    known = true;
                    break;
                case "Td" when Number(o, 0) is { } tx && Number(o, 1) is { } ty:
                    MoveTo(tx, ty);
                    break;
                case "TD" when Number(o, 0) is { } tx && Number(o, 1) is { } ty:
                    state = state with { Leading = -ty };
                    MoveTo(tx, ty);
                    break;
                case "T*":
                    NextLine();
                    break;
                case "'" when o.Length >= 1:
                    NextLine();
                    Show(i, o[^1], isArray: false);
                    break;
                case "\"" when o.Length >= 3 && Number(o, 0) is { } aw && Number(o, 1) is { } ac:
                    state = state with { WordSpace = aw, CharSpace = ac };
                    NextLine();
                    Show(i, o[2], isArray: false);
                    break;
                case "Tj" when o.Length >= 1:
                    Show(i, o[^1], isArray: false);
                    break;
                case "TJ" when o.Length >= 1:
                    Show(i, o[^1], isArray: true);
                    break;
                case "BDC":
                    marks.Push(o.Length < 2 || o[1] switch
                    {
                        CosDict dict => dict["ActualText"] is not null,
                        CosName propertyName => file.ResolveDict(properties?[propertyName.Value]) is not { } props || props["ActualText"] is not null,
                        _ => true,
                    });
                    break;
                case "BMC":
                    marks.Push(false);
                    break;
                case "EMC":
                    marks.TryPop(out _);
                    break;
            }
        }

        return new ContentTextLayout { Shows = shows, Blocks = blocks, ObjectShows = objectShows };

        void MarkIrregular()
        {
            if (block >= 0)
            {
                blocks[block].Irregular = true;
            }
        }

        void MoveTo(double tx, double ty)
        {
            lineX += tx;
            lineY += ty;
            x = lineX;
            y = lineY;
            known = true;
        }

        void NextLine()
        {
            lineY -= state.Leading;
            x = lineX;
            y = lineY;
            known = true;
        }

        void Show(int index, CosObject operand, bool isArray)
        {
            if (isArray && operand is not CosArray)
            {
                return;
            }

            var segments = new List<(byte[] Bytes, double KerningAfter)>();
            double initial = 0, numbers = 0;
            var anyString = false;
            foreach (var item in operand is CosArray array && isArray ? array.Items : [operand])
            {
                switch (item)
                {
                    case CosString s:
                        anyString = true;
                        if (s.Bytes.Length > 0)
                        {
                            segments.Add((s.Bytes, 0));
                        }

                        break;
                    case CosNumber n when isArray:
                        numbers += n.Value;
                        if (segments.Count == 0)
                        {
                            initial += n.Value;
                        }
                        else
                        {
                            segments[^1] = (segments[^1].Bytes, segments[^1].KerningAfter + n.Value);
                        }

                        break;
                }
            }

            var font = FontInfo(state.FontName);
            var show = new ShowOp
            {
                OpIndex = index,
                Block = block,
                Ctm = state.Ctm,
                TextMatrix = textMatrix,
                X = x,
                Y = y,
                PositionKnown = known,
                FontName = state.FontName,
                Font = font,
                FontSize = state.FontSize,
                CharSpace = state.CharSpace,
                WordSpace = state.WordSpace,
                HorizontalScale = state.HorizontalScale,
                Rise = state.Rise,
                InitialKerning = initial,
                Segments = segments,
                InReplacedText = marks.Any(m => m),
            };
            shows.Add(show);
            if (isArray && !anyString)
            {
                // PDFium applies a TJ without strings as positioning only, with or without a font.
                x -= numbers / 1000 * state.FontSize * state.HorizontalScale;
                return;
            }

            if (state.FontName is null)
            {
                return;
            }

            if (segments.Count > 0)
            {
                show.Ordinal = objectShows.Count;
                objectShows.Add(show);
            }

            x -= initial / 1000 * state.FontSize * state.HorizontalScale;
            double advance = 0;
            foreach (var (code, kerning) in show.Codes())
            {
                if (width(show, code) is not { } w)
                {
                    known = false;
                    return;
                }

                advance += (w * state.FontSize) + state.CharSpace + (code == 32 && font!.CodeLength == 1 ? state.WordSpace : 0)
                    - (kerning / 1000 * state.FontSize);
            }

            if (font is null && segments.Count > 0)
            {
                known = false;
                return;
            }

            x += advance * state.HorizontalScale;
        }

        PdfFontInfo? FontInfo(string? name)
        {
            if (name is null)
            {
                return null;
            }

            if (!fonts.TryGetValue(name, out var info))
            {
                info = file.ResolveDict(fontResources?[name]) is { } dict ? PdfFontInfo.Load(file, dict) : null;
                fonts[name] = info;
            }

            return info;
        }
    }

    private static double? Number(CosObject[] operands, int index) =>
        index < operands.Length && operands[index] is CosNumber n ? n.Value : null;

    private sealed record GraphicsState
    {
        public PdfMatrix Ctm { get; init; } = PdfMatrix.Identity;

        public string? FontName { get; init; }

        public double FontSize { get; init; }

        public double CharSpace { get; init; }

        public double WordSpace { get; init; }

        public double HorizontalScale { get; init; } = 1;

        public double Leading { get; init; }

        public double Rise { get; init; }
    }
}
