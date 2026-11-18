using System.Text;
using PaperKnife.Core.Content;
using PaperKnife.Core.Fonts;
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
    RunChange? Change = null,
    string? AddedFont = null,
    string? GlyphsToCheck = null,
    string? CopyFont = null);

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
        if (edit.DeletesAll(layout.Pieces.Count))
        {
            return Delete(page, text, runShows, layout, edit, pagePieces);
        }

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

        var count = edit.Last - edit.First + 1;
        var oldShows = runShows.GetRange(edit.First, count);
        var preferred = oldShows.SelectMany(s => s.Codes()).Select(c => c.Code).ToHashSet();
        double? wordGap = null;
        var planner = new GlyphPlanner
        {
            Font = font,
            Width = code => Width(first, code),
            Preferred = preferred,
            SpaceCode = SpaceCode(font, firstPiece.Font, layout.Run.FontName, preferred),
            WordGap = () => wordGap ??= WordGap(text, pagePieces, font, Width),
            Old = GlyphPlanner.ReadOldGlyphs(
                oldShows, [.. layout.Pieces.Skip(edit.First).Take(count)], [.. layout.Separators.Skip(edit.First).Take(count - 1)], Width, first),
        };
        var plan = planner.Plan(edit.NewText);
        if (plan.Missing.Count > 0 && font.IsType3)
        {
            return Rejected(TextSupport.MissingMessage(layout.Run.FontName, plan.Missing), plan.Missing);
        }

        // Letters the font lacks: TeX embeds only those a document uses, and PaperKnife may have
        // the whole font. The new text then uses a complete copy of the font; nothing else does.
        FontCompletion? completion = null;
        var support = plan.Missing.Count == 0 && plan.NewCharacters.Length > 0 && !font.IsType3
            ? TextSupport.Check(firstPiece.Font, layout.Run.FontName, plan.NewCharacters)
            : TextSupportResult.Supported;
        if (plan.Missing.Count > 0 || !support.IsSupported)
        {
            completion = FontCompletion.TryCreate(page, first.FontName!, out var difference);
            if (completion is null)
            {
                if (plan.Missing.Count > 0)
                {
                    // PDFium may still encode them on its own; if it can't draw them either, say so.
                    support = TextSupport.Check(firstPiece.Font, layout.Run.FontName, string.Concat(plan.Missing));
                    if (support.IsSupported)
                    {
                        return NotApplicable("The font's character map doesn't cover the new text.");
                    }
                }

                return Rejected(
                    difference is null ? support.Problem! : $"{support.Problem}. PaperKnife has its own copy of this font but can't use it here: {difference}.",
                    support.Characters);
            }

            // Kept letters keep their codes, which select the same glyphs with the same widths in the copy.
            var copy = completion.Info;
            plan = new GlyphPlanner
            {
                Font = copy,
                Width = code => copy.Width(code),
                Preferred = preferred,
                SpaceCode = planner.SpaceCode,
                WordGap = planner.WordGap,
                Old = planner.Old,
            }.Plan(edit.NewText);
            var notDrawn = plan.Missing.Concat(plan.NewGlyphs.Where(g => !completion.Draws(g.Code, g.Text)).Select(g => g.Text)).Distinct().ToList();
            if (notDrawn.Count > 0)
            {
                return Rejected(TextSupport.MissingMessage(layout.Run.FontName, notDrawn), notDrawn);
            }
        }

        var glyphs = plan.Glyphs;
        if (glyphs.Any(g => double.IsNaN(g.Width)))
        {
            return NotApplicable("A character's width isn't known.");
        }

        // Ends along the baseline, in the units of TextRunLayout: the end of the last glyph,
        // leaving out the spacing after it, so the gap to the rest of the run stays the same.
        // Spaces drawn as gaps count, at either end.
        double Start(ShowOp show) => layout.DistanceAlongBaseline(show.PageStart.X, show.PageStart.Y);
        var lead = glyphs.Count > 0 ? -plan.InitialKerning / 1000 * first.FontSize : 0;
        var trail = plan.TrailingKerning == 0 ? 0 : (-plan.TrailingKerning / 1000 * first.FontSize) + (glyphs.Count > 0 ? first.CharSpace : 0);
        var end = Start(first) + lead + Span(first, glyphs) + trail;
        var trimmedEnd = Start(first) + lead + Span(first, [.. glyphs.Take(plan.VisibleGlyphs)]);
        double oldEnd;
        var lastPiece = layout.Pieces[edit.Last];
        if (!lastPiece.HasCharacters)
        {
            oldEnd = layout.EndAlongBaseline(lastPiece);
        }
        else
        {
            var lastShow = runShows[edit.Last];
            var oldGlyphs = new List<PlannedGlyph>();
            foreach (var (code, kerning) in lastShow.Codes())
            {
                if (Width(lastShow, code) is not { } w)
                {
                    return NotApplicable("A character's width isn't known.");
                }

                oldGlyphs.Add(new PlannedGlyph(code, w, kerning));
            }

            oldEnd = Start(lastShow) + Span(lastShow, oldGlyphs);
        }

        var rest = runShows.Skip(edit.Last + 1).ToList();
        var shift = end - oldEnd;
        var moves = rest.Count > 0 && Math.Abs(shift) > firstPiece.FontSize * 1e-3;
        var (dx, dy) = moves ? (shift * firstPiece.Matrix.A, shift * firstPiece.Matrix.B) : (0d, 0d);

        var keepsFirst = glyphs.Count > 0;
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

        (string, string, double)? switchFont = completion is null ? null : (completion.ResourceName, first.FontName!, first.FontSize);
        var content = Rewrite(page, text, blocks, roles, first, EditedOperation(completion?.Info ?? font, plan), (dx, dy), switchFont);
        if (content is null)
        {
            return NotApplicable("A moved text object's transformation can't be inverted.");
        }

        var updates = new List<ObjectUpdate>(page.Streams.Select((s, i) => new StreamUpdate(s.Number, s.Generation, i == 0 ? content : [])));
        if (completion is not null)
        {
            updates.AddRange(completion.Updates());
        }

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
        return new ContentEditResult(
            ContentEditOutcome.Ready,
            Candidate: candidate,
            Change: change,
            AddedFont: completion is { Reused: false } ? completion.FontName : null,
            GlyphsToCheck: completion is null ? null : plan.NewCharacters,
            CopyFont: completion?.FontName);
    }

    /// <summary>
    /// Deletes a whole run: its text-showing operations are left out of their blocks, which are
    /// rewritten as for an edit, so the rest of each block keeps its place. Nothing is encoded, so
    /// any font will do.
    /// </summary>
    private static ContentEditResult Delete(
        ContentPage page, ContentTextLayout text, List<ShowOp> runShows, TextRunLayout layout, TextRunEdit edit, IReadOnlyList<TextPiece> pagePieces)
    {
        if (runShows.Any(s => s.InReplacedText))
        {
            return NotApplicable("The text has replacement text (/ActualText).");
        }

        var roles = new Dictionary<int, Role>();
        foreach (var show in runShows)
        {
            roles[show.OpIndex] = Role.Removed;
        }

        var blocks = runShows.Select(s => s.Block).Distinct().Order().ToList();
        if (CheckBlocks(text, blocks, pagePieces) is { } problem)
        {
            return NotApplicable(problem);
        }

        if (Rewrite(page, text, blocks, roles, runShows[0], string.Empty, (0, 0), switchFont: null) is not { } content)
        {
            return NotApplicable("The text blocks can't be rewritten.");
        }

        var updates = page.Streams.Select((s, i) => (ObjectUpdate)new StreamUpdate(s.Number, s.Generation, i == 0 ? content : [])).ToList();
        var deleted = layout.Pieces.Select(p => p.ObjectIndex).ToList();
        var change = new RunChange(layout, edit, deleted.Count, 0, 0, 0, 0, deleted, []);
        return new ContentEditResult(ContentEditOutcome.Ready, Candidate: IncrementalUpdate.Append(page.File, updates), Change: change);
    }

    /// <summary>
    /// The advance from a glyph sequence's origin to the end of its last glyph, along the baseline in
    /// TextRunLayout units (where horizontal scaling cancels out): widths, character and word
    /// spacing and TJ adjustments, but not the spacing after the last glyph.
    /// </summary>
    private static double Span(ShowOp show, IReadOnlyList<PlannedGlyph> glyphs)
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

    /// <summary>A code that draws a space in this font, or null when spaces have to be drawn as gaps.</summary>
    private static uint? SpaceCode(PdfFontInfo font, nint pdfiumFont, string fontName, IReadOnlySet<uint> preferred)
    {
        var codes = font.Encode(" ", preferred, out var missing);
        if (missing.Count > 0 || codes.Count != 1)
        {
            return null;
        }

        return font.IsType3 || TextSupport.Check(pdfiumFont, fontName, " ").IsSupported ? codes[0] : null;
    }

    /// <summary>
    /// The median gap between words, in thousandths of an em, among this font's text on the page:
    /// the TJ adjustments PDFium reads as spaces. A third of an em, TeX's usual word space, when
    /// there are none.
    /// </summary>
    private static double WordGap(ContentTextLayout text, IReadOnlyList<TextPiece> pagePieces, PdfFontInfo font, Func<ShowOp, uint, double?> width)
    {
        var gaps = new List<double>();
        foreach (var show in text.ObjectShows)
        {
            if (ReferenceEquals(show.Font, font) && GlyphPlanner.ReadOldGlyphs([show], [pagePieces[show.Ordinal]], [], width, show) is { } glyphs)
            {
                gaps.AddRange(glyphs.Where(g => g.SpaceAfter).Select(g => -g.KerningAfter));
            }
        }

        if (gaps.Count == 0)
        {
            return 1000.0 / 3;
        }

        gaps.Sort();
        return gaps[gaps.Count / 2];
    }

    /// <summary>The text-showing operation for the new text: Tj, or TJ when it needs adjustments.</summary>
    private static string EditedOperation(PdfFontInfo font, GlyphPlan plan)
    {
        if (!plan.HasAdjustments)
        {
            return CosWriter.ToText(new CosString(font.JoinCodes([.. plan.Glyphs.Select(g => g.Code)]))) + " Tj";
        }

        var operation = new StringBuilder("[");
        if (plan.InitialKerning != 0)
        {
            operation.Append(CosWriter.Number(plan.InitialKerning)).Append(' ');
        }

        var codes = new List<uint>();
        for (var i = 0; i < plan.Glyphs.Count; i++)
        {
            codes.Add(plan.Glyphs[i].Code);
            if (i == plan.Glyphs.Count - 1 || plan.Glyphs[i].KerningAfter != 0)
            {
                operation.Append(CosWriter.ToText(new CosString(font.JoinCodes(codes))));
                if (i < plan.Glyphs.Count - 1)
                {
                    operation.Append(' ').Append(CosWriter.Number(plan.Glyphs[i].KerningAfter)).Append(' ');
                }

                codes.Clear();
            }
        }

        return operation.Append("] TJ").ToString();
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
        string editedOperation,
        (double X, double Y) shift,
        (string Copy, string Original, double Size)? switchFont)
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
                            // Text drawn with a completed copy of the font selects the copy for itself only.
                            if (switchFont is { } fonts)
                            {
                                Write(output, $"{Name(fonts.Copy)} {CosWriter.Number(fonts.Size)} Tf\n");
                            }

                            Write(output, $"{edited.GlyphMatrix.ToOperands()} Tm {editedOperation}\n");
                            if (switchFont is { } restore)
                            {
                                Write(output, $"{Name(restore.Original)} {CosWriter.Number(restore.Size)} Tf\n");
                            }

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

    private static string Name(string name)
    {
        var text = new StringBuilder();
        CosWriter.WriteName(text, name);
        return text.ToString();
    }

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
