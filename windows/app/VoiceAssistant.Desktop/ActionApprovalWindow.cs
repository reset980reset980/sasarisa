using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VoiceAssistant.Desktop;

internal sealed class ActionApprovalWindow : Window
{
    internal ActionApprovalWindow(Window? owner, string description, VisualPreview? preview = null)
    {
        Owner = owner; Title = UiText.Get("실행 내용 확인"); Width = 620; Height = preview == null ? 440 : 680;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        var layout = new DockPanel { Margin = new Thickness(20) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var no = new Button { Content = UiText.Get("취소"), IsCancel = true, MinWidth = 90, Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(0, 0, 10, 0) };
        var yes = new Button { Content = UiText.Get("실행"), MinWidth = 90, Padding = new Thickness(12, 7, 12, 7) };
        yes.Click += (_, _) => { DialogResult = true; };
        buttons.Children.Add(no); buttons.Children.Add(yes); DockPanel.SetDock(buttons, Dock.Bottom); layout.Children.Add(buttons);
        var content = new StackPanel();
        content.Children.Add(new TextBox { Text = description + UiText.Get("\n\n이 내용을 실행할까요?"), IsReadOnly = true, TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0), FontSize = 16 });
        if (preview != null)
        {
            using var bytes = new MemoryStream(Convert.FromBase64String(preview.Image.Base64));
            var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = bytes; bitmap.EndInit(); bitmap.Freeze();
            var drawing = new DrawingGroup { ClipGeometry = new RectangleGeometry(new Rect(0, 0, bitmap.PixelWidth, bitmap.PixelHeight)) };
            using (var dc = drawing.Open())
            {
                dc.DrawImage(bitmap, new Rect(0, 0, bitmap.PixelWidth, bitmap.PixelHeight));
                var radius = Math.Max(10, bitmap.PixelWidth / 60d); var point = new Point(preview.X + .5, preview.Y + .5);
                var pen = new Pen(Brushes.Red, Math.Max(2, radius / 6));
                dc.DrawEllipse(null, pen, point, radius, radius);
                dc.DrawLine(pen, new Point(point.X - radius * 1.5, point.Y), new Point(point.X + radius * 1.5, point.Y));
                dc.DrawLine(pen, new Point(point.X, point.Y - radius * 1.5), new Point(point.X, point.Y + radius * 1.5));
            }
            drawing.Freeze();
            content.Children.Add(new TextBlock { Text = UiText.Get("빨간 표시의 위치를 누릅니다."), Margin = new Thickness(0, 16, 0, 8), FontSize = 15 });
            content.Children.Add(new Image { Source = new DrawingImage(drawing), Stretch = Stretch.Uniform, MaxHeight = 380 });
        }
        layout.Children.Add(new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = layout; Loaded += (_, _) => { Activate(); no.Focus(); };
    }
    internal static bool Confirm(Window owner, string description, CancellationToken token, VisualPreview? preview = null)
    {
        token.ThrowIfCancellationRequested(); var dialog = new ActionApprovalWindow(owner, description, preview);
        if (!owner.IsVisible) owner.Show();
        if (owner.WindowState == WindowState.Minimized) owner.WindowState = WindowState.Normal;
        owner.Activate();
        using var cancel = token.Register(() => dialog.Dispatcher.BeginInvoke(() => { if (dialog.IsVisible) dialog.Close(); }));
        var approved = dialog.ShowDialog() == true;
        token.ThrowIfCancellationRequested(); return approved;
    }
}
