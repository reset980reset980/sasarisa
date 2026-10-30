using System.Windows;
using System.Windows.Controls;

namespace VoiceAssistant.Desktop;

public partial class MainWindow
{
    void RefreshUiText() => OnUi(() =>
    {
        ShowPage(_page); UpdateHotkeyHints(); RefreshLanguageHelp(); RefreshBrainAccessHelp();
        if (_lastUiStatus is { } state) SetStatusCore(state.Title,_statusDetailCopy?.Render() ?? state.Detail,_statusDetailCopy,state.Listening,state.Busy,state.Paused,state.Attention);
        foreach (var child in HistoryPanel.Children)
            if (child is Border { Child: StackPanel panel } && panel.Children.Count > 0 && panel.Children[0] is Grid header)
                foreach (var part in header.Children)
                    if (part is TextBlock { Tag: string tag } label)
                        switch (tag)
                        {
                            case "ui-role-user": label.Text = UiText.Get("나"); break;
                            case "ui-role-phone": label.Text = UiText.Get("휴대폰"); break;
                            case "ui-role-error": label.Text = UiText.Get("확인이 필요해요"); break;
                            case "ui-role-system": label.Text = UiText.Get("안내"); break;
                        }
    });
}
