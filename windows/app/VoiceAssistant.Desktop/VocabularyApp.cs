using System;
using System.IO;
using System.Linq;

namespace VoiceAssistant.Desktop;

public partial class App
{
    void LearnVocabulary(string live, string refined)
    {
        if (RemoteExecution.Current != null || !settings.Text.AutoLearnWords) return;
        try
        {
            var update = VocabularyLearning.Observe(settings.Text, live, refined, settings.LearnedWakes.Values.SelectMany(x => x).Append(settings.Name), DateTimeOffset.UtcNow);
            if (update == null) return;
            var next = settings with { Text = update.Preferences };
            SettingsStore.SaveVocabulary(next, update.Added.Length > 0);
            settings = next;
            window.ApplyLearnedVocabulary(update);
            if (update.Added.Length > 0) window.AddMessage("system", $"반복 보정된 단어 {update.Added.Length}개를 학습했어요. 다음 듣기 시작부터 사용합니다. 설정의 단어 목록에서 확인하거나 복원할 수 있어요.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            AppPaths.Log("vocabulary-save", ex.GetType().Name);
            window.AddMessage("system", "단어 학습을 저장하지 못했어요. 기존 단어를 유지합니다. " + ex.Message);
        }
    }
}

public partial class MainWindow
{
    internal void ApplyLearnedVocabulary(VocabularyUpdate update)
    {
        _loadedSettings = _loadedSettings with { Text = update.Preferences };
        // Preserve edits in every other field and words the user is currently changing.
        if (update.Added.Length > 0)
            CustomWordsInput.Text = string.Join(Environment.NewLine, Lines(CustomWordsInput.Text).Concat(update.Added).Distinct(StringComparer.OrdinalIgnoreCase));
    }
}
