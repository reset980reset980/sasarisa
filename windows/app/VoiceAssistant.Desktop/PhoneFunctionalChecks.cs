using System;
using System.IO;
using System.Threading.Tasks;

namespace VoiceAssistant.Desktop;

public partial class App
{
    async Task VerifyPhoneTimersAsync(PhoneProbe phone)
    {
        timers.Clear();
        try
        {
            var answer = await phone.Command("30분 타이머");
            if (timers.Items.Count != 1 || !answer.Contains("타이머")) throw new Exception("Timer command did not reach App");
            if (!(await phone.Command("타이머 목록")).Contains("30분")) throw new Exception("Timer list missing");
            await phone.Command("타이머 취소");
            if (timers.Items.Count != 0) throw new Exception("Timer cancellation missing");
        }
        finally { timers.Clear(); }
    }
    async Task VerifyPhoneSpeechAsync(PhoneProbe phone, bool failOutput = false)
    {
        var saved = settings;
        var log = Path.Combine(AppPaths.Home, "assistant.log");
        var before = File.Exists(log) ? File.ReadAllText(log).Length : 0;
        try
        {
            settings = settings with { Speak = true, SpeechVolume = 0, SpeechRate = 5, OutputId = failOutput ? "missing-phone-check-device" : "" };
            var result = await phone.Command("지금 몇 시야");
            if (failOutput)
            {
                if (!result.Contains("PC 음성 출력에 실패") || !result.Contains("시")) throw new Exception("Phone missed playback failure or answer");
                return;
            }
            var added = File.ReadAllText(log)[before..];
            if (!result.Contains("PC 답변 음량이 0") || !added.Contains("[tts-audio] start provider=windows volume=0") || !added.Contains("[tts-audio] done") || result.Contains("실패")) throw new Exception("Actual muted Windows output was not completed: " + result);
        }
        finally { settings = saved; }
    }
}
