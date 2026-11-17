using System.Runtime.InteropServices;

namespace PaperKnife.Core.Pdfium;

/// <summary>
/// PDFium's <c>FPDF_FILEWRITE</c>, followed by a GCHandle to the managed sink.
/// PDFium only reads the first two fields and hands the struct pointer back to the callback.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct FpdfFileWrite
{
    public int Version;
    public delegate* unmanaged<FpdfFileWrite*, void*, uint, int> WriteBlock;
    public nint State;
}

[StructLayout(LayoutKind.Sequential)]
internal struct FsMatrix
{
    public float A;
    public float B;
    public float C;
    public float D;
    public float E;
    public float F;
}

/// <summary>
/// The subset of the PDFium C API that PaperKnife uses, matching the headers shipped in
/// bblanchon.PDFium.Win32 159.0.8152 (chromium/8152). PDFium is not thread-safe: call these only
/// while holding <see cref="PdfiumLibrary.Sync"/>. On Windows, C <c>unsigned long</c>
/// (<c>FPDF_DWORD</c>) is 32 bits wide and maps to <see cref="uint"/>.
/// </summary>
internal static unsafe partial class PdfiumNative
{
    private const string Library = "pdfium";

    public const uint FPDF_ERR_FILE = 2;
    public const uint FPDF_ERR_FORMAT = 3;
    public const uint FPDF_ERR_PASSWORD = 4;
    public const uint FPDF_ERR_SECURITY = 5;
    public const uint FPDF_ERR_PAGE = 6;

    public const int FPDF_ANNOT = 0x01;
    public const int FPDFBitmap_BGRA = 4;
    public const uint FPDF_NO_INCREMENTAL = 1 << 1;

    public const int FPDF_PAGEOBJ_TEXT = 1;
    public const int FPDF_PAGEOBJ_PATH = 2;
    public const int FPDF_PAGEOBJ_IMAGE = 3;
    public const int FPDF_PAGEOBJ_SHADING = 4;
    public const int FPDF_PAGEOBJ_FORM = 5;

    public const int FPDF_TEXTRENDERMODE_INVISIBLE = 3;
    public const int FPDF_FONT_TRUETYPE = 2;
    public const int FPDF_FILLMODE_ALTERNATE = 1;

    /// <summary>Permission bit 4 (ISO 32000-1, table 22): modify the contents of the document.</summary>
    public const uint PermissionModifyContents = 1u << 3;

    /// <summary>Permission bit 5: copy or otherwise extract text and graphics.</summary>
    public const uint PermissionCopy = 1u << 4;

    /// <summary>Permission bit 11 (security handler revision 3 and later): assemble the document.</summary>
    public const uint PermissionAssemble = 1u << 10;

    // fpdfview.h
    [LibraryImport(Library)] public static partial void FPDF_InitLibrary();
    [LibraryImport(Library)] public static partial uint FPDF_GetLastError();
    [LibraryImport(Library)] public static partial nint FPDF_LoadMemDocument64(void* dataBuffer, nuint size, byte* password);
    [LibraryImport(Library)] public static partial void FPDF_CloseDocument(nint document);
    [LibraryImport(Library)] public static partial int FPDF_GetPageCount(nint document);
    [LibraryImport(Library)] public static partial uint FPDF_GetDocPermissions(nint document);
    [LibraryImport(Library)] public static partial int FPDF_GetSecurityHandlerRevision(nint document);
    [LibraryImport(Library)] public static partial nint FPDF_LoadPage(nint document, int pageIndex);
    [LibraryImport(Library)] public static partial void FPDF_ClosePage(nint page);
    [LibraryImport(Library)] public static partial float FPDF_GetPageWidthF(nint page);
    [LibraryImport(Library)] public static partial float FPDF_GetPageHeightF(nint page);
    [LibraryImport(Library)] public static partial nint FPDFBitmap_CreateEx(int width, int height, int format, void* firstScan, int stride);
    [LibraryImport(Library)] public static partial int FPDFBitmap_FillRect(nint bitmap, int left, int top, int width, int height, uint color);
    [LibraryImport(Library)] public static partial void FPDFBitmap_Destroy(nint bitmap);
    [LibraryImport(Library)] public static partial void FPDF_RenderPageBitmap(nint bitmap, nint page, int startX, int startY, int sizeX, int sizeY, int rotate, int flags);
    [LibraryImport(Library)] public static partial int FPDF_DeviceToPage(nint page, int startX, int startY, int sizeX, int sizeY, int rotate, int deviceX, int deviceY, double* pageX, double* pageY);
    [LibraryImport(Library)] public static partial int FPDF_PageToDevice(nint page, int startX, int startY, int sizeX, int sizeY, int rotate, double pageX, double pageY, int* deviceX, int* deviceY);

    // fpdf_save.h
    [LibraryImport(Library)] public static partial int FPDF_SaveAsCopy(nint document, FpdfFileWrite* fileWrite, uint flags);

    // fpdf_edit.h
    [LibraryImport(Library)] public static partial nint FPDF_CreateNewDocument();
    [LibraryImport(Library)] public static partial nint FPDFPage_New(nint document, int pageIndex, double width, double height);
    [LibraryImport(Library)] public static partial void FPDFPage_Delete(nint document, int pageIndex);
    [LibraryImport(Library)] public static partial int FPDF_MovePages(nint document, int* pageIndices, uint pageIndicesLength, int destPageIndex);
    [LibraryImport(Library)] public static partial int FPDFPage_GetRotation(nint page);
    [LibraryImport(Library)] public static partial void FPDFPage_SetRotation(nint page, int rotate);
    [LibraryImport(Library)] public static partial int FPDFPage_InsertObject(nint page, nint pageObject);
    [LibraryImport(Library)] public static partial int FPDFPage_CountObjects(nint page);
    [LibraryImport(Library)] public static partial nint FPDFPage_GetObject(nint page, int index);
    [LibraryImport(Library)] public static partial int FPDFPage_GenerateContent(nint page);
    [LibraryImport(Library)] public static partial int FPDFPageObj_GetType(nint pageObject);
    [LibraryImport(Library)] public static partial int FPDFPageObj_SetIsActive(nint pageObject, int active);
    [LibraryImport(Library)] public static partial int FPDFPageObj_GetBounds(nint pageObject, float* left, float* bottom, float* right, float* top);
    [LibraryImport(Library)] public static partial int FPDFPageObj_GetMatrix(nint pageObject, FsMatrix* matrix);
    [LibraryImport(Library)] public static partial void FPDFPageObj_Transform(nint pageObject, double a, double b, double c, double d, double e, double f);
    [LibraryImport(Library)] public static partial int FPDFPageObj_SetFillColor(nint pageObject, uint r, uint g, uint b, uint a);
    [LibraryImport(Library)] public static partial nint FPDFPageObj_CreateNewRect(float x, float y, float width, float height);
    [LibraryImport(Library)] public static partial int FPDFPath_SetDrawMode(nint path, int fillMode, int stroke);
    [LibraryImport(Library)] public static partial nint FPDFPageObj_NewTextObj(nint document, byte* font, float fontSize);
    [LibraryImport(Library)] public static partial nint FPDFPageObj_CreateTextObj(nint document, nint font, float fontSize);
    [LibraryImport(Library)] public static partial int FPDFText_SetText(nint textObject, ushort* text);
    [LibraryImport(Library)] public static partial nint FPDFText_LoadFont(nint document, byte* data, uint size, int fontType, int cid);
    [LibraryImport(Library)] public static partial void FPDFFont_Close(nint font);
    [LibraryImport(Library)] public static partial nint FPDFTextObj_GetFont(nint textObject);
    [LibraryImport(Library)] public static partial int FPDFTextObj_GetFontSize(nint textObject, float* size);
    [LibraryImport(Library)] public static partial int FPDFTextObj_GetTextRenderMode(nint textObject);
    [LibraryImport(Library)] public static partial uint FPDFTextObj_GetText(nint textObject, nint textPage, ushort* buffer, uint length);
    [LibraryImport(Library)] public static partial nuint FPDFFont_GetBaseFontName(nint font, byte* buffer, nuint length);
    [LibraryImport(Library)] public static partial int FPDFFont_GetIsEmbedded(nint font);
    [LibraryImport(Library)] public static partial int FPDFFont_GetGlyphWidth(nint font, uint glyph, float fontSize, float* width);
    [LibraryImport(Library)] public static partial nint FPDFFont_GetGlyphPath(nint font, uint glyph, float fontSize);
    [LibraryImport(Library)] public static partial int FPDFGlyphPath_CountGlyphSegments(nint glyphPath);
    [LibraryImport(Library)] public static partial nint FPDFGlyphPath_GetGlyphPathSegment(nint glyphPath, int index);
    [LibraryImport(Library)] public static partial int FPDFPathSegment_GetPoint(nint segment, float* x, float* y);
    [LibraryImport(Library)] public static partial int FPDFPathSegment_GetType(nint segment);

    // fpdf_text.h
    [LibraryImport(Library)] public static partial nint FPDFText_LoadPage(nint page);
    [LibraryImport(Library)] public static partial void FPDFText_ClosePage(nint textPage);

    // fpdf_ppo.h
    [LibraryImport(Library)] public static partial int FPDF_ImportPagesByIndex(nint destDocument, nint sourceDocument, int* pageIndices, uint length, int index);

    // fpdf_signature.h
    [LibraryImport(Library)] public static partial int FPDF_GetSignatureCount(nint document);
}
