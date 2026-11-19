using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using PaperKnife.Core.Content;
using PaperKnife.Core.Editing;
using PaperKnife.Core.Syntax;

namespace PaperKnife.Core.Tests.Conformance;

/// <summary>A step of a conformance case: something done to the document, whose outcome is recorded.</summary>
internal abstract record CorpusStep
{
    /// <summary>The longest the step may take, for steps that guard against slow handling of crafted input.</summary>
    public int? MaxMilliseconds { get; init; }
}

/// <summary>Replaces the text of the run whose text is <paramref name="Run"/> (as PaperKnife reads it) on a page.</summary>
internal sealed record ReplaceTextStep(int Page, string Run, string Text, bool ContentEditing = true) : CorpusStep;

/// <summary>Replaces the text of whatever page object has the given index, which needn't be text.</summary>
internal sealed record ReplaceObjectStep(int Page, int ObjectIndex, string Text) : CorpusStep;

internal sealed record RotatePageStep(int Page, int QuarterTurns) : CorpusStep;

internal sealed record DeletePageStep(int Page) : CorpusStep;

internal sealed record MovePageStep(int From, int To) : CorpusStep;

/// <summary>Appends the pages of another document of the case, by name.</summary>
internal sealed record AppendDocumentStep(string Document, string? Password = null) : CorpusStep;

internal sealed record UndoStep : CorpusStep;

internal sealed record RedoStep : CorpusStep;

/// <summary>
/// A case of the conformance corpus: a document, steps done to it, and why. Every PaperKnife app
/// runs the same cases and must observe what the Windows app observes (see conformance/README.md).
/// </summary>
internal sealed record CorpusCase(string Id, string Title, string Guards, Func<PdfSnapshot> Document, IReadOnlyList<CorpusStep> Steps)
{
    public string? Password { get; init; }

    /// <summary>More documents the steps use, by name, such as one whose pages are appended.</summary>
    public IReadOnlyDictionary<string, Func<PdfSnapshot>> Attachments { get; init; } = new Dictionary<string, Func<PdfSnapshot>>();
}

/// <summary>
/// Runs a conformance case with the Windows engine and records what it observes, in the format the
/// corpus stores (see conformance/README.md). Run again on the stored documents, the same steps
/// must give the stored observations, so the corpus always describes this engine.
/// </summary>
internal static class CorpusRecorder
{
    public const int Format = 1;

    /// <summary>
    /// The case as the corpus stores it, with the documents it uses and the decoded page contents
    /// worth keeping, by file name.
    /// </summary>
    public static (JsonObject Case, Dictionary<string, byte[]> Files) Record(
        CorpusCase corpusCase, PdfSnapshot document, IReadOnlyDictionary<string, PdfSnapshot> attachments)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var attachmentFiles = new JsonObject();
        foreach (var (name, snapshot) in attachments.OrderBy(a => a.Key, StringComparer.Ordinal))
        {
            attachmentFiles[name] = Keep(files, "documents", ".pdf", snapshot.Span.ToArray());
        }

        var result = new JsonObject
        {
            ["format"] = Format,
            ["id"] = corpusCase.Id,
            ["title"] = corpusCase.Title,
            ["guards"] = corpusCase.Guards,
            ["document"] = Keep(files, "documents", ".pdf", document.Span.ToArray()),
        };
        if (corpusCase.Password is not null)
        {
            result["password"] = corpusCase.Password;
        }

        if (attachmentFiles.Count > 0)
        {
            result["attachments"] = attachmentFiles;
        }

        using var session = DocumentSession.FromSnapshot(document, corpusCase.Password);
        var history = new List<byte[]> { document.Span.ToArray() };
        result["initial"] = State(session, history, 0, files);

        var steps = new JsonArray();
        for (var i = 0; i < corpusCase.Steps.Count; i++)
        {
            var step = corpusCase.Steps[i];
            var json = Describe(step, session);
            var watch = Stopwatch.StartNew();
            var outcome = Perform(step, session, attachments);
            watch.Stop();
            if (step.MaxMilliseconds is { } limit && watch.ElapsedMilliseconds > limit)
            {
                throw new InvalidOperationException($"{corpusCase.Id} step {i + 1} took {watch.ElapsedMilliseconds} ms, more than {limit} ms.");
            }

            json["expect"] = new JsonObject
            {
                ["result"] = outcome,
                ["state"] = State(session, history, i + 1, files),
            };
            steps.Add(json);
        }

        result["steps"] = steps;
        return (result, files);
    }

    /// <summary>The step as the corpus stores it, with the run it names as PaperKnife reads it.</summary>
    private static JsonObject Describe(CorpusStep step, DocumentSession session)
    {
        var json = step switch
        {
            ReplaceTextStep s => new JsonObject
            {
                ["action"] = "replaceText",
                ["page"] = s.Page,
                ["run"] = FindRun(session, s.Page, s.Run).Text,
                ["text"] = s.Text,
                ["contentEditing"] = s.ContentEditing,
            },
            ReplaceObjectStep s => new JsonObject { ["action"] = "replaceObject", ["page"] = s.Page, ["object"] = s.ObjectIndex, ["text"] = s.Text },
            RotatePageStep s => new JsonObject { ["action"] = "rotatePage", ["page"] = s.Page, ["quarterTurns"] = s.QuarterTurns },
            DeletePageStep s => new JsonObject { ["action"] = "deletePage", ["page"] = s.Page },
            MovePageStep s => new JsonObject { ["action"] = "movePage", ["from"] = s.From, ["to"] = s.To },
            AppendDocumentStep s => s.Password is null
                ? new JsonObject { ["action"] = "appendDocument", ["document"] = s.Document }
                : new JsonObject { ["action"] = "appendDocument", ["document"] = s.Document, ["password"] = s.Password },
            UndoStep => new JsonObject { ["action"] = "undo" },
            RedoStep => new JsonObject { ["action"] = "redo" },
            _ => throw new ArgumentException($"Unknown step {step}.", nameof(step)),
        };
        if (step.MaxMilliseconds is { } limit)
        {
            json["maxMilliseconds"] = limit;
        }

        return json;
    }

    private static JsonObject Perform(CorpusStep step, DocumentSession session, IReadOnlyDictionary<string, PdfSnapshot> attachments)
    {
        switch (step)
        {
            case ReplaceTextStep s:
                session.UseContentEditing = s.ContentEditing;
                try
                {
                    return Result(session.ReplaceText(s.Page, FindRun(session, s.Page, s.Run).ObjectIndex, s.Text));
                }
                finally
                {
                    session.UseContentEditing = true;
                }

            case ReplaceObjectStep s:
                return Result(session.ReplaceText(s.Page, s.ObjectIndex, s.Text));
            case RotatePageStep s:
                return Result(session.RotatePage(s.Page, s.QuarterTurns));
            case DeletePageStep s:
                return Result(session.DeletePage(s.Page));
            case MovePageStep s:
                return Result(session.MovePage(s.From, s.To));
            case AppendDocumentStep s:
                return Result(session.AppendDocument(attachments[s.Document], s.Password));
            case UndoStep:
                return new JsonObject { ["done"] = session.Undo() };
            case RedoStep:
                return new JsonObject { ["done"] = session.Redo() };
            default:
                throw new ArgumentException($"Unknown step {step}.", nameof(step));
        }
    }

    /// <summary>The run with exactly this text on a page, or with text equivalent to it when there is no exact one.</summary>
    private static PdfTextRun FindRun(DocumentSession session, int page, string text)
    {
        var runs = session.GetTextRuns(page);
        var exact = runs.Where(r => r.Text == text).ToList();
        var matches = exact.Count > 0 ? exact : runs.Where(r => TextEquivalence.AreEquivalent(r.Text, text)).ToList();
        return matches.Count == 1
            ? matches[0]
            : throw new InvalidOperationException($"{matches.Count} runs on page {page + 1} read “{text}”; a step must name exactly one.");
    }

    private static JsonObject Result(EditResult result) => new()
    {
        ["status"] = result.Status.ToString(),
        ["rejection"] = result.Rejection.ToString(),
        ["message"] = result.Message,
        ["details"] = Strings(result.Details),
        ["warnings"] = Strings(result.Warnings),
        ["unsupportedCharacters"] = Strings(result.UnsupportedCharacters),
        ["run"] = result.TextRun is { } run ? Run(run) : null,
    };

    /// <summary>
    /// The document as a reader sees it after a step: its pages, their text runs, decoded content
    /// and fonts, the undo state, and whether its bytes are those of an earlier state or extend the
    /// previous one.
    /// </summary>
    private static JsonObject State(DocumentSession session, List<byte[]> history, int stepIndex, Dictionary<string, byte[]> files)
    {
        var bytes = session.CurrentSnapshot.Span.ToArray();
        var state = new JsonObject
        {
            ["pageCount"] = session.PageCount,
            ["modified"] = session.IsModified,
            ["canUndo"] = session.CanUndo,
            ["canRedo"] = session.CanRedo,
        };
        if (stepIndex > 0)
        {
            var same = history.FindIndex(earlier => earlier.AsSpan().SequenceEqual(bytes));
            state["sameAs"] = same >= 0 ? same : null;
            state["appended"] = bytes.AsSpan().StartsWith(history[^1]);
            history.Add(bytes);
        }

        var file = PdfFile.TryOpen(bytes, out _) is { IsEncrypted: false } opened ? opened : null;
        var nodes = file?.GetPages();
        var pages = new JsonArray();
        for (var page = 0; page < session.PageCount; page++)
        {
            var size = session.GetPageSize(page);
            var json = new JsonObject
            {
                ["width"] = Round(size.Width),
                ["height"] = Round(size.Height),
                ["runs"] = new JsonArray([.. session.GetTextRuns(page).Select(r => (JsonNode)Run(r))]),
                ["blockers"] = Strings(session.GetTextEditBlockers(page)),
            };

            // Decoded content as PaperKnife's own reader gives it, or null when it doesn't read the
            // page; kept in contents/ too, for comparing by eye when another app writes different content.
            var content = file is not null && nodes is not null && page < nodes.Count ? ContentPage.TryLoad(file, nodes, page)?.Content : null;
            json["content"] = content is null ? null : Convert.ToHexStringLower(SHA256.HashData(content));
            if (content is not null)
            {
                Keep(files, "contents", ".content", content);
            }

            json["fonts"] = file is not null && nodes is not null && page < nodes.Count ? Fonts(file, nodes[page]) : null;
            pages.Add(json);
        }

        state["pages"] = pages;
        return state;
    }

    private static JsonObject Run(PdfTextRun run) => new()
    {
        ["text"] = run.Text,
        ["object"] = run.ObjectIndex,
        ["objects"] = run.ObjectCount,
        ["font"] = run.FontName,
        ["size"] = Round(run.FontSize),
        ["embedded"] = run.IsFontEmbedded,
        ["subset"] = run.IsFontSubset,
        ["invisible"] = run.IsInvisible,
        ["bounds"] = new JsonArray(Round(run.Bounds.Left), Round(run.Bounds.Bottom), Round(run.Bounds.Right), Round(run.Bounds.Top)),
    };

    /// <summary>The page's fonts in a form that doesn't depend on how a writer ordered or compressed them.</summary>
    private static JsonArray Fonts(PdfFile file, PdfPageNode page)
    {
        var fonts = new JsonArray();
        foreach (var (name, value) in (file.ResolveDict(page.Resources?["Font"])?.Entries ?? []).OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            if (file.ResolveDict(value) is not { } font)
            {
                fonts.Add(new JsonObject { ["name"] = name });
                continue;
            }

            var descriptor = file.ResolveDict(font["FontDescriptor"]);
            var key = new[] { "FontFile", "FontFile2", "FontFile3" }.FirstOrDefault(k => descriptor?[k] is not null);
            var program = key is null ? null : file.Resolve(descriptor![key]) as CosStream;
            fonts.Add(new JsonObject
            {
                ["name"] = name,
                ["subtype"] = file.ResolveName(font["Subtype"]),
                ["baseFont"] = file.ResolveName(font["BaseFont"]),
                ["firstChar"] = Number(file.ResolveNumber(font["FirstChar"])),
                ["lastChar"] = Number(file.ResolveNumber(font["LastChar"])),
                ["widths"] = file.ResolveArray(font["Widths"]) is { } widths
                    ? new JsonArray([.. widths.Items.Select(w => Number(file.ResolveNumber(w)))])
                    : null,
                ["encoding"] = Encoding(file, font["Encoding"]),
                ["toUnicode"] = font["ToUnicode"] is not null,
                ["flags"] = Number(file.ResolveNumber(descriptor?["Flags"])),
                ["fontFile"] = key,
                ["programLength"] = program is null ? null : file.DecodeStream(program, out _)?.Length,
            });
        }

        return fonts;
    }

    private static JsonNode? Encoding(PdfFile file, CosObject? entry)
    {
        switch (file.Resolve(entry))
        {
            case null:
                return null;
            case CosName name:
                return name.Value;
            case CosDict dict:
                var differences = new JsonObject();
                var next = 0;
                foreach (var item in file.ResolveArray(dict["Differences"])?.Items ?? [])
                {
                    switch (file.Resolve(item))
                    {
                        case CosNumber number:
                            next = (int)number.Value;
                            break;
                        case CosName glyph:
                            differences[(next++).ToString(CultureInfo.InvariantCulture)] = glyph.Value;
                            break;
                    }
                }

                return new JsonObject { ["base"] = file.ResolveName(dict["BaseEncoding"]), ["differences"] = differences };
            default:
                return "(other)";
        }
    }

    /// <summary>Keeps a file under its folder, named by the first 12 hex digits of its SHA-256, so cases share identical files.</summary>
    private static string Keep(Dictionary<string, byte[]> files, string folder, string extension, byte[] bytes)
    {
        var name = $"{folder}/{Convert.ToHexStringLower(SHA256.HashData(bytes))[..12]}{extension}";
        files.TryAdd(name, bytes);
        return name;
    }

    private static JsonArray Strings(IEnumerable<string> values) => new([.. values.Select(v => (JsonNode?)v)]);

    private static double Round(double value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    private static JsonNode? Number(double? value) => value is { } v ? Math.Round(v, 4, MidpointRounding.AwayFromZero) : null;
}
