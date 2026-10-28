using System;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;

namespace VoiceAssistant.Desktop;

// Opt-in, disposable WPF peer for real UIA/SendInput integration checks.
public partial class App
{
    async Task RunTextEditFixtureAsync(string pipe)
    {
        var root = new StackPanel { Margin = new Thickness(24) };
        var field = new TextBox { AcceptsReturn = true, MinHeight = 120, FontSize = 20 };
        var untouched = new TextBox { Text = "다른 칸 유지", MinHeight = 40 };
        AutomationProperties.SetAutomationId(field, "undo-field"); AutomationProperties.SetName(field, "되돌리기 시험칸");
        root.Children.Add(new TextBlock { Text = "자비스 문서 되돌리기 시험 창" }); root.Children.Add(field); root.Children.Add(untouched);
        var fixture = new Window { Title = "Sasarisa disposable text undo fixture", Content = root, Width = 500, Height = 300 };
        var other = new Window { Title = "Sasarisa unrelated fixture", Content = new TextBox { Text = "다른 창 유지" }, Width = 320, Height = 150 };
        fixture.Show();
        try
        {
            using var connection = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
            await connection.ConnectAsync(10000); using var reader = new StreamReader(connection); using var writer = new StreamWriter(connection) { AutoFlush = true };
            while (await reader.ReadLineAsync() is { } line)
            {
                using var json = JsonDocument.Parse(line); var command = json.RootElement; var kind = command.GetProperty("kind").GetString();
                if (kind == "close") break;
                if (kind == "set") { field.IsReadOnly = false; field.Text = command.GetProperty("text").GetString()!; field.Select(command.GetProperty("start").GetInt32(), command.GetProperty("length").GetInt32()); }
                if (kind == "readonly") field.IsReadOnly = true;
                if (kind == "replace") { root.Children.Remove(field); field = new TextBox { Text = "새 입력칸", MinHeight = 120 }; AutomationProperties.SetAutomationId(field, "undo-field"); root.Children.Insert(1, field); }
                if (kind == "other") { other.Show(); other.Activate(); }
                if (kind is "set" or "focus" or "replace" or "readonly") { fixture.Activate(); field.Focus(); Native.SetForegroundWindow(new WindowInteropHelper(fixture).Handle); }
                await Task.Delay(100);
                await writer.WriteLineAsync(JsonSerializer.Serialize(new { handle = new WindowInteropHelper(fixture).Handle.ToInt64(), text = FieldInsertion.Normalize(field.Text), start = field.SelectionStart, length = field.SelectionLength, untouched = untouched.Text }));
            }
        }
        finally { fixture.Close(); other.Close(); }
    }
}
