using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;

namespace VoiceAssistant.Desktop;

public sealed class AiUsageWindow : Window
{
    readonly AiUsageStore store;
    readonly string codexRoot, assistant;
    readonly bool english;
    readonly Func<DateTimeOffset> now;
    readonly StackPanel quotas = new();
    readonly Grid chart = new() { Height = 165, Margin = new Thickness(0,12,0,5) };
    readonly TextBlock today = new() { TextWrapping = TextWrapping.Wrap, LineHeight = 21 }, status = new() { TextWrapping = TextWrapping.Wrap, LineHeight = 20 };
    readonly ComboBox provider = new() { DisplayMemberPath = "Label", SelectedValuePath = "Id", MinWidth = 150 }, metric = new() { MinWidth = 220, SelectedIndex = 0 };
    readonly DataGrid breakdown = new() { IsReadOnly = true, AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false, HeadersVisibility = DataGridHeadersVisibility.Column, MinColumnWidth = 110, MinHeight = 110, MaxHeight = 240, Margin = new Thickness(0,10,0,0) };
    readonly Button refresh;
    AiUsageSnapshot snapshot = new(new(), null, 0, 0);
    CancellationTokenSource? refreshing;
    int generation; bool closed, rendering;
    internal string Summary => today.Text;
    internal string StatusText => status.Text;
    internal int QuotaCount => quotas.Children.Count;
    internal bool Refreshing => refreshing != null;
    string T(string ko, string en) => english ? en : ko;
    public AiUsageWindow(AiUsageStore store, string assistant, string language = "ko", string? codexRoot = null, Func<DateTimeOffset>? now = null)
    {
        this.store = store; this.assistant = assistant; english = language != "ko"; this.codexRoot = codexRoot ?? CodexUsageReader.DefaultRoot; this.now = now ?? (() => DateTimeOffset.Now);
        Title = T("AI 사용량", "AI usage"); Width = 940; Height = 830; MinWidth = 760; MinHeight = 640; WindowStartupLocation = WindowStartupLocation.CenterOwner; Background = (Brush)Application.Current.FindResource("CanvasBrush");
        var root = new DockPanel { Margin = new Thickness(24) }; Content = root;
        var header = new Grid { Margin = new Thickness(0,0,0,16) }; header.ColumnDefinitions.Add(new()); header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        header.Children.Add(new TextBlock { Text = Title, FontSize = 24, FontWeight = FontWeights.SemiBold });
        refresh = new Button { Content = T("새로고침", "Refresh"), Style = (Style)Application.Current.FindResource("SecondaryButton"), Padding = new Thickness(16,8,16,8) }; refresh.Click += async (_,_) => await RefreshAsync(); Grid.SetColumn(refresh, 1); header.Children.Add(refresh); DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        var footer = new StackPanel { Margin = new Thickness(0,14,0,0) };
        footer.Children.Add(status);
        footer.Children.Add(new TextBlock { Text = T("한도 막대는 다른 앱을 포함한 계정 전체의 마지막 보고값입니다. 그래프는 이 PC에서 자비스가 보낸 AI 요청 중 보고된 토큰만 집계합니다. 요청문·답변·API 키는 사용 기록에 보관하지 않으며 새로고침은 로컬 CLI 기록만 읽습니다.", "Plan bars show the last reported account-wide limits, including other apps. The chart counts reported tokens from this assistant on this PC. Prompts, replies and API keys are not saved in usage records. Refresh only reads local CLI logs."), TextWrapping = TextWrapping.Wrap, Foreground = Brushes.SlateGray, LineHeight = 20, Margin = new Thickness(0,8,0,0) });
        var close = new Button { Content = T("닫기", "Close"), IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right, Padding = new Thickness(18,8,18,8), Style = (Style)Application.Current.FindResource("SecondaryButton"), Margin = new Thickness(0,10,0,0) }; close.Click += (_,_) => Close(); footer.Children.Add(close); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var body = new StackPanel(); root.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0,0,10,0) });
        body.Children.Add(Heading(T("계정 전체 한도 · 마지막 보고", "Account-wide limits · last report"))); body.Children.Add(quotas);
        body.Children.Add(new TextBlock { Text = T("Claude 한도는 이 앱의 Claude 응답에서 갱신합니다. Codex 한도는 최근 로컬 세션 기록을 읽습니다. 계정 변경·초기화 뒤의 새 값은 다음 보고가 필요합니다.", "Claude limits update from this app's Claude responses. Codex limits are read from recent local sessions. A new report is needed after account changes or resets."), TextWrapping = TextWrapping.Wrap, Foreground = Brushes.SlateGray, LineHeight = 20, Margin = new Thickness(0,7,0,15) });
        body.Children.Add(Heading(T(assistant + "가 기록한 양 · 최근 7일", "Recorded by " + assistant + " · last 7 days")));
        var filters = new WrapPanel { Margin = new Thickness(0,10,0,0) }; provider.Margin = new Thickness(0,0,10,7); metric.Margin = new Thickness(0,0,0,7);
        metric.Items.Add(T("환산 토큰 · 캐시 읽기 1/10", "Equivalent tokens · cache reads × 0.1")); metric.Items.Add(T("보고된 전체 토큰", "All reported tokens")); metric.Items.Add(T("AI 요청 시도", "AI request attempts"));
        filters.Children.Add(provider); filters.Children.Add(metric); body.Children.Add(filters); body.Children.Add(chart); body.Children.Add(today);
        foreach (var (caption, field) in new[] { (T("AI", "AI"), "Provider"), (T("요청", "Attempts"), "Calls"), (T("토큰 보고", "Reported"), "Reported"), (T("전체 토큰", "Tokens"), "Tokens"), (T("캐시 읽기", "Cache read"), "Cache"), (T("실패/중단", "Failed/stopped"), "Stopped") })
            breakdown.Columns.Add(new DataGridTextColumn { Header = caption, Binding = new Binding(field), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        body.Children.Add(breakdown);
        provider.SelectionChanged += (_,_) => { if (!rendering) Render(); }; metric.SelectionChanged += (_,_) => { if (!rendering) Render(); };
        Loaded += async (_,_) => await RefreshAsync(); Closed += (_,_) => { closed = true; generation++; refreshing?.Cancel(); };
        Render();
    }
    TextBlock Heading(string text) => new() { Text = text, FontSize = 17, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0,7,0,0) };
    internal async Task RefreshAsync()
    {
        var ownGeneration = ++generation; refreshing?.Cancel(); var own = new CancellationTokenSource(); refreshing = own; refresh.IsEnabled = false;
        try
        {
            var scan = await CodexUsageReader.RefreshAsync(store, codexRoot, now(), own.Token); var next = await Task.Run(() => store.Snapshot(), own.Token);
            if (closed || ownGeneration != generation || own.IsCancellationRequested) return; snapshot = next; Render();
            status.Text = next.Error ?? scan.Error ?? T("마지막 확인 ", "Last refreshed ") + now().ToLocalTime().ToString("MM/dd HH:mm:ss", CultureInfo.InvariantCulture);
            if (next.Pending > 0) status.Text += T(" · 저장 대기 ", " · pending ") + next.Pending;
            if (next.Dropped > 0) status.Text += T(" · 저장되지 못한 요청 ", " · requests not saved ") + next.Dropped;
            status.Foreground = next.Error != null || scan.Error != null || next.Dropped > 0 ? Brushes.Firebrick : Brushes.SlateGray;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or ArgumentException) { if (!closed && ownGeneration == generation) { status.Text = T("사용 기록을 읽지 못했어요. ", "Could not read usage records. ") + ex.GetType().Name; status.Foreground = Brushes.Firebrick; } }
        finally { if (ReferenceEquals(refreshing, own)) { refreshing = null; if (!closed) refresh.IsEnabled = true; } own.Dispose(); }
    }
    internal void LoadSnapshot(AiUsageSnapshot value) { snapshot = value; Render(); }
    internal void Select(string providerId, int metricIndex) { provider.SelectedValue = providerId; metric.SelectedIndex = metricIndex; Render(); }
    static string Short(long value) => value >= 1_000_000 ? (value / 1_000_000d).ToString("0.##", CultureInfo.InvariantCulture) + "M" : value >= 1000 ? (value / 1000d).ToString("0.##", CultureInfo.InvariantCulture) + "K" : value.ToString("N0", CultureInfo.InvariantCulture);
    void Render()
    {
        if (rendering) return; rendering = true;
        try
        {
            var selected = provider.SelectedValue as string ?? "all";
            var providers = snapshot.State.Days.Values.SelectMany(x => x.Keys).Concat(snapshot.State.Windows.Values.Select(x => x.Provider)).Distinct().OrderBy(Brain.NameFor).ToArray();
            provider.ItemsSource = new[] { new DeviceOption("all", T("모든 AI", "All AI")) }.Concat(providers.Select(x => new DeviceOption(x, Brain.NameFor(x)))).ToArray(); provider.SelectedValue = selected == "all" || providers.Contains(selected) ? selected : "all"; selected = provider.SelectedValue as string ?? "all";
            quotas.Children.Clear();
            foreach (var key in new[] { "claude", "codex" })
            {
                if (selected != "all" && selected != key) continue;
                var values = snapshot.State.Windows.Values.Where(x => x.Provider == key).OrderBy(x => x.Key).ToArray();
                if (values.Length == 0) { quotas.Children.Add(new TextBlock { Text = Brain.NameFor(key) + T(" · 아직 보고된 한도가 없어요.", " · no reported limit yet."), Foreground = Brushes.SlateGray, Margin = new Thickness(0,10,0,5) }); continue; }
                foreach (var window in values)
                {
                    var stale = window.Resets != null && window.Resets <= now(); var panel = new StackPanel { Margin = new Thickness(0,11,0,2) };
                    var label = english ? window.Label.Replace("시간", " h").Replace("일", " days").Replace("분", " min").Replace("기본 한도", "Primary limit").Replace("추가 한도", "Secondary limit") : window.Label;
                    panel.Children.Add(new TextBlock { Text = Brain.NameFor(key) + " · " + label + " · " + T("보고 당시 ", "At last report ") + (window.Used * 100).ToString("0.#", CultureInfo.InvariantCulture) + T("% 사용", "% used") + (stale ? T(" · 새 보고 필요", " · new report needed") : ""), FontWeight = FontWeights.SemiBold });
                    panel.Children.Add(new ProgressBar { Minimum = 0, Maximum = 100, Value = window.Used * 100, Height = 9, Margin = new Thickness(0,6,0,5), Foreground = stale ? Brushes.SlateGray : Brushes.RoyalBlue });
                    panel.Children.Add(new TextBlock { Text = T("관측 ", "Seen ") + window.Seen.ToLocalTime().ToString("MM/dd HH:mm", CultureInfo.InvariantCulture) + (window.Resets is { } resets ? T(" · 초기화 보고 시각 ", " · reported reset ") + resets.ToLocalTime().ToString("MM/dd HH:mm", CultureInfo.InvariantCulture) : T(" · 초기화 시각 미제공", " · no reset time reported")), Foreground = Brushes.SlateGray }); quotas.Children.Add(panel);
                }
            }
            var date = now().LocalDateTime.Date;
            var rows = Enumerable.Range(0, 7).Select(back => date.AddDays(back - 6)).Select(day =>
            {
                var key = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture); var values = snapshot.State.Days.GetValueOrDefault(key) ?? new();
                return (Day: day, Usage: AiUsageStore.Sum(values.Where(x => selected == "all" || x.Key == selected).Select(x => x.Value)));
            }).ToArray();
            long Value(AiDailyUsage row) => metric.SelectedIndex == 2 ? row.Calls : metric.SelectedIndex == 1 ? row.Tokens.Total : row.Tokens.Equivalent;
            var max = Math.Max(1, rows.Max(x => Value(x.Usage))); chart.Children.Clear(); chart.ColumnDefinitions.Clear();
            for (var i = 0; i < rows.Length; i++)
            {
                chart.ColumnDefinitions.Add(new()); var row = rows[i]; var value = Value(row.Usage); var panel = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Center };
                var partial = metric.SelectedIndex != 2 && row.Usage.Calls > row.Usage.ReportedCalls;
                panel.Children.Add(new TextBlock { Text = value == 0 && row.Usage.Calls == 0 ? T("기록 없음", "No record") : value == 0 && metric.SelectedIndex != 2 && row.Usage.ReportedCalls == 0 ? T("미제공", "Unknown") : Short(value) + (partial ? " *" : ""), FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center });
                panel.Children.Add(new Rectangle { Width = 40, Height = value == 0 ? 2 : Math.Max(3, value / (double)max * 100), Fill = row.Usage.ReportedCalls == 0 && metric.SelectedIndex != 2 ? Brushes.LightSlateGray : Brushes.RoyalBlue, Margin = new Thickness(0,5,0,6), ToolTip = value.ToString("N0", CultureInfo.InvariantCulture) + (partial ? T(" · 토큰 수 미제공 요청은 이 수치에 포함되지 않아요: ", " · excludes attempts without token counts: ") + (row.Usage.Calls - row.Usage.ReportedCalls) : "") });
                panel.Children.Add(new TextBlock { Text = row.Day.ToString("M/d", CultureInfo.InvariantCulture), HorizontalAlignment = HorizontalAlignment.Center }); Grid.SetColumn(panel, i); chart.Children.Add(panel);
            }
            var last = rows.Last().Usage;
            today.Text = last.Calls == 0 ? T("오늘 이 앱에 기록된 AI 요청이 없어요.", "No AI requests recorded by this app today.") : T("오늘 요청 시도 ", "Today's attempts ") + last.Calls + T("회 · 토큰 수 보고 ", " · with token counts ") + last.ReportedCalls + T("회 · 정보 미제공 ", " · counts unavailable ") + (last.Calls - last.ReportedCalls) + T("회\n보고된 전체 토큰 ", "\nAll reported tokens ") + (last.ReportedCalls == 0 ? T("미제공", "Unknown") : Short(last.Tokens.Total)) + T(" · 환산 ", " · equivalent ") + (last.ReportedCalls == 0 ? T("미제공", "Unknown") : Short(last.Tokens.Equivalent));
            today.Text += T("\n환산은 원본 방식대로 캐시 읽기를 1/10로 세는 참고치이며 요금이나 잔액이 아닙니다. 기록 이전 사용량은 추정하지 않습니다.", "\nEquivalent tokens count cache reads at one tenth, matching the original. This is not a bill or balance. Earlier usage is not estimated.");
            if (rows.Any(x => x.Usage.Calls > x.Usage.ReportedCalls) && metric.SelectedIndex != 2) today.Text += T("\n* 토큰 수가 제공되지 않은 요청은 토큰 막대에 포함되지 않아요.", "\n* Token bars exclude attempts whose counts were not reported.");
            var dayRows = snapshot.State.Days.GetValueOrDefault(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)) ?? new();
            breakdown.ItemsSource = dayRows.Where(x => selected == "all" || x.Key == selected).Select(x => new { Provider = Brain.NameFor(x.Key), Calls = x.Value.Calls.ToString("N0", CultureInfo.InvariantCulture), Reported = x.Value.ReportedCalls.ToString("N0", CultureInfo.InvariantCulture), Tokens = x.Value.ReportedCalls == 0 ? "—" : Short(x.Value.Tokens.Total), Cache = x.Value.ReportedCalls == 0 ? "—" : Short(x.Value.Tokens.CacheRead), Stopped = x.Value.Failed + "/" + x.Value.Canceled }).ToArray();
        }
        finally { rendering = false; }
    }
}
