using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;

namespace VoiceAssistant.Desktop;

// Like MacButler.Strings.t: Korean UI for ko, English UI for other conversation
// languages. Only explicitly marked UI copy is translated. Never traverse user
// input, saved notes, device names, model IDs, message bodies or command tags.
public static class UiText
{
    static readonly Lazy<Dictionary<string,string>> english = new(Load);
    static string language = "ko";
    static Application? applied;
    public static string Language => language;
    public static bool Korean => language == "ko";
    public static event Action? LanguageChanged;
    internal static IReadOnlyDictionary<string,string> Catalog => english.Value;
    static Dictionary<string,string> Load()
    {
        var result = new Dictionary<string,string>(StringComparer.Ordinal);
        foreach (var file in new[] { "VoiceAssistant.Ui.en.json", "VoiceAssistant.Ui.dynamic.en.json", "VoiceAssistant.Ui.dialogs.en.json" })
        {
            using var source = typeof(UiText).Assembly.GetManifestResourceStream(file) ?? throw new InvalidDataException("UI language catalogue is missing.");
            var values = JsonSerializer.Deserialize<Dictionary<string,string>>(source) ?? throw new InvalidDataException("UI language catalogue is empty.");
            foreach (var pair in values)
            {
                if (result.TryGetValue(pair.Key,out var old) && old != pair.Value) throw new InvalidDataException("Conflicting UI translation: " + pair.Key);
                result[pair.Key] = pair.Value;
            }
        }
        return result;
    }
    internal static string Key(string text) => "Ui." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..24];
    public static string Get(string korean) => !Korean && english.Value.TryGetValue(korean,out var translated) ? translated : korean;
    public static string Format(string korean, params object?[] values)
    {
        var rendered = (object?[])values.Clone();
        for (int i = 0; i < rendered.Length; i++) if (rendered[i] is UiPhrase phrase) rendered[i] = phrase.Render();
        return string.Format(Korean ? CultureInfo.GetCultureInfo("ko-KR") : CultureInfo.GetCultureInfo("en-US"),Get(korean),rendered);
    }
    public static UiPhrase Copy(string korean, params object?[] values) => new(korean,(object?[])values.Clone());
    internal static System.Windows.Forms.ToolStripMenuItem MenuItem(string korean, EventHandler click)
    {
        var item = new System.Windows.Forms.ToolStripMenuItem(Get(korean));
        void Refresh() => item.Text = Get(korean);
        LanguageChanged += Refresh;
        item.Click += click;
        item.Disposed += (_, _) => LanguageChanged -= Refresh;
        return item;
    }
    public static void Ensure() { if (!ReferenceEquals(applied,Application.Current)) Apply(language); }
    public static void Apply(string conversationLanguage)
    {
        _ = ConversationLanguage.Get(conversationLanguage);
        var app = Application.Current;
        if (app != null && !app.Dispatcher.CheckAccess()) { app.Dispatcher.Invoke(() => Apply(conversationLanguage)); return; }
        var changed = language != conversationLanguage; language = conversationLanguage;
        if (app != null)
        {
            foreach (var pair in english.Value) app.Resources[Key(pair.Key)] = Korean ? pair.Key : pair.Value;
            applied = app;
        }
        if (changed) LanguageChanged?.Invoke();
    }
}

public sealed record UiPhrase(string Korean, object?[] Values)
{
    public string Render() => UiText.Format(Korean,Values);
}
