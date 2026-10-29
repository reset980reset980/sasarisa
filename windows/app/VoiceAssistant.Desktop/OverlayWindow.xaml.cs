using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace VoiceAssistant.Desktop;

public partial class OverlayWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x00000080;
    private bool _positioned;
    private bool _dragged;
    private bool _idle;
    private bool _uiBusy;
    private bool _dismissed;
    private DesktopPreferences _preferences = new();
    private readonly bool _allowPresentation;

    public event Action? WakeRequested;
    public event Action? StopRequested;

    public OverlayWindow(bool allowPresentation = true)
    {
        _allowPresentation = allowPresentation;
        UiText.Ensure();
        InitializeComponent();
        UiText.LanguageChanged += RefreshLocalizedChrome;
        Closed += (_,_) => UiText.LanguageChanged -= RefreshLocalizedChrome;
        SourceInitialized += (_, _) =>
        {
            IntPtr handle = new WindowInteropHelper(this).Handle;
            SetWindowLong(handle, GwlExStyle, GetWindowLong(handle, GwlExStyle) | WsExNoActivate | WsExToolWindow);
        };
        SizeChanged += (_, _) => { if (!_dragged && IsVisible) Dispatcher.BeginInvoke(PositionAtCorner); };
    }

    public void SetContent(string title, string text, bool busy) => OnUi(() =>
    {
        UpdateAssistantLabel();
        SetIdle(false);
        _uiBusy = busy;
        OverlayTitle.Text = title;
        OverlayText.Text = text;
        OverlayState.Text = UiText.Get(busy ? "작업을 처리하고 있어요" : "음성을 기다리고 있어요");
        OverlayDot.Fill = new SolidColorBrush(busy ? Color.FromRgb(244, 189, 94) : Color.FromRgb(112, 209, 178));
    });

    public void HideOverlay() => OnUi(Hide);

    public void ShowOverlay() => OnUi(() =>
    {
        _dismissed = false;
        UpdateAssistantLabel();
        if (!_allowPresentation) return;
        if (!IsVisible) Show();
        if (!_dragged || !_positioned) PositionAtCorner();
    });
    public void Configure(DesktopPreferences preferences) => OnUi(() =>
    {
        _preferences = preferences with { }; _dragged = false; _positioned = false;
        if (IsVisible) PositionAtCorner();
    });
    public void ShowIdle() => OnUi(() =>
    {
        if (_dismissed || (_idle && IsVisible)) return;
        SetIdle(true); ShowOverlay();
    });
    void SetIdle(bool idle)
    {
        if (_idle == idle) return;
        _idle = idle; Width = idle ? 76 : 360;
        RootBorder.Padding = idle ? new Thickness(8) : new Thickness(17,16,17,14);
        CardPanel.Visibility = idle ? Visibility.Collapsed : Visibility.Visible;
        IdleButton.Visibility = idle ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateAssistantLabel()
    {
        if (Application.Current?.MainWindow is not MainWindow window) return;
        OverlayBrand.Text = window.Title;
        Title = UiText.Format("{0} · 알림",window.Title);
    }
    void RefreshLocalizedChrome() => OnUi(() =>
    {
        UpdateAssistantLabel();
        OverlayState.Text = UiText.Get(_uiBusy ? "작업을 처리하고 있어요" : "음성을 기다리고 있어요");
    });

    private void PositionAtCorner()
    {
        var screens = System.Windows.Forms.Screen.AllScreens;
        var screen = _preferences.OverlayScreen == "active" ? System.Windows.Forms.Screen.FromHandle(Native.GetForegroundWindow()) :
            _preferences.OverlayScreen == "primary" ? System.Windows.Forms.Screen.PrimaryScreen : Array.Find(screens, s => s.DeviceName == _preferences.OverlayScreen);
        screen ??= System.Windows.Forms.Screen.PrimaryScreen ?? screens[0];
        var area = screen.WorkingArea; var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        if (!GetWindowRect(handle, out var bounds)) return;
        var point = Placement(new Rect(area.Left, area.Top, area.Width, area.Height), new Size(bounds.Right - bounds.Left, bounds.Bottom - bounds.Top), _preferences.OverlayPosition);
        SetWindowPos(handle, IntPtr.Zero, (int)Math.Round(point.X), (int)Math.Round(point.Y), 0, 0, 0x0015);
        _positioned = true;
    }
    internal static Point Placement(Rect workArea, Size size, string position)
    {
        const double margin = 20;
        var x = position == "bottomLeft" ? workArea.Left + margin : position == "bottomRight" ? workArea.Right - size.Width - margin : workArea.Left + (workArea.Width - size.Width) / 2;
        var y = position == "top" ? workArea.Top + margin : position == "center" ? workArea.Top + (workArea.Height - size.Height) / 2 : workArea.Bottom - size.Height - margin;
        return new(Math.Clamp(x, workArea.Left, Math.Max(workArea.Left, workArea.Right - size.Width)), Math.Clamp(y, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - size.Height)));
    }

    private void OnUi(Action action)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        if (Dispatcher.CheckAccess()) action();
        else Dispatcher.BeginInvoke(action);
    }

    private void Wake_Click(object sender, RoutedEventArgs e) => WakeRequested?.Invoke();
    private void Stop_Click(object sender, RoutedEventArgs e) => StopRequested?.Invoke();
    private void Hide_Click(object sender, RoutedEventArgs e) { _dismissed = true; HideOverlay(); }
    private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        for (var node = e.OriginalSource as DependencyObject; node != null; node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
            if (node is System.Windows.Controls.Primitives.ButtonBase) return;
        if (e.ButtonState != MouseButtonState.Pressed) return;
        try { DragMove(); _dragged = true; }
        catch (InvalidOperationException) { /* The mouse may be released before the move begins. */ }
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr handle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr handle, int index, int value);
    [StructLayout(LayoutKind.Sequential)] struct WindowRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr handle, out WindowRect rect);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr handle, IntPtr after, int x, int y, int width, int height, uint flags);
}
