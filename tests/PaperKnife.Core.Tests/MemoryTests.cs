using System.Globalization;
using System.Text;

namespace PaperKnife.Core.Tests;

[CollectionDefinition(nameof(MemoryTests), DisableParallelization = true)]
public sealed class MemoryTestCollection;

/// <summary>
/// What PaperKnife keeps in memory while pages are viewed. These tests run on their own, so the
/// memory they measure is theirs alone.
/// </summary>
[Collection(nameof(MemoryTests))]
public sealed class MemoryTests
{
    private const long Megabyte = 1024 * 1024;

    // The most codes one bfrange of a character map may cover for PaperKnife to read it.
    private const int RangeSize = 1 << 17;

    [Fact]
    public void ViewingPages_WhoseFontsShareACharacterMap_KeepsOneCopyOfIt()
    {
        // Every page has a font of its own, and all of them share one map of 262,144 codes, which
        // takes about 15 MB once read. Fonts used to keep a copy each: as much again per page viewed.
        var retained = RetainedAfterViewing(Pages(48, sharedMap: true, codes: 2 * RangeSize));

        Assert.True(retained < 48 * Megabyte, $"{retained / Megabyte} MB stayed in memory.");
    }

    [Fact]
    public void ViewingManyPages_WithLargeCharacterMaps_KeepsOnlyRecentPagesFonts()
    {
        // Every page's font has a map of its own, of 32,768 codes: about 2 MB once read. Only the
        // fonts of the last pages viewed need to stay.
        var retained = RetainedAfterViewing(Pages(160, sharedMap: false, codes: RangeSize / 4));

        Assert.True(retained < 96 * Megabyte, $"{retained / Megabyte} MB stayed in memory.");
    }

    /// <summary>The memory that stays in use after every page is viewed, while the document is still open.</summary>
    private static long RetainedAfterViewing(PdfSnapshot pdf)
    {
        using var session = DocumentSession.FromSnapshot(pdf);
        var before = GC.GetTotalMemory(forceFullCollection: true);
        for (var page = 0; page < session.PageCount; page++)
        {
            session.GetTextEditBlockers(page);
        }

        var retained = GC.GetTotalMemory(forceFullCollection: true) - before;
        GC.KeepAlive(session);
        return retained;
    }

    /// <summary>
    /// Pages that each draw a word in a Helvetica font of their own, whose ToUnicode map covers
    /// <paramref name="codes"/> codes: one map for all of them, or one map each.
    /// </summary>
    private static PdfSnapshot Pages(int count, bool sharedMap, int codes)
    {
        var map = RawPdf.Stream(CharacterMap(codes));
        var objects = new List<string> { "<< /Type /Catalog /Pages 2 0 R >>", string.Empty };
        if (sharedMap)
        {
            objects.Add(map);
        }

        var kids = new List<int>();
        for (var i = 0; i < count; i++)
        {
            var page = objects.Count + 1;
            kids.Add(page);
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents {page + 1} 0 R /Resources << /Font << /F1 {page + 2} 0 R >> >> >>");
            objects.Add(RawPdf.Stream("BT /F1 24 Tf 72 700 Td (Hello) Tj ET"));
            objects.Add($"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding /ToUnicode {(sharedMap ? 3 : page + 3)} 0 R >>");
            if (!sharedMap)
            {
                objects.Add(map);
            }
        }

        objects[1] = $"<< /Type /Pages /Kids [{string.Join(' ', kids.Select(k => $"{k} 0 R"))}] /Count {count} >>";
        return RawPdf.WriteWithTable(objects);
    }

    /// <summary>A ToUnicode CMap whose bfranges map <paramref name="codes"/> codes, each to a text of its own.</summary>
    private static string CharacterMap(int codes)
    {
        var ranges = (codes + RangeSize - 1) / RangeSize;
        var map = new StringBuilder("/CIDInit /ProcSet findresource begin 12 dict begin begincmap /CMapName /Large def /CMapType 2 def\n")
            .Append(CultureInfo.InvariantCulture, $"{ranges} beginbfrange\n");
        for (var start = 0; start < codes; start += RangeSize)
        {
            map.Append(CultureInfo.InvariantCulture, $"<{start:X6}> <{Math.Min(codes, start + RangeSize) - 1:X6}> <4E00>\n");
        }

        return map.Append("endbfrange\nendcmap CMapName currentdict /CMap defineresource pop end end").ToString();
    }
}
