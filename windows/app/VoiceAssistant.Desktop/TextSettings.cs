using System;
using System.Threading;
using System.Windows;
using System.Windows.Media;

namespace VoiceAssistant.Desktop;

public partial class MainWindow
{
    CancellationTokenSource? textPreview;
    bool resetWordCandidatesDraft;
    void LoadTextPreferences(TextPreferences preferences)
    {
        resetWordCandidatesDraft = false;
        CustomWordsInput.Text = string.Join(Environment.NewLine, preferences.CustomWords);
        AutoLearnWordsInput.IsChecked = preferences.AutoLearnWords;
        FillerWordsInput.Text = string.Join(Environment.NewLine, preferences.FillerWords);
        RemoveFillersInput.IsChecked = preferences.RemoveFillers; PolishDictationInput.IsChecked = preferences.PolishDictation;
    }
    TextPreferences ReadTextPreferences()
    {
        var result = _loadedSettings.Text with { CustomWords = Lines(CustomWordsInput.Text), FillerWords = Lines(FillerWordsInput.Text), RemoveFillers = RemoveFillersInput.IsChecked == true, PolishDictation = PolishDictationInput.IsChecked == true, AutoLearnWords = AutoLearnWordsInput.IsChecked == true, WordCandidates = resetWordCandidatesDraft ? new() : _loadedSettings.Text.WordCandidates };
        result.Validate(); return result;
    }
    async void PreviewDictation_Click(object sender, RoutedEventArgs e)
    {
        textPreview?.Cancel(); var own = new CancellationTokenSource(); textPreview = own;
        try
        {
            var draft = _loadedSettings with { Text = ReadTextPreferences() };
            if (string.IsNullOrWhiteSpace(DictationSampleInput.Text)) throw new ArgumentException("시험할 받아쓰기 문장을 입력하세요.");
            DictationPreviewFeedback.Foreground = Brushes.SlateGray;
            DictationPreviewFeedback.Text = draft.Text.PolishDictation ? UiText.Get("저장된 ") + Brain.NameFor(draft.BrainProvider) + UiText.Get(" 연결로 다듬고 있어요…") : UiText.Get("말버릇을 정리하고 있어요…");
            DictationCancelButton.IsEnabled = true;
            var edit = await DictationProcessor.PrepareAsync(DictationSampleInput.Text, draft, own.Token);
            var result = edit.Result;
            if (textPreview != own) return;
            DictationResultInput.Text = edit.Utterance.Control == DictationControl.Text ? result.Text : edit.Description;
            DictationPreviewFeedback.Text = (edit.Utterance.Control != DictationControl.Text || edit.Utterance.EnterAfter ? edit.Description + " " : result.Notice ?? (result.Polished ? UiText.Get("AI로 다듬었어요. 원문과 비교하세요.") : UiText.Get("말버릇 설정을 적용했어요."))) + UiText.Get(" 다른 앱에 입력하지 않았습니다.");
        }
        catch (OperationCanceledException) { if (textPreview == own) DictationPreviewFeedback.Text = UiText.Get("다듬기 시험을 취소했어요."); }
        catch (Exception ex) { if (textPreview == own) { DictationPreviewFeedback.Text = ex.Message; DictationPreviewFeedback.Foreground = Brushes.Firebrick; } }
        finally { if (textPreview == own) { textPreview = null; DictationCancelButton.IsEnabled = false; } own.Dispose(); }
    }
    void CancelDictationPreview_Click(object sender, RoutedEventArgs e) => textPreview?.Cancel();
    public void CancelTextPreview() => textPreview?.Cancel();
}
