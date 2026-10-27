using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace VoiceAssistant.Desktop;

public partial class MainWindow
{
    void ImportVocabulary_Click(object sender, RoutedEventArgs e) => SettingsFileRequested?.Invoke(() =>
    {
        try
        {
            var picker = new OpenFileDialog { Title = "단어 목록 가져오기", Filter = "단어 목록 (*.json;*.txt)|*.json;*.txt", CheckFileExists = true, Multiselect = false };
            if (picker.ShowDialog(this) == true) ReviewVocabulary(VocabularyFiles.Read(picker.FileName));
        }
        catch (Exception ex) { VocabularyFeedback.Text = "가져오지 못했어요. " + ex.Message; }
    });
    void ExportVocabulary_Click(object sender, RoutedEventArgs e) => SettingsFileRequested?.Invoke(() =>
    {
        try
        {
            var preferences = ReadTextPreferences();
            var picker = new SaveFileDialog { Title = "화면의 단어 목록 내보내기", Filter = "단어 목록 (*.json)|*.json", DefaultExt = ".json", AddExtension = true, OverwritePrompt = true, FileName = "자비스-단어-" + DateTime.Now.ToString("yyyyMMdd") + ".json" };
            if (picker.ShowDialog(this) != true) return;
            VocabularyFiles.Write(picker.FileName, preferences); VocabularyFeedback.Text = "단어와 말버릇 목록을 내보냈어요. 학습 후보·음성·연결 정보는 포함하지 않습니다.";
        }
        catch (Exception ex) { VocabularyFeedback.Text = "내보내지 못했어요. " + ex.Message; }
    });
    void RestoreVocabulary_Click(object sender, RoutedEventArgs e) => SettingsFileRequested?.Invoke(() =>
    {
        try
        {
            var path = AppPaths.ConfigFile + ".vocabulary-backup";
            if (!File.Exists(path)) { VocabularyFeedback.Text = "아직 자동 학습으로 단어가 추가된 기록이 없어요."; return; }
            ReviewVocabulary(VocabularyFiles.Read(path), restore: true);
        }
        catch (Exception ex) { VocabularyFeedback.Text = "복원할 단어를 읽지 못했어요. " + ex.Message; }
    });
    void ReviewVocabulary(VocabularyList words, bool restore = false)
    {
        var review = new VocabularyImportWindow(words, ReadTextPreferences(), restore) { Owner = this };
        if (review.ShowDialog() != true || review.Result == null) { VocabularyFeedback.Text = "단어 목록 변경을 취소했어요."; return; }
        ApplyVocabularyDraft(review.Result);
    }
    internal void ApplyVocabularyDraft(TextPreferences preferences)
    {
        LoadTextPreferences(preferences);
        resetWordCandidatesDraft = true;
        VocabularyFeedback.Text = "단어 목록을 화면에 적용했어요. ‘설정 저장’을 누르면 사용하며, 이전 설정은 백업됩니다.";
    }
}

public sealed class VocabularyImportWindow : Window
{
    readonly VocabularyList imported;
    readonly TextPreferences current;
    readonly CheckBox fillers = new() { Content = "말버릇 목록도 가져오기", Margin = new Thickness(0,12,0,8) };
    readonly CheckBox replace = new() { Content = "기존 목록을 가져온 목록으로 바꾸기", Margin = new Thickness(0,0,0,14) };
    readonly TextBox preview = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(14) };
    readonly Button apply = new() { Content = "목록에 적용", Padding = new Thickness(20,9,20,9) };
    public TextPreferences? Result { get; private set; }
    public VocabularyImportWindow(VocabularyList imported, TextPreferences current, bool restore = false)
    {
        this.imported = imported; this.current = current;
        Title = restore ? "자동 학습 직전 단어 복원" : "가져올 단어 확인"; Width = 670; Height = 620; MinWidth = 510; MinHeight = 430;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; Background = (Brush)Application.Current.FindResource("CanvasBrush");
        var root = new DockPanel { Margin = new Thickness(24) }; Content = root;
        var header = new StackPanel(); DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        header.Children.Add(new TextBlock { Text = Title, FontSize = 23, FontWeight = FontWeights.SemiBold });
        header.Children.Add(new TextBlock { Text = "기본으로 기존 단어에 합칩니다. 적용 후 메인 화면에서 설정을 저장하세요.\n인식 중이던 학습 후보는 초기화하며 다른 설정은 그대로 사용합니다.", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.SlateGray, Margin = new Thickness(0,12,0,0) });
        header.Children.Add(fillers); header.Children.Add(replace); replace.IsChecked = restore;
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0,16,0,0) }; DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        footer.Children.Add(new Button { Content = "취소", IsCancel = true, Padding = new Thickness(20,9,20,9), Margin = new Thickness(0,0,10,0), Style = (Style)Application.Current.FindResource("SecondaryButton") });
        apply.Style = (Style)Application.Current.FindResource("ActionButton"); footer.Children.Add(apply); root.Children.Add(preview);
        fillers.Checked += (_,_) => Refresh(); fillers.Unchecked += (_,_) => Refresh(); replace.Checked += (_,_) => Refresh(); replace.Unchecked += (_,_) => Refresh();
        apply.Click += (_,_) => { Result = Prepare(); DialogResult = true; }; Refresh();
    }
    TextPreferences Prepare() => VocabularyFiles.Apply(current, imported, fillers.IsChecked == true, replace.IsChecked == true);
    void Refresh()
    {
        try
        {
            var next = Prepare();
            preview.Text = $"단어 {current.CustomWords.Length} → {next.CustomWords.Length}개 / 말버릇 {current.FillerWords.Length} → {next.FillerWords.Length}개\n\n" + SettingsTransfer.Describe(new() { Text = current }, new() { Text = next });
            apply.IsEnabled = true;
        }
        catch (ArgumentException ex) { preview.Text = ex.Message; apply.IsEnabled = false; }
    }
}
