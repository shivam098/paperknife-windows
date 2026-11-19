using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PaperKnife.Core.Tests.Conformance;

/// <summary>
/// The conformance corpus in conformance/ at the root of the repository: every case's documents
/// and the observations of this engine, which PaperKnife's other apps must reproduce. The corpus
/// is regenerated from <see cref="CorpusCases"/> with <c>PAPERKNIFE_WRITE_CORPUS=1</c>; otherwise
/// these tests check that it is up to date and that the stored steps give the stored observations.
/// </summary>
[Collection(nameof(ConformanceTests))]
[CollectionDefinition(nameof(ConformanceTests), DisableParallelization = true)]
public sealed class ConformanceTests
{
    /// <summary>Numbers may differ this much between platforms (they are rounded to 0.01).</summary>
    public const double Tolerance = 0.011;

    private static readonly JsonSerializerOptions Options = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    [Fact]
    public void Corpus_IsUpToDate()
    {
        var root = CorpusDirectory();
        var generated = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        var index = new StringBuilder("# Conformance cases\n\nGenerated from `tests/PaperKnife.Core.Tests/Conformance/CorpusCases*.cs` in PaperKnife for Windows. See [README.md](README.md).\n\n| Case | What it checks | Guards against |\n|---|---|---|\n");
        foreach (var corpusCase in CorpusCases.All.OrderBy(c => c.Id, StringComparer.Ordinal))
        {
            Assert.Matches("^[a-z0-9]+(-[a-z0-9]+)*$", corpusCase.Id);
            Assert.False(generated.ContainsKey($"cases/{corpusCase.Id}.json"), $"Two cases are named {corpusCase.Id}.");
            var attachments = corpusCase.Attachments.ToDictionary(a => a.Key, a => a.Value());
            var (json, files) = CorpusRecorder.Record(corpusCase, corpusCase.Document(), attachments);
            generated[$"cases/{corpusCase.Id}.json"] = Encoding.UTF8.GetBytes(Write(json));
            foreach (var (name, bytes) in files)
            {
                generated[name] = bytes;
            }

            index.Append(CultureInfo.InvariantCulture, $"| [`{corpusCase.Id}`](cases/{corpusCase.Id}.json) | {Cell(corpusCase.Title)} | {Cell(corpusCase.Guards)} |\n");
        }

        var indexPath = Path.Combine(root, "INDEX.md");
        if (Environment.GetEnvironmentVariable("PAPERKNIFE_WRITE_CORPUS") == "1")
        {
            foreach (var folder in Folders.Where(f => Directory.Exists(Path.Combine(root, f))))
            {
                Directory.Delete(Path.Combine(root, folder), recursive: true);
            }

            foreach (var (name, bytes) in generated)
            {
                var path = Path.Combine(root, name);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, bytes);
            }

            File.WriteAllText(indexPath, index.ToString());
            return;
        }

        Assert.True(Directory.Exists(Path.Combine(root, "cases")), "The corpus hasn't been written; run the tests with PAPERKNIFE_WRITE_CORPUS=1.");
        var stored = Folders.Where(f => Directory.Exists(Path.Combine(root, f)))
            .SelectMany(f => Directory.GetFiles(Path.Combine(root, f)).Select(p => $"{f}/{Path.GetFileName(p)}"))
            .Order(StringComparer.Ordinal).ToList();
        Assert.Equal(generated.Keys.ToList(), stored);
        foreach (var (name, bytes) in generated)
        {
            var path = Path.Combine(root, name);
            if (name.EndsWith(".json", StringComparison.Ordinal))
            {
                AssertSame(JsonNode.Parse(File.ReadAllText(path))!, JsonNode.Parse(bytes)!, name);
            }
            else
            {
                Assert.True(File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes), $"{name} differs from what the cases generate.");
            }
        }

        Assert.Equal(Normalize(index.ToString()), Normalize(File.ReadAllText(indexPath)));
    }

    /// <summary>The stored cases, run from their stored documents and steps, as the other apps run them.</summary>
    public static TheoryData<string> StoredCases() =>
        Directory.Exists(Path.Combine(CorpusDirectory(), "cases"))
            ? new TheoryData<string>(Directory.GetFiles(Path.Combine(CorpusDirectory(), "cases"), "*.json").Select(p => Path.GetFileNameWithoutExtension(p)!).Order(StringComparer.Ordinal))
            : new TheoryData<string>("(no corpus)");

    [Theory]
    [MemberData(nameof(StoredCases))]
    public void StoredCase_GivesTheStoredObservations(string id)
    {
        var root = CorpusDirectory();
        Assert.SkipWhen(id == "(no corpus)", "The corpus hasn't been written.");
        var stored = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "cases", $"{id}.json")))!.AsObject();
        Assert.Equal(CorpusRecorder.Format, (int)stored["format"]!);

        PdfSnapshot Load(string name) => PdfSnapshot.FromBytes(File.ReadAllBytes(Path.Combine(root, name)));
        var attachments = (stored["attachments"]?.AsObject() ?? []).ToDictionary(a => a.Key, a => Load((string)a.Value!));
        var steps = stored["steps"]!.AsArray().Select(s => Step(s!.AsObject())).ToList();
        var document = Load((string)stored["document"]!);
        var corpusCase = new CorpusCase(id, (string)stored["title"]!, (string)stored["guards"]!, () => document, steps)
        {
            Password = (string?)stored["password"],
        };

        var (observed, _) = CorpusRecorder.Record(corpusCase, document, attachments);

        AssertSame(stored, observed, $"cases/{id}.json");
    }

    /// <summary>A step as the corpus stores it.</summary>
    private static CorpusStep Step(JsonObject json)
    {
        CorpusStep step = (string)json["action"]! switch
        {
            "replaceText" => new ReplaceTextStep((int)json["page"]!, (string)json["run"]!, (string)json["text"]!, (bool?)json["contentEditing"] ?? true),
            "replaceObject" => new ReplaceObjectStep((int)json["page"]!, (int)json["object"]!, (string)json["text"]!),
            "rotatePage" => new RotatePageStep((int)json["page"]!, (int)json["quarterTurns"]!),
            "deletePage" => new DeletePageStep((int)json["page"]!),
            "movePage" => new MovePageStep((int)json["from"]!, (int)json["to"]!),
            "appendDocument" => new AppendDocumentStep((string)json["document"]!, (string?)json["password"]),
            "undo" => new UndoStep(),
            "redo" => new RedoStep(),
            var other => throw new InvalidDataException($"Unknown action {other}."),
        };
        return step with { MaxMilliseconds = (int?)json["maxMilliseconds"] };
    }

    /// <summary>Fails with the first place two corpus documents differ: numbers may differ by <see cref="Tolerance"/>.</summary>
    private static void AssertSame(JsonNode expected, JsonNode actual, string file)
    {
        if (Difference(expected, actual, "$") is { } difference)
        {
            Assert.Fail($"{file}: {difference}");
        }
    }

    private static string? Difference(JsonNode? expected, JsonNode? actual, string path)
    {
        switch (expected, actual)
        {
            case (null, null):
                return null;
            case (JsonObject a, JsonObject b):
                foreach (var key in a.Select(p => p.Key).Union(b.Select(p => p.Key)))
                {
                    if (!a.ContainsKey(key) || !b.ContainsKey(key))
                    {
                        return $"{path}.{key} is {(a.ContainsKey(key) ? "missing" : "unexpected")}";
                    }

                    if (Difference(a[key], b[key], $"{path}.{key}") is { } difference)
                    {
                        return difference;
                    }
                }

                return null;
            case (JsonArray a, JsonArray b):
                if (a.Count != b.Count)
                {
                    return $"{path} has {b.Count} items, not {a.Count}: {Shorten(b)} instead of {Shorten(a)}";
                }

                for (var i = 0; i < a.Count; i++)
                {
                    if (Difference(a[i], b[i], $"{path}[{i}]") is { } difference)
                    {
                        return difference;
                    }
                }

                return null;
            case (JsonValue a, JsonValue b) when a.GetValueKind() == JsonValueKind.Number && b.GetValueKind() == JsonValueKind.Number:
                return Math.Abs(Number(a) - Number(b)) <= Tolerance ? null : $"{path} is {b.ToJsonString()}, not {a.ToJsonString()}";
            default:
                return JsonNode.DeepEquals(expected, actual) ? null : $"{path} is {Shorten(actual)}, not {Shorten(expected)}";
        }
    }

    private static double Number(JsonValue value) => double.Parse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture);

    private static string Shorten(JsonNode? node)
    {
        var text = node?.ToJsonString(Options) ?? "null";
        return text.Length > 300 ? text[..300] + "…" : text;
    }

    /// <summary>
    /// JSON as the corpus writes it: objects one key per line, arrays of plain values on one line,
    /// two-space indents and "\n" line ends, so files read and diff well.
    /// </summary>
    internal static string Write(JsonNode node)
    {
        var text = new StringBuilder();
        Append(node, 0);
        return text.Append('\n').ToString();

        void Append(JsonNode? value, int depth)
        {
            switch (value)
            {
                case JsonObject obj when obj.Count > 0:
                    text.Append("{\n");
                    var i = 0;
                    foreach (var (key, item) in obj)
                    {
                        text.Append(' ', (depth + 1) * 2).Append(JsonValue.Create(key).ToJsonString(Options)).Append(": ");
                        Append(item, depth + 1);
                        text.Append(++i < obj.Count ? ",\n" : "\n");
                    }

                    text.Append(' ', depth * 2).Append('}');
                    break;
                case JsonArray array when array.Count > 0 && array.Any(v => v is JsonObject or JsonArray):
                    text.Append("[\n");
                    for (var j = 0; j < array.Count; j++)
                    {
                        text.Append(' ', (depth + 1) * 2);
                        Append(array[j], depth + 1);
                        text.Append(j + 1 < array.Count ? ",\n" : "\n");
                    }

                    text.Append(' ', depth * 2).Append(']');
                    break;
                case JsonArray array:
                    text.Append('[').AppendJoin(", ", array.Select(v => v?.ToJsonString(Options) ?? "null")).Append(']');
                    break;
                default:
                    text.Append(value?.ToJsonString(Options) ?? "null");
                    break;
            }
        }
    }

    private static string Cell(string text) => text.Replace("|", "\\|", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);

    private static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

    /// <summary>The generated folders of the corpus, which writing it replaces.</summary>
    private static readonly string[] Folders = ["cases", "contents", "documents"];

    /// <summary>conformance/ at the root of the repository.</summary>
    private static string CorpusDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "PaperKnife.slnx")))
            {
                return Path.Combine(directory.FullName, "conformance");
            }
        }

        throw new InvalidOperationException("The repository root wasn't found.");
    }
}
