using System.Buffers.Binary;
using System.Collections.Concurrent;

namespace PaperKnife.Core.Fonts;

/// <summary>
/// The full fonts PaperKnife ships to draw new letters in subset fonts of PDFs made with TeX, the
/// versions TeX distributions embed, all unmodified: the Type 1 Computer Modern fonts of the
/// American Mathematical Society (amsfonts 3.04, SIL Open Font License 1.1), compiled in as
/// resources, and Latin Modern (lm 2.004, GUST Font License) and CM-Super (0.3.4, GNU GPL with a
/// font exception), which ship as separate files in <c>Fonts</c> next to the app. See
/// third_party/amsfonts, third_party/lm and third_party/cm-super.
/// </summary>
internal static class BundledFonts
{
    private const string ResourcePrefix = "PaperKnife.Fonts.";

    private static readonly Lazy<Dictionary<string, Func<byte[]?>>> Sources = new(FindSources);
    private static readonly ConcurrentDictionary<string, Type1Font?> Cache = new(StringComparer.Ordinal);

    /// <summary>The folder of the fonts that ship as separate files.</summary>
    public static string FontsDirectory => Path.Combine(AppContext.BaseDirectory, "Fonts");

    /// <summary>
    /// The full font with this PostScript name (for example "CMBX10", "LMRoman10-Regular" or
    /// "SFRM1000"), or null when PaperKnife doesn't ship it.
    /// </summary>
    public static Type1Font? FindType1(string postScriptName) =>
        Sources.Value.TryGetValue(postScriptName, out var read)
            ? Cache.GetOrAdd(postScriptName, name => read() is { } data && Type1Font.FromPfb(data) is { } font && font.FontName == name ? font : null)
            : null;

    /// <summary>
    /// Every font by the name its program gives itself, which isn't always its file name (Latin
    /// Modern's lmr10.pfb is LMRoman10-Regular). A name from a PDF is only ever looked up here,
    /// never turned into a path.
    /// </summary>
    private static Dictionary<string, Func<byte[]?>> FindSources()
    {
        var sources = new Dictionary<string, Func<byte[]?>>(StringComparer.Ordinal);
        var assembly = typeof(BundledFonts).Assembly;
        foreach (var resource in assembly.GetManifestResourceNames())
        {
            if (resource.StartsWith(ResourcePrefix, StringComparison.Ordinal) && resource.EndsWith(".pfb", StringComparison.Ordinal))
            {
                Add(() => assembly.GetManifestResourceStream(resource));
            }
        }

        if (Directory.Exists(FontsDirectory))
        {
            foreach (var path in Directory.EnumerateFiles(FontsDirectory, "*.pfb", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                Add(() => File.OpenRead(path));
            }
        }

        return sources;

        void Add(Func<Stream?> open)
        {
            if (Read(open, headerOnly: true) is { } header && Type1Font.ReadFontName(header) is { } name)
            {
                sources.TryAdd(name, () => Read(open, headerOnly: false));
            }
        }
    }

    /// <summary>A PFB file, or with <paramref name="headerOnly"/> its first, cleartext part; null when it can't be read.</summary>
    private static byte[]? Read(Func<Stream?> open, bool headerOnly)
    {
        try
        {
            using var stream = open();
            if (stream is null || stream.Length > Type1Font.MaxProgram + 64)
            {
                return null;
            }

            if (!headerOnly)
            {
                var data = new byte[stream.Length];
                stream.ReadExactly(data);
                return data;
            }

            Span<byte> segment = stackalloc byte[6];
            stream.ReadExactly(segment);
            var length = BinaryPrimitives.ReadInt32LittleEndian(segment[2..]);
            if (segment[0] != 0x80 || segment[1] != 1 || length is <= 0 or > Type1Font.MaxCleartext)
            {
                return null;
            }

            var cleartext = new byte[length];
            stream.ReadExactly(cleartext);
            return cleartext;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
