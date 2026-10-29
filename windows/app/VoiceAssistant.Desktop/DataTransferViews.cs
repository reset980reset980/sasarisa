using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace VoiceAssistant.Desktop;

public abstract class DataReviewWindow : Window
{
    protected readonly StackPanel Header = new();
    protected readonly TextBox Details = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(14), BorderBrush = Brushes.LightGray };
    protected readonly TextBlock Feedback = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,10,0,10) };
    internal readonly Button Apply;
    public bool Applied { get; private set; }
    internal string Description => Details.Text;
    protected DataReviewWindow(string title, string subtitle, string applyLabel)
    {
        Title = title; Width = 900; Height = 790; MinWidth = 720; MinHeight = 600; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (Brush)Application.Current.FindResource("CanvasBrush");
        var root = new DockPanel { Margin = new Thickness(24) }; Content = root;
        Header.Children.Add(new TextBlock { Text = title, FontSize = 23, FontWeight = FontWeights.SemiBold });
        Header.Children.Add(new TextBlock { Text = subtitle, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.SlateGray, Margin = new Thickness(0,10,0,15) });
        DockPanel.SetDock(Header, Dock.Top); root.Children.Add(Header);
        var footer = new StackPanel(); footer.Children.Add(Feedback); var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = MakeButton(UiText.Get("취소"), (_,_) => Close()); cancel.IsCancel = true; buttons.Children.Add(cancel);
        Apply = MakeButton(applyLabel, (_,_) =>
        {
            try { CommitSelection(); DialogResult = true; }
            catch (Exception ex) { Feedback.Text = ex.Message; Feedback.Foreground = Brushes.Firebrick; }
        }, true); buttons.Children.Add(Apply); footer.Children.Add(buttons);
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer); root.Children.Add(Details); AutomationProperties.SetName(Details, UiText.Get("가져오거나 복원할 변경 내용"));
    }
    protected static Button MakeButton(string text, RoutedEventHandler click, bool primary = false)
    { var button = new Button { Content = text, Padding = new Thickness(15,8,15,8), Margin = new Thickness(0,0,8,0), Style = (Style)Application.Current.FindResource(primary ? "ActionButton" : "SecondaryButton") }; button.Click += click; return button; }
    protected static CheckBox Choice(string text, bool value = true) => new() { Content = text, IsChecked = value, Margin = new Thickness(0,0,18,8) };
    internal void CommitSelection() { if (Applied) throw new InvalidOperationException("이미 적용한 가져오기예요."); CommitCore(); Applied = true; }
    protected abstract void CommitCore();
}

public sealed class PersonalImportWindow : DataReviewWindow
{
    readonly Personalization store;
    readonly AppRegistry apps;
    readonly PersonalImportSource source;
    readonly int revision, appRevision;
    readonly string stamp, appStamp;
    readonly CheckBox notes, routines, learned, replace;
    readonly Dictionary<string, string> mappings;
    readonly RegisteredApp[] appSnapshot;
    readonly Dictionary<string, ComboBox> mappingControls = new();
    public PersonalState Before { get; }
    internal PersonalImportDraft Draft { get; private set; } = null!;
    public PersonalImportWindow(PersonalImportSource source, Personalization store, AppRegistry apps) : base(UiText.Get("가져올 기억·루틴 확인"), UiText.Get("선택한 항목을 현재 데이터에 합칩니다. 같은 이름은 기본적으로 유지합니다. 적용 전 파일은 백업되며 명령은 실행하지 않습니다."), UiText.Get("확인한 개인 데이터 적용"))
    {
        this.source = new(PersonalDataTransfer.Parse(PersonalDataTransfer.Serialize(source.Package)), source.Notices.ToArray()); this.store = store; this.apps = apps;
        store.Reload(); apps.Reload();
        Before = store.Snapshot(); revision = store.Version; stamp = store.StorageStamp; appRevision = apps.Version; appStamp = apps.StorageStamp; appSnapshot = apps.Items.ToArray();
        mappings = PersonalDataTransfer.SuggestMappings(this.source.Package, appSnapshot);
        var choices = new WrapPanel(); notes = Choice(UiText.Get("기억")); routines = Choice(UiText.Get("루틴")); learned = Choice(UiText.Get("배운 실행 방법")); replace = Choice(UiText.Get("같은 이름의 루틴·배운 말도 교체"), false);
        foreach (var box in new[] { notes, routines, learned, replace }) { choices.Children.Add(box); box.Checked += (_,_) => Refresh(); box.Unchecked += (_,_) => Refresh(); } Header.Children.Add(choices);
        if (this.source.Package.Apps.Length > 0)
        {
            Header.Children.Add(new TextBlock { Text = UiText.Get("이 PC에서 연결할 앱"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0,5,0,8) });
            var links = new StackPanel();
            var options = new[] { new DeviceOption("", UiText.Get("연결할 앱 선택")) }.Concat(CommandParser.Apps.GroupBy(x => x.Value).Select(x => new DeviceOption(x.Key, "기본 앱 · " + x.First().Key)))
                .Concat(appSnapshot.Select(x => new DeviceOption(x.Id, "등록 앱 · " + x.Name))).ToArray();
            foreach (var link in this.source.Package.Apps)
            {
                var row = new Grid { Margin = new Thickness(0,0,0,7) }; row.ColumnDefinitions.Add(new() { Width = new GridLength(260) }); row.ColumnDefinitions.Add(new());
                row.Children.Add(new TextBlock { Text = link.Name + "\n" + link.Hint, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,0,12,0), ToolTip = link.Hint });
                var selection = new ComboBox { ItemsSource = options, DisplayMemberPath = "Label", SelectedValuePath = "Id", MinHeight = 36, SelectedValue = mappings.GetValueOrDefault(link.Id) ?? "" };
                AutomationProperties.SetName(selection, link.Name + "에 연결할 Windows 앱"); Grid.SetColumn(selection, 1); row.Children.Add(selection); links.Children.Add(row);
                mappingControls[link.Id] = selection;
                selection.SelectionChanged += (_,_) => { var value = selection.SelectedValue as string ?? ""; if (value.Length == 0) mappings.Remove(link.Id); else mappings[link.Id] = value; Refresh(); };
            }
            Header.Children.Add(new ScrollViewer { Content = links, MaxHeight = 175, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0,0,0,12) });
            Header.Children.Add(new TextBlock { Text = UiText.Get("목록에 없는 앱은 취소한 뒤 설정 → 실행할 앱에서 먼저 등록하세요. 원본 앱 경로를 자동 실행하거나 등록하지 않습니다."), TextWrapping = TextWrapping.Wrap, Foreground = Brushes.SlateGray, Margin = new Thickness(0,0,0,10) });
        }
        Refresh();
    }
    void Refresh()
    {
        if (notes == null || routines == null || learned == null || replace == null) return;
        Draft = PersonalDataTransfer.Prepare(source.Package, Before, appSnapshot, mappings, new(notes.IsChecked == true, routines.IsChecked == true, learned.IsChecked == true, replace.IsChecked == true));
        Details.Text = (source.Notices.Length > 0 ? "가져오기 안내\n" + string.Join("\n", source.Notices) + "\n\n" : "") + Draft.Description + (Draft.Blocked.Length > 0 ? "\n\n확인이 필요한 항목\n" + string.Join("\n", Draft.Blocked) : "");
        Apply.IsEnabled = store.Error == null && Draft.Blocked.Length == 0 && Draft.Added + Draft.Replaced > 0;
        Feedback.Text = store.Error ?? (Draft.Blocked.Length > 0 ? UiText.Get("연결할 앱을 고르거나 해당 종류를 선택 해제하세요.") : UiText.Get("현재 설정·로그인·듣기 상태는 그대로 유지합니다.")); Feedback.Foreground = store.Error != null || Draft.Blocked.Length > 0 ? Brushes.Firebrick : Brushes.SlateGray;
    }
    internal void SelectOptions(PersonalImportOptions options) { notes.IsChecked = options.Notes; routines.IsChecked = options.Routines; learned.IsChecked = options.Learned; replace.IsChecked = options.ReplaceConflicts; Refresh(); }
    internal void MapApp(string sourceId, string targetId) { if (!mappingControls.TryGetValue(sourceId, out var control)) throw new ArgumentException("등록 앱 연결 항목을 확인하세요."); control.SelectedValue = targetId; Refresh(); }
    protected override void CommitCore()
    {
        Refresh();
        if (!Apply.IsEnabled) throw new InvalidOperationException("가져올 항목과 앱 연결을 확인하세요.");
        if (apps.Version != appRevision || apps.StorageStamp != appStamp) throw new InvalidOperationException("검토하는 동안 등록 앱이 바뀌었어요. 새로 가져와 확인하세요.");
        store.ApplySnapshot(Draft.Next, revision, stamp);
    }
}

public sealed class AppImportWindow : DataReviewWindow
{
    readonly AppRegistry store;
    readonly int revision;
    readonly string stamp;
    readonly CheckBox replace;
    readonly AppImportRow[] rows;
    readonly List<(TextBox Name, TextBox Path, CheckBox Selected)> rowControls = new();
    public RegisteredApp[] Before { get; }
    internal AppImportDraft Draft { get; private set; } = null!;
    public AppImportWindow(IEnumerable<AppImportRow> incoming, AppRegistry store) : base(UiText.Get("가져올 등록 앱 확인"), UiText.Get("이 PC의 실행 파일이나 바로가기를 지정하세요. 기본적으로 같은 이름의 현재 앱을 유지합니다. 적용해도 앱을 실행하지 않습니다."), UiText.Get("확인한 등록 앱 적용"))
    {
        this.store = store; store.Reload(); Before = store.Items.ToArray(); revision = store.Version; stamp = store.StorageStamp;
        rows = incoming.Select(x => new AppImportRow { Name = x.Name, Path = x.Path, Hint = x.Hint, Selected = x.Selected }).ToArray();
        if (rows.Length > 100) throw new ArgumentException("등록 앱은 최대 100개까지 가져올 수 있어요.");
        replace = Choice(UiText.Get("같은 이름의 앱 경로도 교체"), false); Header.Children.Add(replace); replace.Checked += (_,_) => Refresh(); replace.Unchecked += (_,_) => Refresh();
        var list = new StackPanel();
        foreach (var row in rows)
        {
            var block = new StackPanel { Margin = new Thickness(0,0,0,12) }; var selected = Choice(UiText.Get("이 앱 가져오기"), row.Selected); block.Children.Add(selected);
            var name = new TextBox { Text = row.Name, MinHeight = 34, Margin = new Thickness(0,0,0,5) }; AutomationProperties.SetName(name, UiText.Get("가져올 앱 호출 이름")); block.Children.Add(name);
            block.Children.Add(new TextBlock { Text = UiText.Get("원본: ") + row.Hint, Foreground = Brushes.SlateGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,0,0,5) });
            var line = new DockPanel(); var choose = MakeButton(UiText.Get("파일 선택"), (_,_) =>
            {
                var picker = new OpenFileDialog { Title = UiText.Get("이 PC에서 연결할 앱 선택"), Filter = "실행 파일·바로가기|*.exe;*.lnk", CheckFileExists = true, Multiselect = false, DereferenceLinks = false };
                if (picker.ShowDialog(this) == true) { row.Path = picker.FileName; Refresh(); }
            }); DockPanel.SetDock(choose, Dock.Right); line.Children.Add(choose);
            var path = new TextBox { Text = row.Path, MinHeight = 34, Margin = new Thickness(0,0,8,0) }; AutomationProperties.SetName(path, UiText.Get("이 PC의 앱 실행 파일 경로")); line.Children.Add(path); block.Children.Add(line); list.Children.Add(block);
            rowControls.Add((name, path, selected));
            selected.Checked += (_,_) => { row.Selected = true; Refresh(); }; selected.Unchecked += (_,_) => { row.Selected = false; Refresh(); };
            name.TextChanged += (_,_) => { row.Name = name.Text; Refresh(); }; path.TextChanged += (_,_) => { row.Path = path.Text; Refresh(); };
            choose.Click += (_,_) => path.Text = row.Path;
        }
        Header.Children.Add(new ScrollViewer { Content = list, MaxHeight = 255, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0,5,0,12) }); Refresh();
    }
    void Refresh()
    {
        Draft = AppDataTransfer.Prepare(rows, Before, replace.IsChecked == true); Details.Text = Draft.Description + (Draft.Blocked.Length > 0 ? "\n\n확인이 필요한 항목\n" + string.Join("\n", Draft.Blocked) : "");
        Apply.IsEnabled = store.Error == null && Draft.Blocked.Length == 0 && Draft.Changed > 0;
        Feedback.Text = store.Error ?? (Draft.Blocked.Length == 0 ? UiText.Get("현재 목록은 적용 전에 백업합니다. 기억·루틴과 설정은 바꾸지 않습니다.") : UiText.Get("경로·이름을 고치거나 가져오지 않을 앱의 선택을 해제하세요.")); Feedback.Foreground = Draft.Blocked.Length > 0 || store.Error != null ? Brushes.Firebrick : Brushes.SlateGray;
    }
    internal void SetRow(int index, string name, string path, bool selected = true) { rowControls[index].Name.Text = name; rowControls[index].Path.Text = path; rowControls[index].Selected.IsChecked = selected; Refresh(); }
    internal void ReplaceConflicts(bool value) { replace.IsChecked = value; Refresh(); }
    protected override void CommitCore() { Refresh(); if (!Apply.IsEnabled) throw new InvalidOperationException("가져올 앱 이름과 경로를 확인하세요."); store.ApplySnapshot(Draft.Next, revision, stamp); }
}

public sealed class DataRestoreWindow : DataReviewWindow
{
    readonly Action restore;
    public DataRestoreWindow(string title, string description, Action restore) : base(title, UiText.Get("현재 목록을 아래 백업 내용으로 바꿉니다. 현재 파일도 다시 백업하므로 이전 저장 복원으로 되돌릴 수 있어요. 어떤 명령이나 앱도 실행하지 않습니다."), UiText.Get("검토한 백업으로 복원"))
    { this.restore = restore; Details.Text = description; Feedback.Text = UiText.Get("설정·로그인·듣기 상태는 바꾸지 않습니다."); }
    protected override void CommitCore() => restore();
}
