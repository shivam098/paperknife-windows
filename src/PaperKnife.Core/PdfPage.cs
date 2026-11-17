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
    public RenderedBitmap Render(int pixelWidth, int pixelHeight)
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
                    FPDFBitmap_FillRect(bitmap, 0, 0, pixelWidth, pixelHeight, 0xFFFFFFFF);
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
            var handle = Handle;
            var runs = new List<PdfTextRun>();
            var textPage = FPDFText_LoadPage(handle);
            if (textPage == 0)
            {
                return runs;
            }

            try
            {
                var count = FPDFPage_CountObjects(handle);
                for (var i = 0; i < count; i++)
                {
                    var obj = FPDFPage_GetObject(handle, i);
                    if (obj != 0 && FPDFPageObj_GetType(obj) == FPDF_PAGEOBJ_TEXT)
                    {
                        runs.Add(CreateRun(i, obj, textPage));
                    }
                }
            }
            finally
            {
                FPDFText_ClosePage(textPage);
            }

            return runs;
        }
    }

    public PdfTextRun? TryGetTextRun(int objectIndex)
    {
        lock (PdfiumLibrary.Sync)
        {
            var obj = GetTextObject(objectIndex);
            if (obj == 0)
            {
                return null;
            }

            var textPage = FPDFText_LoadPage(Handle);
            if (textPage == 0)
            {
                return null;
            }

            try
            {
                return CreateRun(objectIndex, obj, textPage);
            }
            finally
            {
                FPDFText_ClosePage(textPage);
            }
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

    internal nint GetTextFont(int objectIndex)
    {
        var obj = GetTextObject(objectIndex);
        return obj == 0 ? 0 : FPDFTextObj_GetFont(obj);
    }

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

    /// <summary>Reads a text object's text through a fresh text page, so it reflects recent edits.</summary>
    internal string ReadText(nint textObject)
    {
        var textPage = FPDFText_LoadPage(Handle);
        if (textPage == 0)
        {
            return string.Empty;
        }

        try
        {
            return ReadText(textObject, textPage);
        }
        finally
        {
            FPDFText_ClosePage(textPage);
        }
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

    private static string ReadText(nint textObject, nint textPage)
    {
        var byteLength = FPDFTextObj_GetText(textObject, textPage, null, 0);
        if (byteLength <= 2)
        {
            return string.Empty;
        }

        var buffer = new char[byteLength / 2];
        fixed (char* chars = buffer)
        {
            byteLength = FPDFTextObj_GetText(textObject, textPage, (ushort*)chars, byteLength);
        }

        // The returned length includes the terminating NUL. PDFium also attributes the gap before
        // the next run on a line to this run as a generated space, so trailing whitespace is dropped.
        return new string(buffer, 0, Math.Max(0, (int)(byteLength / 2) - 1)).TrimEnd();
    }

    private static (string Name, bool IsSubset) ReadFontName(nint font)
    {
        if (font == 0)
        {
            return ("(no font)", false);
        }

        var length = FPDFFont_GetBaseFontName(font, null, 0);
        if (length <= 1)
        {
            return ("(unnamed font)", false);
        }

        var buffer = new byte[(int)length];
        fixed (byte* bytes = buffer)
        {
            FPDFFont_GetBaseFontName(font, bytes, length);
        }

        var name = Encoding.UTF8.GetString(buffer, 0, (int)length - 1);

        // Subset fonts carry a six-uppercase-letter tag, e.g. "ABCDEF+Arial-BoldMT" (ISO 32000-1, 9.6.4).
        var isSubset = name.Length > 7 && name[6] == '+' && !name.AsSpan(0, 6).ContainsAnyExceptInRange('A', 'Z');
        return (isSubset ? name[7..] : name, isSubset);
    }

    private PdfTextRun CreateRun(int objectIndex, nint obj, nint textPage)
    {
        float left, bottom, right, top;
        FPDFPageObj_GetBounds(obj, &left, &bottom, &right, &top);

        var font = FPDFTextObj_GetFont(obj);
        var (fontName, isSubset) = ReadFontName(font);

        float size = 0;
        FPDFTextObj_GetFontSize(obj, &size);
        FsMatrix matrix;
        var scale = FPDFPageObj_GetMatrix(obj, &matrix) != 0
            ? MathF.Sqrt((matrix.C * matrix.C) + (matrix.D * matrix.D))
            : 1f;

        return new PdfTextRun(
            Index,
            objectIndex,
            ReadText(obj, textPage),
            new PdfRect(left, bottom, right, top),
            fontName,
            font != 0 && FPDFFont_GetIsEmbedded(font) == 1,
            isSubset,
            size * scale,
            FPDFTextObj_GetTextRenderMode(obj) == FPDF_TEXTRENDERMODE_INVISIBLE);
    }
}
