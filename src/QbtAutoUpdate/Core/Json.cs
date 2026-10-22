using System.Globalization;
using System.Text;
using System.Text.Json;

namespace QbtAutoUpdate.Core;

/// <summary>
/// Field-by-field JSON reading: one bad value falls back to its default (with a warning)
/// instead of discarding the whole file. No reflection, so it stays trim-safe.
/// </summary>
internal sealed class JsonFields(JsonElement element, string fileLabel, List<string> warnings)
{
    public static JsonDocument? TryLoad(string path, string fileLabel, List<string> warnings)
    {
        if (!File.Exists(path))
            return null;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > 1024 * 1024)
            {
                warnings.Add($"{fileLabel} is larger than 1 MB and was ignored.");
                return null;
            }
            JsonDocument document = JsonDocument.Parse(stream, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
                MaxDepth = 16,
            });
            if (document.RootElement.ValueKind == JsonValueKind.Object)
                return document;
            document.Dispose();
            warnings.Add($"{fileLabel} does not contain a JSON object; defaults are used.");
        }
        catch (JsonException ex)
        {
            warnings.Add($"{fileLabel} is not valid JSON ({ex.Message}); defaults are used.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"{fileLabel} could not be read ({ex.Message}); defaults are used.");
        }
        return null;
    }

    public JsonFields? Object(string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Object
            ? new JsonFields(value, $"{fileLabel}:{name}", warnings)
            : null;

    public bool Bool(string name, bool fallback)
    {
        if (!element.TryGetProperty(name, out JsonElement value))
            return fallback;
        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            return value.GetBoolean();
        Warn(name, "a boolean", fallback ? "true" : "false");
        return fallback;
    }

    public int Int(string name, int fallback, int min, int max)
    {
        if (!element.TryGetProperty(name, out JsonElement value))
            return fallback;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number) && number >= min && number <= max)
            return number;
        Warn(name, $"an integer between {min} and {max}", fallback.ToString(CultureInfo.InvariantCulture));
        return fallback;
    }

    public string? String(string name)
    {
        if (!element.TryGetProperty(name, out JsonElement value) || value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind == JsonValueKind.String)
            return value.GetString();
        Warn(name, "a string", "none");
        return null;
    }

    public T Choice<T>(string name, T fallback, params (string Text, T Value)[] choices)
    {
        string? text = String(name);
        if (text is null)
            return fallback;
        foreach ((string Text, T Value) choice in choices)
        {
            if (choice.Text == text)
                return choice.Value;
        }
        Warn(name, "one of " + string.Join(", ", choices.Select(c => '"' + c.Text + '"')), choices.First(c => Equals(c.Value, fallback)).Text);
        return fallback;
    }

    public QbtVersion? Version(string name)
    {
        string? text = String(name);
        if (text is null)
            return null;
        if (QbtVersion.TryParse(text, out QbtVersion version))
            return version;
        Warn(name, "a version such as \"5.3.0\"", "none");
        return null;
    }

    public DateTimeOffset? Date(string name)
    {
        string? text = String(name);
        if (text is null)
            return null;
        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset date))
            return date;
        Warn(name, "an ISO 8601 date", "none");
        return null;
    }

    private void Warn(string name, string expected, string fallback) =>
        warnings.Add($"{fileLabel}: \"{name}\" must be {expected}; using {fallback}.");
}

internal static class JsonFiles
{
    /// <summary>Writes through a temporary file so a crash never leaves a half-written file behind.</summary>
    public static void WriteAtomically(string path, Action<Utf8JsonWriter> write)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            write(writer);
        }
        File.Move(temp, path, overwrite: true);
    }

    public static string ToIso(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);

    public static string ToText(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            write(writer);
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
