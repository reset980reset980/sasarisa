using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace VoiceAssistant.Desktop;

public sealed partial class PersonalWindow : Window
{
    sealed record Entry(string Key, string Label);
    sealed record Step(AssistantAction Action) { public string Label => UiText.Korean ? Personalization.DescribeAction(Action) : PersonalDataTransfer.Describe(Action); }
    readonly Personalization store;
    readonly ComboBox category = new() { DisplayMemberPath = "Label", SelectedValuePath = "Id" };
    readonly ListBox entries = new() { DisplayMemberPath = "Label" }, steps = new() { DisplayMemberPath = "Label", Height = 146 };
    readonly TextBox name = Field(UiText.Get("호출 문장 또는 루틴 이름")), body = Field(UiText.Get("기억 내용 또는 답변"), true), command = Field(UiText.Get("추가할 동작 명령"));
    readonly TextBlock nameLabel = Label(UiText.Get("호출 문장")), bodyLabel = Label(UiText.Get("기억할 내용")), status = new() { TextWrapping = TextWrapping.Wrap, LineHeight = 21 };
    readonly StackPanel actionPanel = new();
    readonly ObservableCollection<Step> draft = new();
    readonly Button save, remove, undo;
    PersonalState view = new();
    PersonalState? previous;
    int revision, undoVersion;
    string undoStamp = "";
    string Kind => (category.SelectedItem as DeviceOption)?.Id ?? "notes";
    public PersonalWindow(Personalization store)
    {
        this.store = store; Title = UiText.Get("기억·루틴·배운 말 관리"); Width = 850; Height = 790; MinWidth = 730; MinHeight = 690;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; Background = (Brush)Application.Current.FindResource("CanvasBrush");
        var root = new DockPanel { Margin = new Thickness(24) }; Content = root;
        var header = new StackPanel { Margin = new Thickness(0,0,0,18) };
        header.Children.Add(new TextBlock { Text = UiText.Get("기억과 실행 방법을 관리하세요"), FontSize = 23, FontWeight = FontWeights.SemiBold });
        header.Children.Add(new TextBlock { Text = UiText.Get("저장을 누르면 수정한 내용을 반영합니다. 마지막 변경을 되돌리거나 이전 저장을 복원할 수 있어요."), Foreground = Brushes.SlateGray, Margin = new Thickness(0,8,0,15), TextWrapping = TextWrapping.Wrap });
        AddTransferButtons(header);
        category.ItemsSource = new[] { new DeviceOption("notes", UiText.Get("기억")), new DeviceOption("routines", UiText.Get("루틴")), new DeviceOption("learned", UiText.Get("배운 실행 방법")) }; AutomationProperties.SetName(category, UiText.Get("관리할 종류")); header.Children.Add(category);
        DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        var footer = new StackPanel { Margin = new Thickness(0,15,0,0) }; footer.Children.Add(status);
        var buttons = new WrapPanel { Margin = new Thickness(0,12,0,0) };
        save = Button(UiText.Get("저장"), (_,_) => Save(), true); remove = Button(UiText.Get("삭제"), (_,_) => Delete()); undo = Button(UiText.Get("마지막 변경 되돌리기"), (_,_) => Undo());
        buttons.Children.Add(Button(UiText.Get("새로고침"), (_,_) => Refresh())); buttons.Children.Add(undo); buttons.Children.Add(remove); buttons.Children.Add(save); footer.Children.Add(buttons);
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var grid = new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(225) }); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) }); grid.ColumnDefinitions.Add(new ColumnDefinition()); root.Children.Add(grid);
        var left = new DockPanel(); var add = Button(UiText.Get("새 항목"), (_,_) => { entries.SelectedItem = null; Fill(); }); DockPanel.SetDock(add, Dock.Top); left.Children.Add(add);
        AutomationProperties.SetName(entries, UiText.Get("저장 항목 목록")); entries.SelectionChanged += (_,_) => Fill(); left.Children.Add(entries); grid.Children.Add(left);
        var editor = new StackPanel(); editor.Children.Add(nameLabel); editor.Children.Add(name); editor.Children.Add(bodyLabel); editor.Children.Add(body);
        actionPanel.Children.Add(Label(UiText.Get("실행할 동작 · 위에서 아래 순서"))); steps.ItemsSource = draft; AutomationProperties.SetName(steps, UiText.Get("실행 동작 목록")); actionPanel.Children.Add(steps);
        var order = new WrapPanel { Margin = new Thickness(0,7,0,0) };
        order.Children.Add(Button(UiText.Get("위로"), (_,_) => Move(-1))); order.Children.Add(Button(UiText.Get("아래로"), (_,_) => Move(1))); order.Children.Add(Button(UiText.Get("동작 빼기"), (_,_) => { if (steps.SelectedItem is Step step) draft.Remove(step); })); actionPanel.Children.Add(order);
        actionPanel.Children.Add(Label(UiText.Get("추가할 명령"))); actionPanel.Children.Add(command); actionPanel.Children.Add(Button(UiText.Get("동작 추가"), (_,_) => AddStep()));
        actionPanel.Children.Add(new TextBlock { Text = UiText.Get("예: 메모장 열어 · 볼륨 30 · 키 ctrl+c · 버튼 저장 · 안녕하세요 라고 써 줘\n최대 8개. 화면 동작은 현재 작업 창을 매번 확인하고 제한 권한으로 실행해요. 암호 입력·창 닫기 키는 학습하지 않아요."), Foreground = Brushes.SlateGray, TextWrapping = TextWrapping.Wrap, LineHeight = 21 }); editor.Children.Add(actionPanel);
        var scroll = new ScrollViewer { Content = editor, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0,0,9,0) }; Grid.SetColumn(scroll, 2); grid.Children.Add(scroll);
        category.SelectionChanged += (_,_) => Refresh(); category.SelectedIndex = 0;
    }
    static TextBox Field(string label, bool multiline = false)
    {
        var input = new TextBox { Style = (Style)Application.Current.FindResource("Field"), TextWrapping = TextWrapping.Wrap, AcceptsReturn = multiline, MinHeight = multiline ? 100 : 44, MaxHeight = multiline ? 200 : 80, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        AutomationProperties.SetName(input, label); return input;
    }
    static TextBlock Label(string text) => new() { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0,12,0,7) };
    static Button Button(string text, RoutedEventHandler handler, bool primary = false)
    { var button = new Button { Content = text, Style = (Style)Application.Current.FindResource(primary ? "ActionButton" : "SecondaryButton"), Padding = new Thickness(12,8,12,8), Margin = new Thickness(0,0,8,8), HorizontalAlignment = HorizontalAlignment.Left }; button.Click += handler; return button; }
    void Refresh(string? selected = null)
    {
        try { store.Reload(); }
        catch (Exception ex) { Error(ex); save.IsEnabled = remove.IsEnabled = undo.IsEnabled = false; return; }
        view = store.Snapshot(); revision = store.Version;
        entries.ItemsSource = Kind switch { "routines" => view.Routines.Keys.Select(x => new Entry(x, x)).ToArray(), "learned" => view.Learned.Keys.Select(x => new Entry(x, x)).ToArray(), _ => view.Notes.Select((x,i) => new Entry(i.ToString(), x.Length > 70 ? x[..70] + "…" : x)).ToArray() };
        entries.SelectedItem = entries.Items.OfType<Entry>().FirstOrDefault(x => x.Key == selected); Fill();
        status.Text = store.Error ?? UiText.Format("저장 항목 {0}개 · 현재 목록을 기준으로 편집합니다.",entries.Items.Count); status.Foreground = store.Error == null ? Brushes.SlateGray : Brushes.Firebrick;
        save.IsEnabled = store.Error == null; undo.IsEnabled = previous != null && undoVersion == store.Version;
    }
    void Fill()
    {
        var selected = entries.SelectedItem as Entry; bool note = Kind == "notes";
        name.Visibility = nameLabel.Visibility = note ? Visibility.Collapsed : Visibility.Visible;
        body.Visibility = bodyLabel.Visibility = Kind == "routines" ? Visibility.Collapsed : Visibility.Visible;
        actionPanel.Visibility = note ? Visibility.Collapsed : Visibility.Visible;
        nameLabel.Text = Kind == "routines" ? UiText.Get("루틴 이름") : UiText.Get("호출 문장"); bodyLabel.Text = note ? UiText.Get("기억할 내용") : UiText.Get("실행 후 답변");
        name.Text = selected?.Key ?? ""; body.Text = ""; command.Text = ""; draft.Clear();
        if (selected != null)
        {
            if (note) body.Text = view.Notes[int.Parse(selected.Key)];
            else if (Kind == "routines") foreach (var action in view.Routines[selected.Key]) draft.Add(new(action));
            else { var plan = view.Learned[selected.Key]; body.Text = plan.Reply; foreach (var action in plan.Actions) draft.Add(new(action)); }
        }
        remove.IsEnabled = selected != null && store.Error == null;
    }
    void AddStep()
    {
        try { var plan = Personalization.ParseStep(command.Text); if (draft.Count + plan.Actions.Count > 8) throw new ArgumentException("동작은 최대 8개입니다."); foreach (var action in plan.Actions) draft.Add(new(action)); command.Clear(); status.Text = UiText.Get("동작을 추가했어요. 저장하면 적용됩니다."); status.Foreground = Brushes.SlateGray; }
        catch (Exception ex) { Error(ex); }
    }
    void Move(int delta) { var index = steps.SelectedIndex; if (index < 0 || index + delta < 0 || index + delta >= draft.Count) return; draft.Move(index, index + delta); steps.SelectedIndex = index + delta; }
    void Change(Action action, string? selected, string message)
    {
        try { var before = store.Snapshot(); action(); previous = before; undoVersion = store.Version; undoStamp = store.StorageStamp; Refresh(selected); status.Text = message; status.Foreground = Brushes.DarkGreen; }
        catch (Exception ex) { Error(ex); }
    }
    void Save()
    {
        var selected = entries.SelectedItem as Entry;
        Change(() =>
        {
            if (Kind == "notes") store.SaveNote(selected == null ? null : int.Parse(selected.Key), body.Text, revision);
            else if (Kind == "routines") store.SaveRoutine(name.Text, draft.Select(x => x.Action), selected?.Key, revision);
            else store.SaveLearned(name.Text, body.Text, draft.Select(x => x.Action), selected?.Key, revision);
        }, Kind == "notes" ? selected?.Key ?? view.Notes.Count.ToString() : CommandParser.Normalize(name.Text), UiText.Get("저장했어요. 다음 명령부터 적용됩니다."));
    }
    void Delete() { if (entries.SelectedItem is Entry selected) Change(() => store.Remove(Kind, selected.Key, revision), null, UiText.Get("삭제했어요. 마지막 변경 되돌리기로 복원할 수 있어요.")); }
    void Undo()
    {
        if (previous == null) return;
        try { store.ApplySnapshot(previous, undoVersion, undoStamp); previous = null; Refresh(); status.Text = UiText.Get("마지막 변경 전으로 복원했어요."); status.Foreground = Brushes.DarkGreen; }
        catch (Exception ex) { Error(ex); }
    }
    void Error(Exception error) { status.Text = error.Message; status.Foreground = Brushes.Firebrick; }
}
