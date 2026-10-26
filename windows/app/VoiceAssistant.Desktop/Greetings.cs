using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace VoiceAssistant.Desktop;

public static class Greetings
{
    public static string Fill(string value, UiSettings settings) => value.Replace("{user}", settings.Desktop.UserName).Replace("{assistant}", settings.Name);
    public static string Reply(UiSettings settings) => Fill(string.IsNullOrWhiteSpace(settings.Desktop.GreetingText) ?
        (settings.Desktop.UserName.Length == 0 ? "어서 오세요. 무엇을 도와드릴까요?" : "어서 오세요, {user}. 무엇을 도와드릴까요?") : settings.Desktop.GreetingText, settings);
    public static string Title(UiSettings settings) => Fill(string.IsNullOrWhiteSpace(settings.Desktop.GreetingTitle) ? settings.Name : settings.Desktop.GreetingTitle, settings);
    public static string Subtitle(UiSettings settings) => Fill(string.IsNullOrWhiteSpace(settings.Desktop.GreetingSubtitle) ?
        (settings.Desktop.UserName.Length == 0 ? "나만의 Windows 비서" : "{user}의 Windows 비서") : settings.Desktop.GreetingSubtitle, settings);
    public static CommandPlan? Parse(string text, UiSettings settings)
    {
        string Key(string value) => CommandParser.Normalize(CommandParser.StripWake(value, settings.Name)).Replace(" ", "").ToLowerInvariant();
        var phrases = settings.Desktop.GreetingPhrases.Length > 0 ? settings.Desktop.GreetingPhrases : new[] { "안녕", "안녕하세요", "아빠 왔어", "아빠 왔다", "나 왔어", "일어나", settings.Name + " 일어나", "일어나 " + settings.Name };
        return phrases.Any(p => Key(p) == Key(text)) ? new(Reply(settings), new[] { new AssistantAction("greet") }) : null;
    }
}

public sealed class GreetingWindow : Window
{
    readonly DispatcherTimer finish = new();
    public event Action? DismissRequested;
    public GreetingWindow(UiSettings settings)
    {
        var targetScreen = ScreenFor(settings.Desktop.OverlayScreen);
        Title = settings.Name + " · 인사"; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false; Topmost = true;
        Background = new SolidColorBrush(Color.FromRgb(7, 17, 35)); Width = 960; Height = 680; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new Grid { Margin = new Thickness(32) }; Content = root;
        var center = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 900 };
        bool reactor = settings.Desktop.GreetingStyle == "reactor";
        var orb = new Grid { Width = reactor ? 240 : 100, Height = reactor ? 240 : 100, Margin = new Thickness(0, 0, 0, 28) };
        var accent = new SolidColorBrush(Color.FromRgb(83, 143, 255));
        orb.Children.Add(new Ellipse { Fill = new RadialGradientBrush(Color.FromArgb(120, 49, 93, 231), Colors.Transparent) });
        if (reactor)
        {
            for (int i = 0; i < 3; i++)
            {
                var rotate = new RotateTransform(); var size = 222 - i * 40;
                var ring = new Ellipse { Width = size, Height = size, Stroke = accent, StrokeThickness = i == 0 ? 2 : 3, Opacity = 0.35 + i * 0.2, StrokeDashArray = new DoubleCollection(new[] { 12.0, 4.0, 1.0, 4.0 }), RenderTransformOrigin = new Point(.5, .5), RenderTransform = rotate };
                orb.Children.Add(ring); rotate.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, i % 2 == 0 ? 360 : -360, TimeSpan.FromSeconds(22 - i * 4)) { RepeatBehavior = RepeatBehavior.Forever });
            }
        }
        orb.Children.Add(new Ellipse { Width = 68, Height = 68, Fill = new SolidColorBrush(Color.FromRgb(49, 93, 231)), Stroke = accent, StrokeThickness = 2 });
        orb.Children.Add(new Ellipse { Width = 27, Height = 27, Fill = Brushes.White }); center.Children.Add(orb);
        center.Children.Add(new TextBlock { Text = Greetings.Title(settings), FontSize = 46, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap });
        center.Children.Add(new TextBlock { Text = Greetings.Subtitle(settings), FontSize = 16, Foreground = new SolidColorBrush(Color.FromRgb(142, 168, 207)), HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 12, 0, 26), TextWrapping = TextWrapping.Wrap });
        center.Children.Add(new TextBlock { Text = Greetings.Reply(settings), FontSize = 25, Foreground = new SolidColorBrush(Color.FromRgb(221, 233, 254)), TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxHeight = 180 }); root.Children.Add(center);
        var dismiss = new Button { Content = "계속하기 · Esc", Foreground = Brushes.White, Background = new SolidColorBrush(Color.FromRgb(35, 57, 91)), BorderThickness = new Thickness(0), Padding = new Thickness(22, 10, 22, 10), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom };
        dismiss.Click += (_, _) => Dismiss(); root.Children.Add(dismiss);
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Dismiss(); } };
        Loaded += (_, _) =>
        {
            var bounds = targetScreen.Bounds;
            SetWindowPos(new WindowInteropHelper(this).Handle, IntPtr.Zero, bounds.Left, bounds.Top, bounds.Width, bounds.Height, 0x0004);
            root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(500)));
            finish.Interval = TimeSpan.FromSeconds(reactor ? 6 : 4.5); finish.Start();
        };
        finish.Tick += (_, _) => Close(); Closed += (_, _) => { finish.Stop(); Content = null; };
    }
    internal static System.Windows.Forms.Screen ScreenFor(string choice) =>
        choice == "active" ? System.Windows.Forms.Screen.FromHandle(Native.GetForegroundWindow()) :
        (choice == "primary" ? null : System.Windows.Forms.Screen.AllScreens.FirstOrDefault(x => x.DeviceName == choice)) ?? System.Windows.Forms.Screen.PrimaryScreen ?? System.Windows.Forms.Screen.AllScreens[0];
    void Dismiss() { DismissRequested?.Invoke(); if (IsVisible) Close(); }
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr handle, IntPtr after, int x, int y, int width, int height, uint flags);
}
