using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace VoiceAssistant.Desktop;

public sealed record VocabularyList(string[] Words, string[] Fillers);
public static class VocabularyFiles
{
    public const int MaxBytes = 512 * 1024;
    const string Format = "Sasarisa.Windows.Vocabulary";
    static string[] Unique(IEnumerable<string> values) => values.Select(x => x.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    static void CheckObject(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in element.EnumerateObject()) { if (!seen.Add(item.Name)) throw new ArgumentException("중복된 단어 목록 항목이 있어요."); CheckObject(item.Value); }
        }
        else if (element.ValueKind == JsonValueKind.Array) foreach (var item in element.EnumerateArray()) CheckObject(item);
    }
    static string[] ReadWords(JsonElement source, string property)
    {
        if (!source.TryGetProperty(property, out var value)) return Array.Empty<string>();
        if (value.ValueKind != JsonValueKind.Array || value.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.String)) throw new ArgumentException("단어와 말버릇 목록에는 문자열만 넣을 수 있어요.");
        return Unique(value.EnumerateArray().Select(x => x.GetString()!));
    }
    public static VocabularyList Parse(string text, bool json)
    {
        if (Encoding.UTF8.GetByteCount(text) > MaxBytes) throw new ArgumentException("단어 파일은 512 KB 이내여야 합니다.");
        VocabularyList result;
        if (!json) result = new(Unique(text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)), Array.Empty<string>());
        else
        {
            try
            {
                using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 16 }); var source = document.RootElement;
                CheckObject(source);
                if (source.ValueKind != JsonValueKind.Object) throw new ArgumentException("단어 목록 JSON 형식을 확인하세요.");
                if (source.TryGetProperty("Format", out var format))
                {
                    if (format.GetString() == Format && source.TryGetProperty("Version", out var version) && version.TryGetInt32(out var number) && number == 1)
                        result = new(ReadWords(source, "Words"), ReadWords(source, "Fillers"));
                    else if (format.GetString() == "Sasarisa.Windows.Settings")
                    { var settings = SettingsTransfer.Parse(text); result = new(settings.Text.CustomWords, settings.Text.FillerWords); }
                    else throw new ArgumentException("지원하지 않는 단어 파일 버전입니다.");
                }
                else if (source.TryGetProperty("Text", out var windows)) result = new(ReadWords(windows, "CustomWords"), ReadWords(windows, "FillerWords"));
                else if (source.TryGetProperty("customWords", out _) || source.TryGetProperty("fillerWords", out _)) result = new(ReadWords(source, "customWords"), ReadWords(source, "fillerWords"));
                else
                {
                    if (source.TryGetProperty("settings", out var handy)) source = handy;
                    if (source.ValueKind != JsonValueKind.Object || !(source.TryGetProperty("custom_words", out _) || source.TryGetProperty("custom_filler_words", out _))) throw new ArgumentException("이 파일에서 지원하는 단어 목록을 찾지 못했어요.");
                    result = new(ReadWords(source, "custom_words"), ReadWords(source, "custom_filler_words"));
                }
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException) { throw new ArgumentException("단어 목록 JSON 형식을 확인하세요."); }
        }
        new TextPreferences { CustomWords = result.Words, FillerWords = result.Fillers }.Validate(); return result;
    }
    public static VocabularyList Read(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > MaxBytes) throw new ArgumentException("단어 파일은 512 KB 이내여야 합니다.");
        using var reader = new StreamReader(file, new UTF8Encoding(false, true));
        return Parse(reader.ReadToEnd(), !Path.GetExtension(path).Equals(".txt", StringComparison.OrdinalIgnoreCase));
    }
    public static TextPreferences Apply(TextPreferences current, VocabularyList imported, bool includeFillers, bool replace)
    {
        var next = current with
        {
            CustomWords = replace ? imported.Words.ToArray() : Unique(current.CustomWords.Concat(imported.Words)),
            FillerWords = !includeFillers ? current.FillerWords.ToArray() : replace ? imported.Fillers.ToArray() : Unique(current.FillerWords.Concat(imported.Fillers)),
            WordCandidates = new()
        };
        next.Validate(); return next;
    }
    public static string Serialize(TextPreferences preferences)
    {
        preferences.Validate(); return JsonSerializer.Serialize(new { Format, Version = 1, Words = preferences.CustomWords, Fillers = preferences.FillerWords }, SettingsStore.Json);
    }
    public static void Write(string path, TextPreferences preferences)
    {
        var text = Serialize(preferences); var full = Path.GetFullPath(path); var temp = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temp, text, new UTF8Encoding(false)); if (File.Exists(full)) File.Replace(temp, full, full + ".previous"); else File.Move(temp, full); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
