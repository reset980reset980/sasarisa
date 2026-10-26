using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;

namespace VoiceAssistant.Desktop;

public sealed record TextSelection(IntPtr Window, AutomationElement Element, string Text);

public sealed class RewriteReview : Window
{
    public RewriteReview(string original, string revised)
    {
        Title = "선택한 글 바꾸기"; Width = 660; Height = 600; MinWidth = 480; MinHeight = 400; WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        var root = new DockPanel { Margin = new Thickness(20) }; Content = root;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var cancel = new Button { Content = "취소", IsCancel = true, Padding = new Thickness(24, 10, 24, 10), Margin = new Thickness(0, 0, 12, 0) };
        var apply = new Button { Content = "선택한 글에 적용", Padding = new Thickness(24, 10, 24, 10) }; apply.Click += (_, _) => DialogResult = true; buttons.Children.Add(cancel); buttons.Children.Add(apply); DockPanel.SetDock(buttons, Dock.Bottom); root.Children.Add(buttons);
        var fields = new Grid(); fields.RowDefinitions.Add(new() { Height = GridLength.Auto }); fields.RowDefinitions.Add(new()); fields.RowDefinitions.Add(new() { Height = GridLength.Auto }); fields.RowDefinitions.Add(new());
        Add(fields, new TextBlock { Text = "원문", Margin = new Thickness(0, 0, 0, 8) }, 0); Add(fields, Box(original), 1);
        Add(fields, new TextBlock { Text = "변경할 내용 — 아래 내용을 확인한 뒤 적용하세요.", Margin = new Thickness(0, 16, 0, 8) }, 2); Add(fields, Box(revised), 3); root.Children.Add(fields);
    }
    static TextBox Box(string text) => new() { Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(12), FontSize = 14 };
    static void Add(Grid grid, UIElement child, int row) { Grid.SetRow(child, row); grid.Children.Add(child); }
}
