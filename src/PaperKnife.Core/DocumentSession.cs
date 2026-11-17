using PaperKnife.Core.Editing;
using PaperKnife.Core.Pdfium;

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
    private PdfDocument _document;
    private PdfSnapshot _current;
    private PdfSnapshot _saved;
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

            blockers.AddRange(FindContentBlockers(page));
            return blockers;
        }
    }

    /// <summary>
    /// Replaces the text of one text run, keeping its font, size, position and color. Trailing
    /// whitespace is dropped: it is invisible, and a character the font can't draw there would be
    /// impossible to detect.
    /// </summary>
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
            var original = page.TryGetTextRun(objectIndex);
            if (original is null)
            {
                return EditResult.Rejected(EditRejection.InvalidTarget, "The selected text no longer exists. Select it again.");
            }

            if (text.Length == 0)
            {
                return EditResult.Rejected(EditRejection.EmptyText, "Type at least one character. Deleting text isn't supported yet.");
            }

            if (TextEquivalence.AreEquivalent(text, original.Text))
            {
                return EditResult.NoChange("The text is unchanged.");
            }

            if (!_document.AllowsContentChanges)
            {
                return EditResult.Rejected(EditRejection.EditingRestricted, "The author of this PDF restricted editing.");
            }

            var blockers = FindContentBlockers(page);
            if (blockers.Count > 0)
            {
                return EditResult.Rejected(EditRejection.PageNotEditable, blockers[0]);
            }

            var support = TextSupport.Check(page.GetTextFont(objectIndex), original.FontName, text);
            if (!support.IsSupported)
            {
                return EditResult.Rejected(EditRejection.UnsupportedCharacters, support.Problem!, support.Characters);
            }

            var pageCount = _document.PageCount;
            var countsBefore = page.CountObjects();
            var (width, height) = EditVerifier.CheckSize(page.Width, page.Height);
            var rewriteProblem = CheckRewriteKeepsAppearance(pageIndex, objectIndex, page.Render(width, height));
            if (rewriteProblem is not null)
            {
                return EditResult.Rejected(EditRejection.PageNotEditable, rewriteProblem);
            }

            var before = RenderWithoutObject(_current, pageIndex, objectIndex, width, height);

            PdfSnapshot candidate;
            using (var scratch = PdfDocument.Load(_current, _password))
            {
                var scratchPage = scratch.GetPage(pageIndex);
                var textObject = scratchPage.GetTextObject(objectIndex);
                if (textObject == 0 || !scratchPage.SetText(textObject, text))
                {
                    return EditResult.Rejected(EditRejection.EngineError, "PDFium couldn't change this text.");
                }

                var stored = scratchPage.ReadText(textObject);
                if (!TextEquivalence.AreEquivalent(stored, text))
                {
                    return EditResult.Rejected(
                        EditRejection.UnsupportedCharacters,
                        $"The font “{original.FontName}” in this PDF can't represent some of these characters.",
                        TextEquivalence.Lost(text, stored));
                }

                candidate = scratch.Save();
            }

            PdfDocument? verified = PdfDocument.Load(candidate, _password);
            try
            {
                var problem = VerifyTextEdit(verified, candidate, pageCount, original, text, countsBefore, before, out var edited, out var warnings);
                if (problem is not null)
                {
                    return EditResult.Rejected(EditRejection.CollateralChange, problem);
                }

                Commit(candidate, verified);
                verified = null;
                return EditResult.Applied("Text updated. Verified: nothing else on this page changed.", warnings, edited);
            }
            finally
            {
                verified?.Dispose();
            }
        }
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

    private string? VerifyTextEdit(
        PdfDocument verified,
        PdfSnapshot candidate,
        int pageCount,
        PdfTextRun original,
        string newText,
        PageObjectCounts countsBefore,
        RenderedBitmap before,
        out PdfTextRun? edited,
        out List<string> warnings)
    {
        edited = null;
        warnings = [];
        if (verified.PageCount != pageCount)
        {
            return "Saving changed the number of pages, so the edit was cancelled.";
        }

        var page = verified.GetPage(original.PageIndex);
        var countsAfter = page.CountObjects();
        if (countsAfter != countsBefore)
        {
            return $"Rewriting this page would drop or alter other content ({countsBefore} before; {countsAfter} after), so the edit was cancelled.";
        }

        edited = page.TryGetTextRun(original.ObjectIndex);
        if (edited is null || !TextEquivalence.AreEquivalent(edited.Text, newText))
        {
            return "The new text didn't survive saving, so the edit was cancelled.";
        }

        // Hide the edited run in both versions and compare the entire page, so content around,
        // under and over the edited text is checked too.
        var after = RenderWithoutObject(candidate, original.PageIndex, original.ObjectIndex, before.Width, before.Height);
        var changed = EditVerifier.CountChangedPixels(before, after);
        if (changed > 0)
        {
            return $"This edit would also change {changed:N0} pixels elsewhere on the page (PDFium can't rewrite some spacing, shading or images exactly), so it was cancelled.";
        }

        foreach (var other in page.GetTextRuns())
        {
            if (other.ObjectIndex != original.ObjectIndex && !other.IsInvisible
                && other.Bounds.Intersects(edited.Bounds) && !other.Bounds.Intersects(original.Bounds))
            {
                warnings.Add("The new text now overlaps nearby text. Undo if that isn't what you want.");
                break;
            }
        }

        if (verified.SignatureCount > 0)
        {
            warnings.Add("This PDF is digitally signed; editing it invalidates the signature.");
        }

        return null;
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

    /// <summary>Renders a page of a snapshot with one object hidden. The hidden state is never saved.</summary>
    private RenderedBitmap RenderWithoutObject(PdfSnapshot snapshot, int pageIndex, int objectIndex, int width, int height)
    {
        using var document = PdfDocument.Load(snapshot, _password);
        var page = document.GetPage(pageIndex);
        if (!page.SetObjectActive(objectIndex, active: false))
        {
            throw new PdfException(PdfErrorCode.OperationFailed, "PDFium couldn't hide the edited text to verify the page.");
        }

        return page.Render(width, height);
    }

    /// <summary>
    /// Has PDFium rewrite the page with nothing changed and checks it still looks the same. This
    /// covers the edited text's own styling (letter and word spacing, print colors), which the
    /// after-edit comparison can't see because it hides that text.
    /// </summary>
    private string? CheckRewriteKeepsAppearance(int pageIndex, int objectIndex, RenderedBitmap original)
    {
        PdfSnapshot rewritten;
        using (var probe = PdfDocument.Load(_current, _password))
        {
            if (!probe.GetPage(pageIndex).MarkObjectModified(objectIndex))
            {
                return "PDFium couldn't prepare this text for editing.";
            }

            rewritten = probe.Save();
        }

        using var reloaded = PdfDocument.Load(rewritten, _password);
        var after = reloaded.GetPage(pageIndex).Render(original.Width, original.Height);
        return EditVerifier.CountChangedPixels(original, after) == 0
            ? null
            : "PaperKnife can't rewrite this page without changing how it looks (for example letter spacing, print colors or small embedded images), so its text can't be edited yet.";
    }

    private PdfPage GetPage(int pageIndex)
    {
        ThrowIfDisposed();
        return _document.GetPage(pageIndex);
    }

    private bool IsValidPage(int pageIndex) => pageIndex >= 0 && pageIndex < _document.PageCount;

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
