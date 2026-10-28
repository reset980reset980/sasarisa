using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace VoiceAssistant.Desktop;

public sealed partial class PersonalWindow
{
    void AddTransferButtons(StackPanel header)
    {
        var row = new WrapPanel { Margin = new Thickness(0,0,0,10) };
        row.Children.Add(Button("개인 데이터 내보내기", (_,_) => ExportData()));
        row.Children.Add(Button("가져오기", (_,_) => ImportData(false)));
        row.Children.Add(Button("Mac 기억·루틴 가져오기", (_,_) => ImportData(true)));
        row.Children.Add(Button("이전 저장 복원", (_,_) => RestoreData())); header.Children.Add(row);
    }
    void ExportData()
    {
        try
        {
            store.Reload(); if (store.Error != null) throw new InvalidOperationException(store.Error);
            var apps = AppRegistry.Default; apps.Reload();
            var picker = new SaveFileDialog { Title = "기억·루틴·배운 실행 방법 내보내기", Filter = "비서 개인 데이터 (*.json)|*.json", DefaultExt = ".json", AddExtension = true, OverwritePrompt = true, FileName = "자비스-개인데이터-" + DateTime.Now.ToString("yyyyMMdd") + ".json" };
            if (picker.ShowDialog(this) != true) return;
            PersonalDataTransfer.ProtectDestination(picker.FileName, store.StoragePath, apps.StoragePath);
            PersonalDataTransfer.Write(picker.FileName, store.Snapshot(), apps.Items);
            status.Text = "기억·루틴·배운 말을 내보냈어요. 이 파일에는 개인 내용과 연결할 앱 이름·경로가 포함됩니다. 로그인·API 키·대화 기록은 포함하지 않습니다."; status.Foreground = Brushes.DarkGreen;
        }
        catch (Exception ex) { Error(ex); }
    }
    void ImportData(bool mac)
    {
        try
        {
            var picker = new OpenFileDialog { Title = mac ? "Mac의 memory.md·routines.json·learned.json·config.json 선택" : "비서 개인 데이터 가져오기", Filter = mac ? "Mac 개인 데이터|memory.md;routines.json;learned.json;config.json|JSON·Markdown|*.json;*.md" : "비서 개인 데이터 (*.json)|*.json", CheckFileExists = true, Multiselect = mac };
            if (picker.ShowDialog(this) != true) return;
            var source = mac ? MacPersonalTransfer.Read(picker.FileNames) : new PersonalImportSource(PersonalDataTransfer.Read(picker.FileName), Array.Empty<string>());
            var review = new PersonalImportWindow(source, store, AppRegistry.Default) { Owner = this };
            if (review.ShowDialog() != true || !review.Applied) { Refresh(); status.Text = "가져오기를 취소했어요. 저장된 개인 데이터는 그대로예요."; return; }
            previous = review.Before; undoVersion = store.Version; undoStamp = store.StorageStamp; Refresh();
            status.Text = "개인 데이터를 가져왔어요. 이전 파일은 personal.json.backup으로 보관했습니다. ‘마지막 변경 되돌리기’ 또는 ‘이전 저장 복원’을 사용할 수 있어요."; status.Foreground = Brushes.DarkGreen;
        }
        catch (Exception ex) { Error(ex); }
    }
    void RestoreData()
    {
        try
        {
            store.Reload(); var backup = PersonalDataTransfer.ReadBackup(store.BackupPath); var before = store.Snapshot(); var version = store.Version; var stamp = store.StorageStamp;
            var description = "현재 · 기억 " + before.Notes.Count + "개 / 루틴 " + before.Routines.Count + "개 / 배운 말 " + before.Learned.Count + "개\n복원 후 · 기억 " + backup.Notes.Count + "개 / 루틴 " + backup.Routines.Count + "개 / 배운 말 " + backup.Learned.Count + "개\n\n복원할 기억\n" + string.Join("\n", backup.Notes) + "\n\n복원할 루틴\n" + string.Join("\n", backup.Routines.Select(x => x.Key + " · " + string.Join(" / ", x.Value.Select(PersonalDataTransfer.Describe)))) + "\n\n복원할 배운 말\n" + string.Join("\n", backup.Learned.Select(x => x.Key + " · " + string.Join(" / ", x.Value.Actions.Select(PersonalDataTransfer.Describe))));
            var review = new DataRestoreWindow("이전 개인 데이터 복원", description, () => store.ApplySnapshot(backup, version, stamp)) { Owner = this };
            if (review.ShowDialog() != true || !review.Applied) return;
            previous = before; undoVersion = store.Version; undoStamp = store.StorageStamp; Refresh(); status.Text = "검토한 이전 저장으로 복원했어요."; status.Foreground = Brushes.DarkGreen;
        }
        catch (Exception ex) { Error(ex); }
    }
}

public partial class MainWindow
{
    void ExportApps_Click(object sender, RoutedEventArgs e) => SettingsFileRequested?.Invoke(() =>
    {
        try
        {
            var store = AppRegistry.Default; store.Reload(); if (store.Error != null) throw new InvalidOperationException(store.Error);
            var picker = new SaveFileDialog { Title = "등록 앱 목록 내보내기", Filter = "비서 등록 앱 (*.json)|*.json", DefaultExt = ".json", AddExtension = true, OverwritePrompt = true, FileName = "자비스-등록앱-" + DateTime.Now.ToString("yyyyMMdd") + ".json" };
            if (picker.ShowDialog(this) != true) return;
            PersonalDataTransfer.ProtectDestination(picker.FileName, store.StoragePath, Path.Combine(AppPaths.Home, "personal.json")); AppDataTransfer.Write(picker.FileName, store.Items);
            AppRegistrationFeedback.Text = "등록 앱 이름과 경로만 내보냈어요. 실행 파일·로그인·API 키는 포함하지 않습니다."; AppRegistrationFeedback.Foreground = Brushes.DarkGreen;
        }
        catch (Exception ex) { AppRegistrationFeedback.Text = ex.Message; AppRegistrationFeedback.Foreground = Brushes.Firebrick; }
    });
    void ImportApps_Click(object sender, RoutedEventArgs e) => ImportAppsFile(false);
    void ImportMacApps_Click(object sender, RoutedEventArgs e) => ImportAppsFile(true);
    void ImportAppsFile(bool mac) => SettingsFileRequested?.Invoke(() =>
    {
        try
        {
            var picker = new OpenFileDialog { Title = mac ? "Mac config.json의 등록 앱 가져오기" : "등록 앱 목록 가져오기", Filter = "비서 앱 목록 (*.json)|*.json", CheckFileExists = true, Multiselect = false };
            if (picker.ShowDialog(this) != true) return;
            var rows = mac ? AppDataTransfer.MacRows(picker.FileName) : AppDataTransfer.Rows(AppDataTransfer.Read(picker.FileName).Apps);
            var review = new AppImportWindow(rows, AppRegistry.Default) { Owner = this };
            if (review.ShowDialog() != true || !review.Applied) { RefreshRegisteredApps(); AppRegistrationFeedback.Text = "가져오기를 취소했어요. 등록 앱은 그대로예요."; return; }
            RefreshRegisteredApps(); AppRegistrationFeedback.Text = "등록 앱을 가져왔어요. 이전 목록은 apps.json.backup으로 보관했습니다. 앱을 실행하지 않았습니다."; AppRegistrationFeedback.Foreground = Brushes.DarkGreen;
        }
        catch (Exception ex) { AppRegistrationFeedback.Text = ex.Message; AppRegistrationFeedback.Foreground = Brushes.Firebrick; }
    });
    void RestoreApps_Click(object sender, RoutedEventArgs e) => SettingsFileRequested?.Invoke(() =>
    {
        try
        {
            var store = AppRegistry.Default; store.Reload(); var backup = AppDataTransfer.ReadBackup(store.BackupPath); var version = store.Version; var stamp = store.StorageStamp;
            var description = "현재 등록 앱 " + store.Items.Count + "개 → 복원 후 " + backup.Length + "개\n\n복원할 목록\n" + string.Join("\n", backup.Select(x => x.Name + "\n  " + x.Path));
            var review = new DataRestoreWindow("이전 등록 앱 목록 복원", description, () => store.ApplySnapshot(backup, version, stamp)) { Owner = this };
            if (review.ShowDialog() != true || !review.Applied) return;
            RefreshRegisteredApps(); AppRegistrationFeedback.Text = "이전 등록 앱 목록을 복원했어요. 현재 목록도 백업으로 보관했습니다."; AppRegistrationFeedback.Foreground = Brushes.DarkGreen;
        }
        catch (Exception ex) { AppRegistrationFeedback.Text = ex.Message; AppRegistrationFeedback.Foreground = Brushes.Firebrick; }
    });
}
