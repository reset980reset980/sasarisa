using System.Windows.Controls;

namespace VoiceAssistant.Desktop;

public partial class MainWindow
{
    void LanguageInput_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LanguageHelp == null || KwsInput == null || WakeHintsInput == null) return;
        var korean = LanguageInput.SelectedValue is not string code || code == "ko";
        KwsInput.IsEnabled = WakeHintsInput.IsEnabled = ThresholdInput.IsEnabled = korean;
        LanguageHelp.Text = korean ? "한국어 실시간 인식과 호출어 감지를 사용합니다. 변경은 설정 저장 후 적용됩니다." :
            "말이 끝나면 선택한 언어로 인식하고, AI도 같은 언어로 답합니다. 아래 ‘음성 보정’에서 CPU·NVIDIA 엔진을 준비하거나 로컬 Whisper 서버를 연결하세요. 호출어를 해당 언어의 발음으로 입력하고 답변 목소리도 확인하세요. 화면 메뉴는 한국어로 표시합니다. 변경은 설정 저장 후 적용됩니다.";
    }
}
