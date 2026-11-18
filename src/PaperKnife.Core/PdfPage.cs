using System.Text;
using PaperKnife.Core.Pdfium;
using static PaperKnife.Core.Pdfium.PdfiumNative;

namespace PaperKnife.Core;

/// <summary>A loaded page. Owned by its <see cref="PdfDocument"/> and invalid once the document is disposed.</summary>
public sealed unsafe class PdfPage
{
    private const long MaxRenderPixels = 120_000_000;

    private readonly PdfDocument _document;
    private nint _handle;
    private bool _contentDirty;

    internal PdfPage(PdfDocument document, nint handle, int index)
    {
        _document = document;
        _handle = handle;
        Index = index;
    }

    public int Index { get; }

    /// <summary>Displayed width in points (rotation applied).</summary>
    public float Width
    {
        get
        {
            lock (PdfiumLibrary.Sync)
            {
                return FPDF_GetPageWidthF(Handle);
            }
        }
    }

    /// <summary>Displayed height in points (rotation applied).</summary>
    public float Height
    {
        get
        {
            lock (PdfiumLibrary.Sync)
            {
                return FPDF_GetPageHeightF(Handle);
            }
        }
    }

    /// <summary>Clockwise rotation in quarter turns (0-3), from the page's /Rotate entry.</summary>
    public int Rotation
    {
        get
        {
            lock (PdfiumLibrary.Sync)
            {
                return Math.Max(0, FPDFPage_GetRotation(Handle));
            }
        }
    }

    internal nint Handle
    {
        get
        {
            if (_handle == 0 || _document.IsDisposed)
            {
                throw new ObjectDisposedException(nameof(PdfPage));
            }

            return _handle;
        }
    }

    /// <summary>Renders the whole page, with annotations, onto a white background.</summary>
    public RenderedBitmap Render(int pixelWidth, int pixelHeight) => Render(pixelWidth, pixelHeight, 0xFFFFFFFF);

    /// <summary>
    /// Renders the page onto a transparent background, so the alpha channel shows what is drawn,
    /// in any color, white included.
    /// </summary>
    internal RenderedBitmap RenderOnTransparent(int pixelWidth, int pixelHeight) => Render(pixelWidth, pixelHeight, 0);

    private RenderedBitmap Render(int pixelWidth, int pixelHeight, uint background)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pixelWidth, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(pixelHeight, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan((long)pixelWidth * pixelHeight, MaxRenderPixels, nameof(pixelWidth));

        var stride = pixelWidth * 4;
        var pixels = GC.AllocateUninitializedArray<byte>(stride * pixelHeight);
        lock (PdfiumLibrary.Sync)
        {
            var handle = Handle;
            fixed (byte* buffer = pixels)
            {
                var bitmap = FPDFBitmap_CreateEx(pixelWidth, pixelHeight, FPDFBitmap_BGRA, buffer, stride);
                if (bitmap == 0)
                {
                    throw new PdfException(PdfErrorCode.OperationFailed, "There isn't enough memory to draw this page.");
                }

                try
                {
                    FPDFBitmap_FillRect(bitmap, 0, 0, pixelWidth, pixelHeight, background);
                    FPDF_RenderPageBitmap(bitmap, handle, 0, 0, pixelWidth, pixelHeight, 0, FPDF_ANNOT);
                }
                finally
                {
                    FPDFBitmap_Destroy(bitmap);
                }
            }
        }

        return new RenderedBitmap(pixelWidth, pixelHeight, stride, pixels);
    }

    public PageObjectCounts CountObjects()
    {
        lock (PdfiumLibrary.Sync)
        {
            var handle = Handle;
            int text = 0, path = 0, image = 0, shading = 0, form = 0, other = 0;
            var count = FPDFPage_CountObjects(handle);
            for (var i = 0; i < count; i++)
            {
                switch (FPDFPageObj_GetType(FPDFPage_GetObject(handle, i)))
                {
                    case FPDF_PAGEOBJ_TEXT: text++; break;
                    case FPDF_PAGEOBJ_PATH: path++; break;
                    case FPDF_PAGEOBJ_IMAGE: image++; break;
                    case FPDF_PAGEOBJ_SHADING: shading++; break;
                    case FPDF_PAGEOBJ_FORM: form++; break;
                    default: other++; break;
                }
            }

            return new PageObjectCounts(text, path, image, shading, form, other);
        }
    }

    /// <summary>All top-level text runs, in drawing order. Text nested in form XObjects is not included.</summary>
    public IReadOnlyList<PdfTextRun> GetTextRuns()
    {
        lock (PdfiumLibrary.Sync)
        {
            return TextLayout.Build(Handle, Index).Runs.Select(r => r.Run).ToList();
        }
    }

    /// <summary>The run that contains the page object at <paramref name="objectIndex"/>, if it is text.</summary>
    public PdfTextRun? TryGetTextRun(int objectIndex)
    {
        lock (PdfiumLibrary.Sync)
        {
            return GetRunLayout(objectIndex)?.Run;
        }
    }

    /// <summary>Finds the most specific visible text run under a pixel of a rendering of the given size.</summary>
    public PdfTextRun? HitTestText(int pixelWidth, int pixelHeight, int x, int y)
    {
        lock (PdfiumLibrary.Sync)
        {
            double pageX, pageY;
            if (FPDF_DeviceToPage(Handle, 0, 0, pixelWidth, pixelHeight, 0, x, y, &pageX, &pageY) == 0)
            {
                return null;
            }

            PdfTextRun? best = null;
            foreach (var run in GetTextRuns())
            {
                if (string.IsNullOrWhiteSpace(run.Text) || !run.Bounds.Inflate(1f).Contains(pageX, pageY))
                {
                    continue;
                }

                if (best is null || IsBetterHit(run, best))
                {
                    best = run;
                }
            }

            return best;
        }
    }

    private static bool IsBetterHit(PdfTextRun candidate, PdfTextRun current)
    {
        // Visible text wins over invisible OCR layers, then the most specific run, then the topmost one.
        if (candidate.IsInvisible != current.IsInvisible)
        {
            return !candidate.IsInvisible;
        }

        if (candidate.Bounds.Area != current.Bounds.Area)
        {
            return candidate.Bounds.Area < current.Bounds.Area;
        }

        return candidate.ObjectIndex > current.ObjectIndex;
    }

    /// <summary>Maps a page-space rectangle to the pixels it covers in a rendering of the given size.</summary>
    public PixelRect MapToPixels(PdfRect rect, int pixelWidth, int pixelHeight)
    {
        lock (PdfiumLibrary.Sync)
        {
            var handle = Handle;
            ReadOnlySpan<double> xs = [rect.Left, rect.Left, rect.Right, rect.Right];
            ReadOnlySpan<double> ys = [rect.Bottom, rect.Top, rect.Bottom, rect.Top];
            int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
            for (var i = 0; i < 4; i++)
            {
                int deviceX, deviceY;
                FPDF_PageToDevice(handle, 0, 0, pixelWidth, pixelHeight, 0, xs[i], ys[i], &deviceX, &deviceY);
                minX = Math.Min(minX, deviceX);
                minY = Math.Min(minY, deviceY);
                maxX = Math.Max(maxX, deviceX);
                maxY = Math.Max(maxY, deviceY);
            }

            return new PixelRect(minX, minY, maxX - minX, maxY - minY);
        }
    }

    internal nint GetTextObject(int objectIndex)
    {
        var handle = Handle;
        if (objectIndex < 0 || objectIndex >= FPDFPage_CountObjects(handle))
        {
            return 0;
        }

        var obj = FPDFPage_GetObject(handle, objectIndex);
        return obj != 0 && FPDFPageObj_GetType(obj) == FPDF_PAGEOBJ_TEXT ? obj : 0;
    }

    /// <summary>The page's top-level text objects and the runs they form, valid until the page changes.</summary>
    internal (IReadOnlyList<TextPiece> Pieces, IReadOnlyList<TextRunLayout> Runs) GetTextLayout() => TextLayout.Build(Handle, Index);

    /// <summary>The run that contains the page object at <paramref name="objectIndex"/>, with the objects it is made of.</summary>
    internal TextRunLayout? GetRunLayout(int objectIndex) =>
        GetTextLayout().Runs.FirstOrDefault(r => r.Contains(objectIndex));

    /// <summary>Hides or shows one object in memory, for example to render a page without it.</summary>
    internal bool SetObjectActive(int objectIndex, bool active)
    {
        var handle = Handle;
        if (objectIndex < 0 || objectIndex >= FPDFPage_CountObjects(handle))
        {
            return false;
        }

        var obj = FPDFPage_GetObject(handle, objectIndex);
        return obj != 0 && FPDFPageObj_SetIsActive(obj, active ? 1 : 0) != 0;
    }

    /// <summary>
    /// Marks one object as modified without changing it (an identity transform), so the next save
    /// makes PDFium rewrite the content stream that contains it.
    /// </summary>
    internal bool MarkObjectModified(int objectIndex)
    {
        var handle = Handle;
        if (objectIndex < 0 || objectIndex >= FPDFPage_CountObjects(handle))
        {
            return false;
        }

        var obj = FPDFPage_GetObject(handle, objectIndex);
        if (obj == 0)
        {
            return false;
        }

        FPDFPageObj_Transform(obj, 1, 0, 0, 1, 0, 0);
        _contentDirty = true;
        return true;
    }

    internal bool SetText(nint textObject, string text)
    {
        fixed (char* chars = text)
        {
            // .NET strings are NUL-terminated, as FPDF_WIDESTRING requires.
            if (FPDFText_SetText(textObject, (ushort*)chars) == 0)
            {
                return false;
            }
        }

        _contentDirty = true;
        return true;
    }

    /// <summary>Removes one object from the page and frees it.</summary>
    internal bool RemoveObject(int objectIndex)
    {
        var handle = Handle;
        if (objectIndex < 0 || objectIndex >= FPDFPage_CountObjects(handle))
        {
            return false;
        }

        var obj = FPDFPage_GetObject(handle, objectIndex);
        if (obj == 0 || FPDFPage_RemoveObject(handle, obj) == 0)
        {
            return false;
        }

        FPDFPageObj_Destroy(obj);
        _contentDirty = true;
        return true;
    }

    /// <summary>The advance width of <paramref name="text"/> in a text object's font and size, in its text space units.</summary>
    internal double MeasureText(int objectIndex, string text)
    {
        var obj = GetTextObject(objectIndex);
        if (obj == 0)
        {
            return double.NaN;
        }

        float size = 0;
        FPDFTextObj_GetFontSize(obj, &size);
        return TextLayout.Measure(FPDFTextObj_GetFont(obj), size, text);
    }

    /// <summary>Moves one object by a page-space offset.</summary>
    internal bool TranslateObject(int objectIndex, double dx, double dy)
    {
        var handle = Handle;
        if (objectIndex < 0 || objectIndex >= FPDFPage_CountObjects(handle))
        {
            return false;
        }

        var obj = FPDFPage_GetObject(handle, objectIndex);
        if (obj == 0)
        {
            return false;
        }

        FPDFPageObj_Transform(obj, 1, 0, 0, 1, dx, dy);
        _contentDirty = true;
        return true;
    }

    /// <summary>The part of the page that is shown: its media box cut to its crop box, in page space.</summary>
    internal PdfRect GetVisibleBox()
    {
        FsRectF box;
        return FPDF_GetPageBoundingBox(Handle, &box) != 0
            ? new PdfRect(box.Left, box.Bottom, box.Right, box.Top)
            : new PdfRect(float.NegativeInfinity, float.NegativeInfinity, float.PositiveInfinity, float.PositiveInfinity);
    }

    /// <summary>Whether a page object is clipped by at least one path.</summary>
    internal bool HasClipPath(int objectIndex)
    {
        var obj = GetObjectHandle(objectIndex);
        var clip = obj == 0 ? 0 : FPDFPageObj_GetClipPath(obj);
        return clip != 0 && FPDFClipPath_CountPaths(clip) > 0;
    }

    /// <summary>Moves one object's clipping path by a page-space offset, in memory only, for example to render it.</summary>
    internal bool TranslateClipPath(int objectIndex, double dx, double dy)
    {
        var obj = GetObjectHandle(objectIndex);
        if (obj == 0)
        {
            return false;
        }

        FPDFPageObj_TransformClipPath(obj, 1, 0, 0, 1, dx, dy);
        return true;
    }

    /// <summary>Hides every object except the given ones, in memory, to render them on their own.</summary>
    internal void ShowOnly(IReadOnlyCollection<int> objectIndexes)
    {
        var handle = Handle;
        var shown = objectIndexes.ToHashSet();
        var count = FPDFPage_CountObjects(handle);
        for (var i = 0; i < count; i++)
        {
            FPDFPageObj_SetIsActive(FPDFPage_GetObject(handle, i), shown.Contains(i) ? 1 : 0);
        }
    }

    private nint GetObjectHandle(int objectIndex)
    {
        var handle = Handle;
        return objectIndex >= 0 && objectIndex < FPDFPage_CountObjects(handle) ? FPDFPage_GetObject(handle, objectIndex) : 0;
    }

    internal void SetRotation(int quarterTurns) => FPDFPage_SetRotation(Handle, quarterTurns);

    internal void MarkContentDirty() => _contentDirty = true;

    internal void CommitContent()
    {
        if (!_contentDirty || _handle == 0)
        {
            return;
        }

        if (FPDFPage_GenerateContent(_handle) == 0)
        {
            throw new PdfException(PdfErrorCode.OperationFailed, "PDFium couldn't rewrite the page content.");
        }

        _contentDirty = false;
    }

    internal void Close()
    {
        if (_handle != 0)
        {
            FPDF_ClosePage(_handle);
            _handle = 0;
        }
    }
}
