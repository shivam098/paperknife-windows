using PaperKnife.Core.Content;
using PaperKnife.Core.Editing;
using PaperKnife.Core.Pdfium;
using PaperKnife.Core.Syntax;

namespace PaperKnife.Core;

/// <summary>
/// An open document and its edit history. Every change is a transaction: it is applied to a scratch
/// copy, serialized, reloaded and verified, and only then committed. A rejected change leaves the
/// document exactly as it was. Members are safe to call from any thread; calls are serialized.
/// </summary>
public sealed class DocumentSession : IDisposable
{
    private readonly string? _password;
    private readonly LinkedList<PdfSnapshot> _undo = new();
    private readonly Stack<PdfSnapshot> _redo = new();
    private readonly Dictionary<int, ContentPage?> _contentPages = [];
    private PdfDocument _document;
    private PdfSnapshot _current;
    private PdfSnapshot _saved;
    private PdfSnapshot? _contentSnapshot;
    private PdfFile? _contentFile;
    private IReadOnlyList<PdfPageNode>? _contentPageTree;
    private bool _disposed;

    private DocumentSession(PdfSnapshot snapshot, string? password, string? filePath)
    {
        _document = PdfDocument.Load(snapshot, password);
        _current = snapshot;
        _saved = snapshot;
        _password = password;
        FilePath = filePath;
    }

    /// <summary>The file this session was opened from or last saved to, if any.</summary>
    public string? FilePath { get; private set; }

    /// <summary>Increments on every committed change, undo and redo.</summary>
    public int Version { get; private set; }

    /// <summary>Approximate memory the undo history may use. The newest undo step is always kept.</summary>
    public long HistoryLimitBytes { get; set; } = 512L * 1024 * 1024;

    /// <summary>Whether text edits may be made in the content stream; tests turn it off to exercise PDFium's page rewrite.</summary>
    internal bool UseContentEditing { get; set; } = true;

    public bool IsModified
    {
        get
        {
            lock (PdfiumLibrary.Sync)
            {
                return !ReferenceEquals(_current, _saved);
            }
        }
    }

    public bool CanUndo
    {
        get
        {
            lock (PdfiumLibrary.Sync)
            {
                return _undo.Count > 0;
            }
        }
    }

    public bool CanRedo
    {
        get
        {
            lock (PdfiumLibrary.Sync)
            {
                return _redo.Count > 0;
            }
        }
    }

    /// <summary>The verified, serialized state of the document. Saving writes exactly these bytes.</summary>
    public PdfSnapshot CurrentSnapshot
    {
        get
        {
            lock (PdfiumLibrary.Sync)
            {
                return _current;
            }
        }
    }

    public int PageCount
    {
        get
        {
            lock (PdfiumLibrary.Sync)
            {
                ThrowIfDisposed();
                return _document.PageCount;
            }
        }
    }

    public int SignatureCount
    {
        get
        {
            lock (PdfiumLibrary.Sync)
            {
                ThrowIfDisposed();
                return _document.SignatureCount;
            }
        }
    }

    public static DocumentSession Open(string path, string? password = null)
    {
        var fullPath = Path.GetFullPath(path);
        return new DocumentSession(PdfSnapshot.FromFile(fullPath), password, fullPath);
    }

    public static DocumentSession FromSnapshot(PdfSnapshot snapshot, string? password = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new DocumentSession(snapshot, password, null);
    }

    public PageSize GetPageSize(int pageIndex)
    {
        lock (PdfiumLibrary.Sync)
        {
            var page = GetPage(pageIndex);
            return new PageSize(page.Width, page.Height);
        }
    }

    public RenderedBitmap RenderPage(int pageIndex, int pixelWidth, int pixelHeight)
    {
        lock (PdfiumLibrary.Sync)
        {
            return GetPage(pageIndex).Render(pixelWidth, pixelHeight);
        }
    }

    public IReadOnlyList<PdfTextRun> GetTextRuns(int pageIndex)
    {
        lock (PdfiumLibrary.Sync)
        {
            return GetPage(pageIndex).GetTextRuns();
        }
    }

    public PdfTextRun? GetTextRun(int pageIndex, int objectIndex)
    {
        lock (PdfiumLibrary.Sync)
        {
            ThrowIfDisposed();
            return IsValidPage(pageIndex) ? _document.GetPage(pageIndex).TryGetTextRun(objectIndex) : null;
        }
    }

    public PdfTextRun? HitTestText(int pageIndex, int pixelWidth, int pixelHeight, int x, int y)
    {
        lock (PdfiumLibrary.Sync)
        {
            return GetPage(pageIndex).HitTestText(pixelWidth, pixelHeight, x, y);
        }
    }

    public PixelRect MapToPixels(int pageIndex, PdfRect rect, int pixelWidth, int pixelHeight)
    {
        lock (PdfiumLibrary.Sync)
        {
            return GetPage(pageIndex).MapToPixels(rect, pixelWidth, pixelHeight);
        }
    }

    /// <summary>Reasons text on this page can't be edited yet; empty when editing is possible.</summary>
    public IReadOnlyList<string> GetTextEditBlockers(int pageIndex)
    {
        lock (PdfiumLibrary.Sync)
        {
            var page = GetPage(pageIndex);
            var blockers = new List<string>();
            if (!_document.AllowsContentChanges)
            {
                blockers.Add("The author of this PDF restricted editing.");
            }

            // Content PDFium can't rewrite only matters when PDFium has to rewrite the page.
            if (GetContentPage(pageIndex) is not { } content || content.Text.ObjectShows.Count != page.CountObjects().Text)
            {
                blockers.AddRange(FindContentBlockers(page));
            }

            return blockers;
        }
    }

    /// <summary>
    /// Replaces the text of a run, keeping its font, size, position and color. Only the text
    /// objects whose characters change are rewritten; the rest of the run keeps its characters and
    /// moves along the baseline to make room. Trailing whitespace is dropped: it is invisible, and
    /// a character the font can't draw there would be impossible to detect. Empty text deletes the
    /// whole run.
    /// </summary>
    /// <param name="objectIndex">Any page object of the run, normally <see cref="PdfTextRun.ObjectIndex"/>.</param>
    /// <remarks>
    /// When PaperKnife can read the page's content with certainty, only the text-showing operations
    /// involved change, in the content stream itself (see <see cref="ContentTextEdit"/>); otherwise
    /// PDFium regenerates the page.
    /// </remarks>
    public EditResult ReplaceText(int pageIndex, int objectIndex, string newText)
    {
        ArgumentNullException.ThrowIfNull(newText);
        var text = newText.TrimEnd();
        lock (PdfiumLibrary.Sync)
        {
            ThrowIfDisposed();
            if (!IsValidPage(pageIndex))
            {
                return EditResult.Rejected(EditRejection.InvalidTarget, "That page no longer exists.");
            }

            var page = _document.GetPage(pageIndex);
            var (pagePieces, runs) = page.GetTextLayout();
            var layout = runs.FirstOrDefault(r => r.Contains(objectIndex));
            if (layout is null)
            {
                return EditResult.Rejected(EditRejection.InvalidTarget, "The selected text no longer exists. Select it again.");
            }

            var original = layout.Run;
            var deletes = text.Length == 0;
            if (!deletes && TextEquivalence.AreEquivalent(text, original.Text))
            {
                return EditResult.NoChange("The text is unchanged.");
            }

            if (!_document.AllowsContentChanges)
            {
                return EditResult.Rejected(EditRejection.EditingRestricted, "The author of this PDF restricted editing.");
            }

            var edit = deletes
                ? TextRunEdit.DeleteAll(layout.Pieces.Count)
                : TextRunEdit.Plan([.. layout.Pieces.Select(p => p.Text)], layout.Separators, text);
            var pageCount = _document.PageCount;
            var countsBefore = page.CountObjects();
            var (width, height) = EditVerifier.CheckSize(page.Width, page.Height);

            PdfSnapshot? candidate = null;
            RunChange? change = null;
            string? addedFont = null;
            string? glyphsToCheck = null;
            string? copyFont = null;
            if (GetContentPage(pageIndex) is { } content)
            {
                ContentEditResult prepared;
                try
                {
                    prepared = ContentTextEdit.Prepare(content, pagePieces, layout, edit);
                }
                catch (Exception ex) when (IsReadFailure(ex))
                {
                    prepared = new ContentEditResult(ContentEditOutcome.NotApplicable, ex.Message);
                }

                if (prepared.Outcome == ContentEditOutcome.Rejected)
                {
                    return prepared.Rejection!;
                }

                if (prepared.Outcome == ContentEditOutcome.Ready)
                {
                    candidate = PdfSnapshot.FromBytes(prepared.Candidate);
                    change = prepared.Change!;
                    addedFont = prepared.AddedFont;
                    glyphsToCheck = prepared.GlyphsToCheck;
                    copyFont = prepared.CopyFont;
                }
            }

            if (candidate is null)
            {
                var failure = RewriteWithPdfium(pageIndex, page, pagePieces, layout, edit, width, height, out candidate, out change);
                if (failure is not null)
                {
                    return failure;
                }
            }

            var before = RenderWithoutObjects(_current, pageIndex, change!.HiddenBefore, width, height);
            PdfDocument? verified = PdfDocument.Load(candidate!, _password);
            try
            {
                var problem = VerifyTextEdit(verified, candidate!, pageCount, change, countsBefore, before, pagePieces, glyphsToCheck, copyFont, out var edited, out var warnings);
                if (problem is not null)
                {
                    return EditResult.Rejected(EditRejection.CollateralChange, problem);
                }

                Commit(candidate!, verified);
                verified = null;
                var message = deletes
                    ? "Text deleted. Verified: nothing else on this page changed."
                    : change.MovedRest
                    ? "Text updated, and the rest of the line moved to fit. Verified: nothing else on this page changed."
                    : "Text updated. Verified: nothing else on this page changed.";
                List<string> details = addedFont is null
                    ? []
                    : [$"This PDF contained only the letters it used from the font “{addedFont}”, so PaperKnife added a complete copy of that font from its own fonts for the new text. The rest of the document still uses the PDF's own font."];
                return EditResult.Applied(message, warnings, edited, details);
            }
            finally
            {
                verified?.Dispose();
            }
        }
    }

    /// <summary>
    /// Makes an edit by having PDFium regenerate the page's content, for pages PaperKnife can't
    /// edit in the content stream. Refuses pages whose content PDFium can't write back unchanged.
    /// </summary>
    /// <param name="pagePieces">The page's text objects.</param>
    private EditResult? RewriteWithPdfium(
        int pageIndex, PdfPage page, IReadOnlyList<TextPiece> pagePieces, TextRunLayout layout, TextRunEdit edit, int width, int height, out PdfSnapshot? candidate, out RunChange? change)
    {
        candidate = null;
        change = null;
        var blockers = FindContentBlockers(page);
        if (blockers.Count > 0)
        {
            return EditResult.Rejected(EditRejection.PageNotEditable, blockers[0]);
        }

        if (edit.Last + 1 < layout.Pieces.Count && !layout.Pieces[edit.Last].IsEndKnown)
        {
            return EditResult.Rejected(EditRejection.PageNotEditable, "PaperKnife can't measure exactly where this part of the line ends, so it can't move the rest of the line to fit.");
        }

        if (edit.NewText.Length > 0)
        {
            var support = TextSupport.Check(layout.Pieces[edit.First].Font, layout.Run.FontName, edit.NewText);
            if (!support.IsSupported)
            {
                return EditResult.Rejected(EditRejection.UnsupportedCharacters, support.Problem!, support.Characters);
            }
        }

        // Deleting needs no rewrite check: nothing of the deleted text remains, and the checks after
        // the change cover everything else on the page, invisible text included.
        if (!edit.DeletesAll(layout.Pieces.Count))
        {
            var runObjects = layout.Pieces.Select(p => p.ObjectIndex).ToList();
            var rewriteProblem = CheckRewriteKeepsAppearance(pageIndex, runObjects, page.Render(width, height), pagePieces);
            if (rewriteProblem is not null)
            {
                return EditResult.Rejected(EditRejection.PageNotEditable, rewriteProblem);
            }
        }

        using var scratch = PdfDocument.Load(_current, _password);
        var failure = ApplyRunEdit(scratch.GetPage(pageIndex), layout, edit, out var applied);
        if (failure is not null)
        {
            return failure;
        }

        candidate = scratch.Save();
        change = applied;
        return null;
    }

    /// <summary>Rotates a page clockwise by the given number of quarter turns.</summary>
    public EditResult RotatePage(int pageIndex, int quarterTurns)
    {
        lock (PdfiumLibrary.Sync)
        {
            ThrowIfDisposed();
            if (!IsValidPage(pageIndex))
            {
                return EditResult.Rejected(EditRejection.InvalidTarget, "That page no longer exists.");
            }

            var turns = ((quarterTurns % 4) + 4) % 4;
            if (turns == 0)
            {
                return EditResult.NoChange("The page orientation is unchanged.");
            }

            var expected = (_document.GetPage(pageIndex).Rotation + turns) % 4;
            var pageCount = _document.PageCount;
            return ApplyPageChange(
                scratch =>
                {
                    scratch.RotatePage(pageIndex, turns);
                    return true;
                },
                verified => verified.PageCount == pageCount && verified.GetPage(pageIndex).Rotation == expected,
                $"Page {pageIndex + 1} rotated.");
        }
    }

    public EditResult DeletePage(int pageIndex)
    {
        lock (PdfiumLibrary.Sync)
        {
            ThrowIfDisposed();
            if (!IsValidPage(pageIndex))
            {
                return EditResult.Rejected(EditRejection.InvalidTarget, "That page no longer exists.");
            }

            var pageCount = _document.PageCount;
            if (pageCount == 1)
            {
                return EditResult.Rejected(EditRejection.InvalidTarget, "A PDF needs at least one page.");
            }

            return ApplyPageChange(
                scratch =>
                {
                    scratch.DeletePage(pageIndex);
                    return true;
                },
                verified => verified.PageCount == pageCount - 1,
                $"Page {pageIndex + 1} deleted.");
        }
    }

    /// <summary>Moves a page so that it ends up at <paramref name="toIndex"/>.</summary>
    public EditResult MovePage(int fromIndex, int toIndex)
    {
        lock (PdfiumLibrary.Sync)
        {
            ThrowIfDisposed();
            if (!IsValidPage(fromIndex) || !IsValidPage(toIndex))
            {
                return EditResult.Rejected(EditRejection.InvalidTarget, "That page no longer exists.");
            }

            if (fromIndex == toIndex)
            {
                return EditResult.NoChange("The page is already there.");
            }

            var pageCount = _document.PageCount;
            return ApplyPageChange(
                scratch => scratch.MovePage(fromIndex, toIndex),
                verified => verified.PageCount == pageCount,
                $"Page {fromIndex + 1} moved to position {toIndex + 1}.");
        }
    }

    /// <summary>Adds every page of another PDF to the end of this one.</summary>
    public EditResult AppendDocument(PdfSnapshot other, string? password = null)
    {
        ArgumentNullException.ThrowIfNull(other);
        lock (PdfiumLibrary.Sync)
        {
            ThrowIfDisposed();
            using var source = PdfDocument.Load(other, password);
            if (!source.AllowsCopyingPages)
            {
                return EditResult.Rejected(EditRejection.EditingRestricted, "The author of that PDF restricted copying its pages.");
            }

            var added = source.PageCount;
            var pageCount = _document.PageCount;
            return ApplyPageChange(
                scratch => scratch.ImportPages(source, pageCount),
                verified => verified.PageCount == pageCount + added,
                added == 1 ? "1 page added." : $"{added} pages added.");
        }
    }

    public bool Undo()
    {
        lock (PdfiumLibrary.Sync)
        {
            ThrowIfDisposed();
            if (_undo.Last is not { } last)
            {
                return false;
            }

            var document = PdfDocument.Load(last.Value, _password);
            _undo.RemoveLast();
            _redo.Push(_current);
            Replace(last.Value, document);
            return true;
        }
    }

    public bool Redo()
    {
        lock (PdfiumLibrary.Sync)
        {
            ThrowIfDisposed();
            if (!_redo.TryPeek(out var next))
            {
                return false;
            }

            var document = PdfDocument.Load(next, _password);
            _redo.Pop();
            _undo.AddLast(_current);
            Replace(next, document);
            return true;
        }
    }

    /// <summary>
    /// Writes the current snapshot to <paramref name="path"/> through a temporary file in the same
    /// folder, so an existing file is replaced in one step and never left half-written.
    /// </summary>
    public void SaveAs(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath) ?? throw new ArgumentException("Choose a file in a folder.", nameof(path));
        var snapshot = CurrentSnapshot;
        var temporary = Path.Combine(directory, $"~{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                snapshot.WriteTo(stream);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, fullPath, overwrite: true);
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }

        lock (PdfiumLibrary.Sync)
        {
            _saved = snapshot;
            FilePath = fullPath;
        }
    }

    public void Dispose()
    {
        lock (PdfiumLibrary.Sync)
        {
            if (_disposed)
            {
                return;
            }

            _document.Dispose();
            _undo.Clear();
            _redo.Clear();
            _disposed = true;
        }
    }

    /// <summary>
    /// Applies a planned edit to the scratch copy of a page: the first changed object takes the new
    /// text, the other changed objects are removed, and the objects after them move along the
    /// baseline by however much longer or shorter the new text is, so the rest of the run keeps its
    /// spacing.
    /// </summary>
    private static EditResult? ApplyRunEdit(PdfPage page, TextRunLayout layout, TextRunEdit edit, out RunChange change)
    {
        change = null!;
        var pieces = layout.Pieces;
        var first = pieces[edit.First];
        var keepsFirst = edit.NewText.Length > 0;
        var start = layout.DistanceAlongBaseline(first.Matrix.E, first.Matrix.F);
        var end = start;
        var trimmedEnd = start;
        if (keepsFirst)
        {
            var textObject = page.GetTextObject(first.ObjectIndex);
            if (textObject == 0 || !page.SetText(textObject, edit.NewText))
            {
                return EditResult.Rejected(EditRejection.EngineError, "PDFium couldn't change this text.");
            }

            // PDFium sets new text without kerning, one advance width after another.
            end = start + page.MeasureText(first.ObjectIndex, edit.NewText);
            trimmedEnd = start + page.MeasureText(first.ObjectIndex, edit.NewText.TrimEnd());
        }

        // Remove from the end, so the indexes of the objects still to remove stay valid.
        var firstRemoved = keepsFirst ? edit.First + 1 : edit.First;
        for (var i = edit.Last; i >= firstRemoved; i--)
        {
            if (!page.RemoveObject(pieces[i].ObjectIndex))
            {
                return EditResult.Rejected(EditRejection.EngineError, "PDFium couldn't change this text.");
            }
        }

        var removed = edit.Last - firstRemoved + 1;
        var rest = pieces.Skip(edit.Last + 1).ToList();
        var delta = end - layout.EndAlongBaseline(pieces[edit.Last]);
        var moves = rest.Count > 0 && Math.Abs(delta) > first.FontSize * 1e-3;
        var (dx, dy) = moves ? (delta * first.Matrix.A, delta * first.Matrix.B) : (0d, 0d);
        foreach (var piece in moves ? rest : [])
        {
            if (!page.TranslateObject(piece.ObjectIndex - removed, dx, dy))
            {
                return EditResult.Rejected(EditRejection.EngineError, "PDFium couldn't move the rest of the line.");
            }
        }

        if (edit.NewText.Trim().Length > 0)
        {
            // FPDFText_SetText silently turns characters it can't encode into character code 0.
            var stored = page.GetTextLayout().Pieces.FirstOrDefault(p => p.ObjectIndex == first.ObjectIndex);
            if (stored is null || !TextEquivalence.AreEquivalentIgnoringSpacing(stored.Text, edit.NewText))
            {
                return EditResult.Rejected(
                    EditRejection.UnsupportedCharacters,
                    $"The font “{layout.Run.FontName}” in this PDF can't represent some of these characters.",
                    TextEquivalence.Lost(edit.NewText, stored?.Text ?? string.Empty));
            }
        }

        var hiddenBefore = pieces.Skip(edit.First).Take(edit.Last - edit.First + 1).Select(p => p.ObjectIndex).ToList();
        var hiddenAfter = keepsFirst ? new List<int> { first.ObjectIndex } : [];
        if (moves)
        {
            hiddenBefore.AddRange(rest.Select(p => p.ObjectIndex));
            hiddenAfter.AddRange(rest.Select(p => p.ObjectIndex - removed));
        }

        change = new RunChange(layout, edit, removed, dx, dy, end, trimmedEnd, hiddenBefore, hiddenAfter);
        return null;
    }

    /// <param name="piecesBefore">The page's text objects before the edit.</param>
    /// <param name="glyphsToCheck">New characters that must have a glyph in the edited text's font in the candidate (the font changed with the edit).</param>
    /// <param name="copyFont">The name of the complete copy of a font the edited text is drawn with, if any (see <see cref="Fonts.FontCompletion"/>).</param>
    private string? VerifyTextEdit(
        PdfDocument verified,
        PdfSnapshot candidate,
        int pageCount,
        RunChange change,
        PageObjectCounts countsBefore,
        RenderedBitmap before,
        IReadOnlyList<TextPiece> piecesBefore,
        string? glyphsToCheck,
        string? copyFont,
        out PdfTextRun? edited,
        out List<string> warnings)
    {
        edited = null;
        warnings = [];
        if (verified.PageCount != pageCount)
        {
            return "Saving changed the number of pages, so the edit was cancelled.";
        }

        var original = change.Layout.Run;
        var page = verified.GetPage(original.PageIndex);
        var expected = countsBefore with { Text = countsBefore.Text - change.Removed };
        var countsAfter = page.CountObjects();
        if (countsAfter != expected)
        {
            return $"Rewriting this page would drop or alter other content ({expected} expected; {countsAfter} after), so the edit was cancelled.";
        }

        var (pieces, runs) = page.GetTextLayout();
        var byIndex = pieces.ToDictionary(p => p.ObjectIndex);
        var edit = change.Edit;
        var originalPieces = change.Layout.Pieces;
        var deletes = edit.DeletesAll(originalPieces.Count);
        if (deletes && !KeepsOtherText(piecesBefore, change.HiddenBefore, byIndex))
        {
            return "Deleting this text would also change other text on the page, so it was cancelled.";
        }

        var rest = originalPieces.Skip(edit.Last + 1).ToList();
        if (edit.NewText.Trim().Length > 0)
        {
            if (!byIndex.TryGetValue(originalPieces[edit.First].ObjectIndex, out var stored)
                || !TextEquivalence.AreEquivalentIgnoringSpacing(stored.Text, edit.NewText))
            {
                return "The new text didn't survive saving, so the edit was cancelled.";
            }

            // A font PDFium can't load is replaced by one of its own, which may draw every letter.
            if (copyFont is not null && (!stored.IsEmbedded || Fonts.Type1Font.BaseName(stored.FontName) != copyFont))
            {
                return "PaperKnife couldn't confirm that the new text uses the font it added, so the edit was cancelled.";
            }

            if (glyphsToCheck is { Length: > 0 } && !TextSupport.Check(stored.Font, original.FontName, glyphsToCheck).IsSupported)
            {
                return "PaperKnife couldn't confirm that the font draws the new letters, so the edit was cancelled.";
            }

            // The rest of the run was placed after the advance widths of the new text, so PDFium
            // must lay it out that way. Checked here, not in memory: saving drops character and
            // word spacing. PDFium may or may not report the object's trailing spaces.
            var storedEnd = change.Layout.EndAlongBaseline(stored);
            var tolerance = originalPieces[edit.First].FontSize * 0.01;
            if (rest.Count > 0 && Math.Abs(storedEnd - change.End) > tolerance && Math.Abs(storedEnd - change.TrimmedEnd) > tolerance)
            {
                return "PDFium laid out the new text differently than expected, so the edit was cancelled.";
            }
        }

        var visible = page.GetVisibleBox();
        foreach (var piece in rest)
        {
            // Spaces drawn on their own have no characters to compare; their position is checked.
            if (!byIndex.TryGetValue(piece.ObjectIndex - change.Removed, out var moved)
                || moved.HasCharacters != piece.HasCharacters
                || (piece.HasCharacters && !TextEquivalence.AreEquivalent(moved.Text, piece.Text))
                || Math.Abs(moved.Matrix.E - (piece.Matrix.E + change.ShiftX)) > 0.01
                || Math.Abs(moved.Matrix.F - (piece.Matrix.F + change.ShiftY)) > 0.01)
            {
                return "The rest of the line didn't survive saving, so the edit was cancelled.";
            }

            if (piece.HasCharacters && piece.Bounds is { } from && moved.Bounds is { } to && SticksOutFurther(from, to, visible))
            {
                return "Moving the rest of the line would push part of it off the page, so the edit was cancelled.";
            }
        }

        if (change.MovedRest
            && !MovedTextKeepsItsClipping(original.PageIndex, [.. rest.Select(p => p.ObjectIndex)], change.ShiftX, change.ShiftY, before.Width, before.Height))
        {
            return "Moving the rest of the line would change how much of it shows, because the PDF clips it to an area, so the edit was cancelled.";
        }

        // Hide the changed objects in both versions and compare the entire page, so content around,
        // under and over the edited text is checked too.
        var after = RenderWithoutObjects(candidate, original.PageIndex, change.HiddenAfter, before.Width, before.Height);
        var changed = EditVerifier.CountChangedPixels(before, after);
        if (changed > 0)
        {
            return $"This edit would also change {changed:N0} pixels elsewhere on the page (PDFium can't rewrite some spacing, shading or images exactly), so it was cancelled.";
        }

        if (!deletes)
        {
            edited = runs.FirstOrDefault(r => r.Contains(original.ObjectIndex))?.Run;
            if (edited is null)
            {
                return "The new text didn't survive saving, so the edit was cancelled.";
            }

            foreach (var other in runs.Select(r => r.Run))
            {
                if (other.ObjectIndex != edited.ObjectIndex && !other.IsInvisible
                    && other.Bounds.Intersects(edited.Bounds) && !other.Bounds.Intersects(original.Bounds))
                {
                    warnings.Add("The new text now overlaps nearby text. Undo if that isn't what you want.");
                    break;
                }
            }
        }

        if (verified.SignatureCount > 0)
        {
            warnings.Add("This PDF is digitally signed; editing it invalidates the signature.");
        }

        return null;
    }

    /// <summary>
    /// Whether every text object but the deleted ones is still there, in the same order, with the
    /// same text, geometry and rendering mode. Invisible text, which the page comparison can't
    /// see, is checked this way too.
    /// </summary>
    /// <param name="deleted">The page's indexes of the deleted objects before the edit.</param>
    /// <param name="after">The page's text objects after the edit, by index.</param>
    /// <remarks>
    /// PDFium reads an object's text in the context of its neighbours: a hyphen or soft hyphen that
    /// ends a line reads as U+0002 only when more text follows on the next line, the spaces after
    /// it may be left out, and a copy drawn over other text (as for fake bold) reads as nothing. So
    /// text is compared as a reader sees it, and an object that read as nothing may now read as text.
    /// </remarks>
    private static bool KeepsOtherText(IReadOnlyList<TextPiece> before, IReadOnlyList<int> deleted, Dictionary<int, TextPiece> after)
    {
        var removed = deleted.Order().ToList();
        foreach (var piece in before)
        {
            var position = removed.BinarySearch(piece.ObjectIndex);
            if (position >= 0)
            {
                continue;
            }

            // The objects after a deleted one move up by one index each.
            if (!after.TryGetValue(piece.ObjectIndex - ~position, out var kept)
                || kept.RenderMode != piece.RenderMode || !SameGeometry(kept, piece)
                || (piece.HasCharacters && (!kept.HasCharacters || !TextEquivalence.AreEquivalentIgnoringSpacing(AsWritten(kept.Text), AsWritten(piece.Text)))))
            {
                return false;
            }
        }

        return true;

        // A hyphen or soft hyphen that ends a line reads as U+0002 while the next line follows it.
        static string AsWritten(string text) => text.Replace('\u0002', '-').Replace('\u00AD', '-');
    }

    /// <summary>
    /// Whether a text object is placed and spaced as it was: the same matrix, and the same bounds,
    /// which also change with letter and word spacing, as PDFium's page rewrite may drop them.
    /// </summary>
    private static bool SameGeometry(TextPiece kept, TextPiece piece)
    {
        var m = piece.Matrix;
        var tolerance = (Math.Max(Math.Max(Math.Abs(m.A), Math.Abs(m.B)), Math.Max(Math.Abs(m.C), Math.Abs(m.D))) * 1e-4) + 1e-6;
        return kept.HasMatrix == piece.HasMatrix
            && Math.Abs(kept.Matrix.E - m.E) <= 0.01 && Math.Abs(kept.Matrix.F - m.F) <= 0.01
            && Math.Abs(kept.Matrix.A - m.A) <= tolerance && Math.Abs(kept.Matrix.B - m.B) <= tolerance
            && Math.Abs(kept.Matrix.C - m.C) <= tolerance && Math.Abs(kept.Matrix.D - m.D) <= tolerance
            && (kept.Bounds, piece.Bounds) switch
            {
                (null, null) => true,
                ({ } a, { } b) => Math.Abs(a.Left - b.Left) <= 0.01 && Math.Abs(a.Bottom - b.Bottom) <= 0.01
                    && Math.Abs(a.Right - b.Right) <= 0.01 && Math.Abs(a.Top - b.Top) <= 0.01,
                _ => false,
            };
    }

    /// <summary>
    /// Content that PDFium drops or can't reproduce when it rewrites a page's content stream
    /// (see docs/ARCHITECTURE.md). Pages containing it are refused up front.
    /// </summary>
    private static List<string> FindContentBlockers(PdfPage page)
    {
        var blockers = new List<string>();
        var counts = page.CountObjects();
        if (counts.Shading > 0)
        {
            blockers.Add("This page has gradient shading that PaperKnife can't rewrite safely yet.");
        }

        if (counts.Other > 0)
        {
            blockers.Add("This page has content PaperKnife doesn't recognize.");
        }

        return blockers;
    }

    private EditResult ApplyPageChange(Func<PdfDocument, bool> change, Func<PdfDocument, bool> verify, string successMessage)
    {
        if (!_document.AllowsPageChanges)
        {
            return EditResult.Rejected(EditRejection.EditingRestricted, "The author of this PDF restricted changes to its pages.");
        }

        PdfSnapshot candidate;
        using (var scratch = PdfDocument.Load(_current, _password))
        {
            if (!change(scratch))
            {
                return EditResult.Rejected(EditRejection.EngineError, "PDFium couldn't make this change.");
            }

            candidate = scratch.Save();
        }

        PdfDocument? verified = PdfDocument.Load(candidate, _password);
        try
        {
            if (!verify(verified))
            {
                return EditResult.Rejected(EditRejection.CollateralChange, "The change didn't survive saving, so it was cancelled.");
            }

            Commit(candidate, verified);
            verified = null;
            return EditResult.Applied(successMessage);
        }
        finally
        {
            verified?.Dispose();
        }
    }

    private void Commit(PdfSnapshot snapshot, PdfDocument document)
    {
        _undo.AddLast(_current);
        _redo.Clear();
        Replace(snapshot, document);

        var total = _undo.Sum(s => (long)s.Length);
        while (total > HistoryLimitBytes && _undo.Count > 1)
        {
            total -= _undo.First!.Value.Length;
            _undo.RemoveFirst();
        }
    }

    private void Replace(PdfSnapshot snapshot, PdfDocument document)
    {
        _document.Dispose();
        _document = document;
        _current = snapshot;
        _contentSnapshot = null;
        _contentFile = null;
        _contentPageTree = null;
        _contentPages.Clear();
        Version++;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Renders a page of a snapshot with some objects hidden. The hidden state is never saved.</summary>
    private RenderedBitmap RenderWithoutObjects(PdfSnapshot snapshot, int pageIndex, IReadOnlyList<int> objectIndexes, int width, int height)
    {
        using var document = PdfDocument.Load(snapshot, _password);
        var page = document.GetPage(pageIndex);
        foreach (var objectIndex in objectIndexes)
        {
            if (!page.SetObjectActive(objectIndex, active: false))
            {
                throw new PdfException(PdfErrorCode.OperationFailed, "PDFium couldn't hide the edited text to verify the page.");
            }
        }

        return page.Render(width, height);
    }

    /// <summary>
    /// Has PDFium rewrite the page with nothing changed and checks it still looks the same. This
    /// covers the edited text's own styling (letter and word spacing, print colors), which the
    /// after-edit comparison can't see because it hides that text. Every text object must also keep
    /// its geometry, which covers text the rendering can't show, such as the invisible text of a
    /// scanned page: PDFium's rewrite drops letter and word spacing from all the text it rewrites.
    /// </summary>
    private string? CheckRewriteKeepsAppearance(int pageIndex, IReadOnlyList<int> objectIndexes, RenderedBitmap original, IReadOnlyList<TextPiece> pagePieces)
    {
        PdfSnapshot rewritten;
        using (var probe = PdfDocument.Load(_current, _password))
        {
            var page = probe.GetPage(pageIndex);
            if (!objectIndexes.All(page.MarkObjectModified))
            {
                return "PDFium couldn't prepare this text for editing.";
            }

            rewritten = probe.Save();
        }

        using var reloaded = PdfDocument.Load(rewritten, _password);
        var rewrittenPage = reloaded.GetPage(pageIndex);
        var after = rewrittenPage.Render(original.Width, original.Height);
        if (EditVerifier.CountChangedPixels(original, after) != 0)
        {
            return "PaperKnife can't rewrite this page without changing how it looks (for example letter spacing, print colors or small embedded images), so its text can't be edited yet.";
        }

        var rewrittenPieces = rewrittenPage.GetTextLayout().Pieces.ToDictionary(p => p.ObjectIndex);
        return pagePieces.All(p => rewrittenPieces.TryGetValue(p.ObjectIndex, out var kept) && SameGeometry(kept, p))
            ? null
            : "PaperKnife can't rewrite this page without changing the spacing of some of its text (for example the invisible text of a scanned page), so its text can't be edited yet.";
    }

    /// <summary>
    /// Whether moving the rest of a run leaves its clipping unchanged. Moving a text object doesn't
    /// move its clipping path, so moved text could be cut off at the edge of a clipped area, or text
    /// that was cut off could appear. On a copy of the original page, the objects are moved and
    /// rendered on their own, then rendered again with their clipping paths moved along with them;
    /// both must cover the same pixels. One copy keeps the glyph positions identical, and it keeps
    /// the clipping paths as saved: PDFium drops a rectangular clipping path when it loads a page
    /// in which the path contains its object. Comparing coverage rather than color catches white text.
    /// </summary>
    /// <param name="moved">The moved objects' indexes in the original page.</param>
    private bool MovedTextKeepsItsClipping(int pageIndex, IReadOnlyList<int> moved, double dx, double dy, int width, int height)
    {
        using var document = PdfDocument.Load(_current, _password);
        var page = document.GetPage(pageIndex);
        if (!moved.Any(page.HasClipPath))
        {
            return true;
        }

        page.ShowOnly(moved);
        if (!moved.All(i => page.TranslateObject(i, dx, dy)))
        {
            return false;
        }

        var clipped = page.RenderOnTransparent(width, height);
        if (!moved.All(i => page.TranslateClipPath(i, dx, dy)))
        {
            return false;
        }

        return EditVerifier.CountChangedCoverage(clipped, page.RenderOnTransparent(width, height)) == 0;
    }

    /// <summary>Whether a moved object sticks out of the visible page further than before, on any side.</summary>
    private static bool SticksOutFurther(PdfRect before, PdfRect after, PdfRect page)
    {
        const float Tolerance = 0.01f;
        return Math.Max(0, page.Left - after.Left) > Math.Max(0, page.Left - before.Left) + Tolerance
            || Math.Max(0, after.Right - page.Right) > Math.Max(0, before.Right - page.Right) + Tolerance
            || Math.Max(0, page.Bottom - after.Bottom) > Math.Max(0, page.Bottom - before.Bottom) + Tolerance
            || Math.Max(0, after.Top - page.Top) > Math.Max(0, before.Top - page.Top) + Tolerance;
    }

    private PdfPage GetPage(int pageIndex)
    {
        ThrowIfDisposed();
        return _document.GetPage(pageIndex);
    }

    private bool IsValidPage(int pageIndex) => pageIndex >= 0 && pageIndex < _document.PageCount;

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    /// <summary>
    /// The page's content, read by PaperKnife's own parser, or null when content editing is off or
    /// the file is encrypted or can't be read with certainty. Cached for the current snapshot.
    /// </summary>
    private ContentPage? GetContentPage(int pageIndex)
    {
        if (!UseContentEditing)
        {
            return null;
        }

        if (!ReferenceEquals(_contentSnapshot, _current))
        {
            _contentSnapshot = _current;
            _contentPages.Clear();
            try
            {
                _contentFile = PdfFile.TryOpen(_current.Memory, out _) is { IsEncrypted: false } file ? file : null;
                _contentPageTree = _contentFile?.GetPages() is { } pages && pages.Count == _document.PageCount ? pages : null;
            }
            catch (Exception ex) when (IsReadFailure(ex))
            {
                _contentFile = null;
                _contentPageTree = null;
            }
        }

        if (_contentFile is null || _contentPageTree is null)
        {
            return null;
        }

        if (!_contentPages.TryGetValue(pageIndex, out var page))
        {
            if (_contentPages.Count >= 16)
            {
                // The fonts and character maps read for those pages go with them.
                _contentPages.Clear();
                _contentFile.ForgetDerived();
            }

            try
            {
                page = ContentPage.TryLoad(_contentFile, _contentPageTree, pageIndex);
            }
            catch (Exception ex) when (IsReadFailure(ex))
            {
                page = null;
            }

            _contentPages[pageIndex] = page;
        }

        return page;
    }

    /// <summary>
    /// Exceptions from reading a damaged or unusual file with PaperKnife's own parser. Such files
    /// are left to PDFium rather than failing the operation.
    /// </summary>
    private static bool IsReadFailure(Exception ex) =>
        ex is ArgumentException or IndexOutOfRangeException or InvalidCastException or InvalidOperationException
            or KeyNotFoundException or OverflowException or FormatException or InvalidDataException
            or NullReferenceException or DivideByZeroException;
}
