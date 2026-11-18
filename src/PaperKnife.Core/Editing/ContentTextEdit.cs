using System.Text;
using PaperKnife.Core.Content;
using PaperKnife.Core.Syntax;
using static PaperKnife.Core.Pdfium.PdfiumNative;

namespace PaperKnife.Core.Editing;

internal enum ContentEditOutcome
{
    /// <summary>This edit can't be made in the content stream; PDFium's page rewrite may still do it.</summary>
    NotApplicable,

    Rejected,

    Ready,
}

internal sealed record ContentEditResult(
    ContentEditOutcome Outcome,
    string? Reason = null,
    EditResult? Rejection = null,
    byte[]? Candidate = null,
    RunChange? Change = null);

/// <summary>
/// Edits a run in the page's content stream itself instead of having PDFium regenerate the page:
/// only the text-showing operations of the run change, and the result is appended to the file as
/// an incremental update, so every other byte stays as it was. Content PDFium can't write back
/// (Type 3 fonts, letter and word spacing, print colors, inline images, shadings) is kept as is.
/// The candidate is verified like any other edit.
/// </summary>
internal static unsafe class ContentTextEdit
{
    private const double PositionTolerance = 0.01;

    public static ContentEditResult Prepare(ContentPage page, IReadOnlyList<TextPiece> pagePieces, TextRunLayout layout, TextRunEdit edit)
    {
        if (page.Text.ObjectShows.Count != pagePieces.Count)
        {
            return NotApplicable("The page's text objects don't match its content.");
        }

        // Fonts without a /Widths array (the standard 14) take their widths from PDFium.
        double? Width(ShowOp show, uint code) =>
            show.Font?.Width(code) ?? (show.Ordinal >= 0 ? PdfiumWidth(pagePieces[show.Ordinal].Font, show.Font?.Unicode(code)) : null);

        var text = TextShowSimulator.Run(page.Ops, page.File, page.Page.Resources, Width);
        var ordinals = new Dictionary<int, int>();
        for (var i = 0; i < pagePieces.Count; i++)
        {
            ordinals[pagePieces[i].ObjectIndex] = i;
        }

        var runShows = layout.Pieces.Select(p => text.ObjectShows[ordinals[p.ObjectIndex]]).ToList();
        var first = runShows[edit.First];
        var firstPiece = layout.Pieces[edit.First];
        if (first.Font is not { CodeLength: 1 or 2 } font || first.InReplacedText)
        {
            return NotApplicable("PaperKnife can't encode text for this font.");
        }

        var layoutProblem = TextSupport.Check(firstPiece.Font, layout.Run.FontName, edit.NewText, checkGlyphs: false);
        if (!layoutProblem.IsSupported)
        {
            return Rejected(layoutProblem.Problem!, layoutProblem.Characters);
        }

        var preferred = runShows.Skip(edit.First).Take(edit.Last - edit.First + 1).SelectMany(s => s.Codes()).Select(c => c.Code).ToHashSet();
        var codes = font.Encode(edit.NewText, preferred, out var missing);
        if (missing.Count > 0)
        {
            return font.IsType3
                ? Rejected($"The font “{layout.Run.FontName}” in this PDF doesn't contain: {string.Join(' ', missing)}", missing)
                : NotApplicable("The font's character map doesn't cover the new text.");
        }

        if (!font.IsType3 && codes.Count > 0)
        {
            var support = TextSupport.Check(firstPiece.Font, layout.Run.FontName, edit.NewText);
            if (!support.IsSupported)
            {
                return Rejected(support.Problem!, support.Characters);
            }
        }

        var glyphs = new List<(double Width, uint Code, double KerningAfter)>();
        foreach (var code in codes)
        {
            if (Width(first, code) is not { } w)
            {
                return NotApplicable("A character's width isn't known.");
            }

            glyphs.Add((w, code, 0));
        }

        // Ends along the baseline, in the units of TextRunLayout: the end of the last glyph,
        // leaving out the spacing after it, so the gap to the rest of the run stays the same.
        double Start(ShowOp show) => layout.DistanceAlongBaseline(show.PageStart.X, show.PageStart.Y);
        var end = Start(first) + Span(first, glyphs);
        var trimmedEnd = Start(first) + Span(first, glyphs.Take(edit.NewText.TrimEnd().Length).ToList());
        double oldEnd;
        var lastPiece = layout.Pieces[edit.Last];
        if (!lastPiece.HasCharacters)
        {
            oldEnd = layout.EndAlongBaseline(lastPiece);
        }
        else
        {
            var lastShow = runShows[edit.Last];
            var oldGlyphs = new List<(double Width, uint Code, double KerningAfter)>();
            foreach (var (code, kerning) in lastShow.Codes())
            {
                if (Width(lastShow, code) is not { } w)
                {
                    return NotApplicable("A character's width isn't known.");
                }

                oldGlyphs.Add((w, code, kerning));
            }

            oldEnd = Start(lastShow) + Span(lastShow, oldGlyphs);
        }

        var rest = runShows.Skip(edit.Last + 1).ToList();
        var shift = end - oldEnd;
        var moves = rest.Count > 0 && Math.Abs(shift) > firstPiece.FontSize * 1e-3;
        var (dx, dy) = moves ? (shift * firstPiece.Matrix.A, shift * firstPiece.Matrix.B) : (0d, 0d);

        var keepsFirst = codes.Count > 0;
        var removed = runShows.Skip(keepsFirst ? edit.First + 1 : edit.First).Take(edit.Last - edit.First + (keepsFirst ? 0 : 1)).ToList();
        var roles = new Dictionary<int, Role>();
        if (keepsFirst)
        {
            roles[first.OpIndex] = Role.Edited;
        }

        foreach (var show in removed)
        {
            roles[show.OpIndex] = Role.Removed;
        }

        foreach (var show in moves ? rest : [])
        {
            roles[show.OpIndex] = Role.Moved;
        }

        var blocks = runShows.Skip(edit.First).Take(edit.Last - edit.First + 1).Concat(moves ? rest : [])
            .Select(s => s.Block).Distinct().Order().ToList();
        var problem = CheckBlocks(text, blocks, pagePieces);
        if (problem is not null)
        {
            return NotApplicable(problem);
        }

        var content = Rewrite(page, text, blocks, roles, first, font.JoinCodes(codes), (dx, dy));
        if (content is null)
        {
            return NotApplicable("A moved text object's transformation can't be inverted.");
        }

        var updates = page.Streams.Select((s, i) => new StreamUpdate(s.Number, s.Generation, i == 0 ? content : [])).ToList();
        var candidate = IncrementalUpdate.Append(page.File, updates);

        var pieces = layout.Pieces;
        var hiddenBefore = pieces.Skip(edit.First).Take(edit.Last - edit.First + 1).Select(p => p.ObjectIndex).ToList();
        var hiddenAfter = keepsFirst ? new List<int> { firstPiece.ObjectIndex } : [];
        if (moves)
        {
            hiddenBefore.AddRange(pieces.Skip(edit.Last + 1).Select(p => p.ObjectIndex));
            hiddenAfter.AddRange(pieces.Skip(edit.Last + 1).Select(p => p.ObjectIndex - removed.Count));
        }

        var change = new RunChange(layout, edit, removed.Count, dx, dy, end, trimmedEnd, hiddenBefore, hiddenAfter);
        return new ContentEditResult(ContentEditOutcome.Ready, Candidate: candidate, Change: change);
    }

    /// <summary>
    /// The advance from a glyph sequence's origin to the end of its last glyph, along the baseline in
    /// TextRunLayout units (where horizontal scaling cancels out): widths, character and word
    /// spacing and TJ adjustments, but not the spacing after the last glyph.
    /// </summary>
    private static double Span(ShowOp show, IReadOnlyList<(double Width, uint Code, double KerningAfter)> glyphs)
    {
        double total = 0;
        for (var i = 0; i < glyphs.Count; i++)
        {
            total += glyphs[i].Width * show.FontSize;
            if (i < glyphs.Count - 1)
            {
                total += show.CharSpace + (glyphs[i].Code == 32 && show.Font!.CodeLength == 1 ? show.WordSpace : 0)
                    - (glyphs[i].KerningAfter / 1000 * show.FontSize);
            }
        }

        return total;
    }

    /// <summary>
    /// The blocks are rewritten with an explicit position for every text-showing operation, so each
    /// one's position must be known and match where PDFium draws it.
    /// </summary>
    private static string? CheckBlocks(ContentTextLayout text, List<int> blocks, IReadOnlyList<TextPiece> pagePieces)
    {
        foreach (var index in blocks)
        {
            if (index < 0 || text.Blocks[index] is not { End: >= 0, Irregular: false })
            {
                return "The text isn't in a simple text block.";
            }

            foreach (var show in text.Shows.Where(s => s.Block == index && s.ProducesObject))
            {
                var piece = pagePieces[show.Ordinal];
                var (x, y) = show.PageStart;
                var m = show.ObjectMatrix;
                var scale = Math.Max(Math.Max(Math.Abs(m.A), Math.Abs(m.B)), Math.Max(Math.Abs(m.C), Math.Abs(m.D)));
                var tolerance = (scale * 1e-4) + 1e-6;
                if (!show.PositionKnown || !piece.HasMatrix
                    || Math.Abs(x - piece.Matrix.E) > PositionTolerance || Math.Abs(y - piece.Matrix.F) > PositionTolerance
                    || Math.Abs(m.A - piece.Matrix.A) > tolerance || Math.Abs(m.B - piece.Matrix.B) > tolerance
                    || Math.Abs(m.C - piece.Matrix.C) > tolerance || Math.Abs(m.D - piece.Matrix.D) > tolerance)
                {
                    return "PaperKnife couldn't confirm where the text is drawn.";
                }
            }
        }

        return null;
    }

    /// <summary>
    /// The page content with each affected text block rewritten: positioning operators are replaced
    /// by an explicit Tm before every text-showing operation, the edited operation shows the new
    /// codes, removed ones are left out, and moved ones are shifted. Everything else is copied
    /// byte for byte.
    /// </summary>
    private static byte[]? Rewrite(
        ContentPage page,
        ContentTextLayout text,
        List<int> blocks,
        Dictionary<int, Role> roles,
        ShowOp edited,
        byte[] newCodes,
        (double X, double Y) shift)
    {
        var content = page.Content;
        var shows = text.Shows.ToDictionary(s => s.OpIndex);
        var output = new MemoryStream(content.Length + 256);
        var copied = 0;
        foreach (var index in blocks)
        {
            var block = text.Blocks[index];
            var start = page.Ops[block.Begin].Start;
            output.Write(content, copied, start - copied);
            for (var i = block.Begin; i <= block.End; i++)
            {
                var op = page.Ops[i];
                var raw = content.AsSpan(op.Start, op.End - op.Start);
                switch (op.Operator)
                {
                    case "Td" or "Tm" or "T*":
                        continue;
                    case "TD":
                        // TD also sets the leading, which later text may use.
                        if (op.Operands is [CosNumber, CosNumber ty])
                        {
                            Write(output, $"{CosWriter.Number(-ty.Value)} TL\n");
                        }

                        continue;
                    case "Tj" or "TJ" or "'" or "\"" when shows.TryGetValue(i, out var show):
                        if (op.Operator == "\"")
                        {
                            Write(output, $"{((CosNumber)op.Operands[0]).Raw} Tw {((CosNumber)op.Operands[1]).Raw} Tc\n");
                        }

                        var role = roles.GetValueOrDefault(i);
                        if (role == Role.Removed)
                        {
                            continue;
                        }

                        if (role == Role.Edited)
                        {
                            Write(output, $"{edited.GlyphMatrix.ToOperands()} Tm ");
                            Write(output, CosWriter.ToText(new CosString(newCodes)));
                            Write(output, " Tj\n");
                            continue;
                        }

                        var matrix = show.OpMatrix;
                        if (role == Role.Moved)
                        {
                            if (show.Ctm.Invert() is not { } inverse)
                            {
                                return null;
                            }

                            var (ux, uy) = inverse.TransformVector(shift.X, shift.Y);
                            matrix = matrix with { E = matrix.E + ux, F = matrix.F + uy };
                        }

                        if (show.ProducesObject)
                        {
                            Write(output, $"{matrix.ToOperands()} Tm ");
                        }

                        if (op.Operator is "Tj" or "TJ")
                        {
                            output.Write(raw);
                        }
                        else
                        {
                            Write(output, CosWriter.ToText(op.Operands[^1]));
                            Write(output, " Tj");
                        }

                        output.WriteByte((byte)'\n');
                        continue;
                    default:
                        output.Write(raw);
                        output.WriteByte((byte)'\n');
                        continue;
                }
            }

            copied = page.Ops[block.End].End;
        }

        output.Write(content, copied, content.Length - copied);
        return output.ToArray();
    }

    private static double? PdfiumWidth(nint font, string? text)
    {
        float width = 0;
        return font != 0 && text is { Length: 1 } && FPDFFont_GetGlyphWidth(font, text[0], 1f, &width) != 0 && width > 0 ? width : null;
    }

    private static void Write(MemoryStream output, string text) => output.Write(Encoding.Latin1.GetBytes(text));

    private static ContentEditResult NotApplicable(string reason) => new(ContentEditOutcome.NotApplicable, reason);

    private static ContentEditResult Rejected(string message, IReadOnlyList<string> characters) =>
        new(ContentEditOutcome.Rejected, Rejection: EditResult.Rejected(EditRejection.UnsupportedCharacters, message, characters));

    private enum Role
    {
        Unchanged,
        Edited,
        Removed,
        Moved,
    }
}
