using PaperKnife.Core.Syntax;

namespace PaperKnife.Core.Content;

/// <summary>One operation of a content stream: operands, operator, and the bytes it spans.</summary>
internal sealed record ContentOp(string Operator, CosObject[] Operands, int Start, int End);

/// <summary>Splits decoded page content into operations (ISO 32000-1, 7.8.2).</summary>
internal static class ContentParser
{
    /// <summary>The operations, or null when the content can't be parsed with certainty.</summary>
    public static List<ContentOp>? Parse(ReadOnlyMemory<byte> data)
    {
        var lexer = new PdfLexer(data);
        var ops = new List<ContentOp>();
        var operands = new List<CosObject>();
        var start = -1;
        while (true)
        {
            var token = lexer.Next();
            if (token.Kind == PdfTokenKind.End)
            {
                return ops;
            }

            if (token.Kind == PdfTokenKind.Keyword && token.Text is not ("true" or "false" or "null"))
            {
                var end = token.End;
                if (token.Text == "BI" && (end = SkipInlineImage(lexer)) < 0)
                {
                    return null;
                }

                ops.Add(new ContentOp(token.Text, [.. operands], start >= 0 ? start : token.Start, end));
                operands.Clear();
                start = -1;
                continue;
            }

            if (start < 0)
            {
                start = token.Start;
            }

            if (PdfObjectParser.FromToken(lexer, token, allowReferences: false, 0) is not { } operand)
            {
                return null;
            }

            operands.Add(operand);
        }
    }

    /// <summary>Moves past an inline image's dictionary, data and EI; returns the end offset, or -1.</summary>
    private static int SkipInlineImage(PdfLexer lexer)
    {
        var span = lexer.Data.Span;
        var dict = new Dictionary<string, CosObject>(StringComparer.Ordinal);
        while (true)
        {
            var token = lexer.Next();
            if (token.IsKeyword("ID"))
            {
                var data = token.End;
                if (data < span.Length && PdfLexer.IsWhitespace(span[data]))
                {
                    data++;
                }

                if (UnfilteredLength(dict) is { } length && data + length <= span.Length)
                {
                    var after = new PdfLexer(lexer.Data, data + length);
                    var end = after.Next();
                    if (end.IsKeyword("EI"))
                    {
                        lexer.Position = end.End;
                        return end.End;
                    }
                }

                for (var i = data; i + 1 < span.Length; i++)
                {
                    if (span[i] == 'E' && span[i + 1] == 'I' && (i == data || PdfLexer.IsWhitespace(span[i - 1]))
                        && (i + 2 == span.Length || !PdfLexer.IsRegular(span[i + 2])))
                    {
                        lexer.Position = i + 2;
                        return i + 2;
                    }
                }

                return -1;
            }

            if (token.Kind != PdfTokenKind.Name || PdfObjectParser.FromToken(lexer, lexer.Next(), allowReferences: false, 0) is not { } value)
            {
                return -1;
            }

            dict[token.Text] = value;
        }
    }

    private static int? UnfilteredLength(Dictionary<string, CosObject> dict)
    {
        CosObject? Get(string shortKey, string longKey) => dict.GetValueOrDefault(shortKey) ?? dict.GetValueOrDefault(longKey);
        int? Number(string shortKey, string longKey) => Get(shortKey, longKey) is CosNumber { Value: > 0 } n ? (int)n.Value : null;

        if (Get("F", "Filter") is not null)
        {
            return null;
        }

        var mask = Get("IM", "ImageMask") is CosBool { Value: true };
        var components = mask ? 1 : Get("CS", "ColorSpace") switch
        {
            CosName { Value: "G" or "DeviceGray" or "CalGray" } => 1,
            CosName { Value: "RGB" or "DeviceRGB" or "CalRGB" } => 3,
            CosName { Value: "CMYK" or "DeviceCMYK" } => 4,
            CosArray { Count: > 0 } a when a[0] is CosName { Value: "I" or "Indexed" } => 1,
            _ => (int?)null,
        };
        var bits = mask ? 1 : Number("BPC", "BitsPerComponent");
        var width = Number("W", "Width");
        var height = Number("H", "Height");
        return components is { } c && bits is { } b && width is { } w && height is { } h ? h * (((w * c * b) + 7) / 8) : null;
    }
}

/// <summary>An affine matrix [a b c d e f], mapping (x, y) to (a·x + c·y + e, b·x + d·y + f) as in PDF.</summary>
internal readonly record struct PdfMatrix(double A, double B, double C, double D, double E, double F)
{
    public static PdfMatrix Identity { get; } = new(1, 0, 0, 1, 0, 0);

    public static PdfMatrix Translation(double x, double y) => new(1, 0, 0, 1, x, y);

    public static PdfMatrix? FromOperands(CosObject[] operands, int start = 0) =>
        operands.Length >= start + 6 && operands.Skip(start).Take(6).All(o => o is CosNumber)
            ? new PdfMatrix(Value(operands[start]), Value(operands[start + 1]), Value(operands[start + 2]),
                Value(operands[start + 3]), Value(operands[start + 4]), Value(operands[start + 5]))
            : null;

    /// <summary>This transformation followed by <paramref name="next"/>.</summary>
    public PdfMatrix Then(PdfMatrix next) => new(
        (A * next.A) + (B * next.C), (A * next.B) + (B * next.D),
        (C * next.A) + (D * next.C), (C * next.B) + (D * next.D),
        (E * next.A) + (F * next.C) + next.E, (E * next.B) + (F * next.D) + next.F);

    public (double X, double Y) Transform(double x, double y) => ((A * x) + (C * y) + E, (B * x) + (D * y) + F);

    public (double X, double Y) TransformVector(double x, double y) => ((A * x) + (C * y), (B * x) + (D * y));

    public PdfMatrix? Invert()
    {
        var determinant = (A * D) - (B * C);
        return Math.Abs(determinant) < 1e-12
            ? null
            : new PdfMatrix(D / determinant, -B / determinant, -C / determinant, A / determinant,
                ((C * F) - (D * E)) / determinant, ((B * E) - (A * F)) / determinant);
    }

    public string ToOperands() =>
        $"{CosWriter.Number(A)} {CosWriter.Number(B)} {CosWriter.Number(C)} {CosWriter.Number(D)} {CosWriter.Number(E)} {CosWriter.Number(F)}";

    private static double Value(CosObject o) => ((CosNumber)o).Value;
}
