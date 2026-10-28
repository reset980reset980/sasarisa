using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace VoiceAssistant.Desktop;

public enum ActionEffect { None, Send, Post, Delete, Delegate, Payment, Unknown }
public enum ConsentDecision { Run, Ask, HoldRemote }

public static class ExecutionConsent
{
    internal static FreeModeAnswer ParseAnswer(string text)
    {
        var value = text.Trim().Trim('.', '!', '?', '~').Trim().ToLowerInvariant();
        if (new[] { "응", "네", "예", "그래", "좋아", "오케이", "yes", "yeah", "sure", "ok", "okay" }.Contains(value)) return FreeModeAnswer.Yes;
        if (new[] { "아니", "아니야", "됐어", "싫어", "no", "nope" }.Contains(value)) return FreeModeAnswer.No;
        return FreeModeAnswer.None;
    }
    public static string ModeLabel(string value) => value switch { "never" => "바로 실행", "brain" => "AI가 작업을 넘길 때 묻기", "always" => "물어보고 실행", _ => throw new ArgumentException("실행 방식을 확인하세요.") };
    public static string Prompt(UiSettings settings, bool remote) =>
        "실행 방식: " + ModeLabel(settings.ConfirmDelegate) + ". 직접 요청한 위임과 AI가 제안한 위임을 앱이 구분한다. " +
        (settings.ConfirmDelegate == "never" ? "짧은 요청도 관측한 대상과 직전 작업으로 뜻이 분명하면 다시 묻지 않고 처리한다. " : "뜻이 둘로 갈리면 실행 전에 한 가지 질문으로 확인한다. ") +
        (settings.FreeMode ? "현재 자유 모드다. 관측한 대상의 전송·게시·삭제·위임 등은 앱의 검사 뒤 추가 질문 없이 실행할 수 있다. " : "현재 확인 모드다. 실행 확인이 필요한 도구는 앱에서 사용자 확인을 받는다. ") +
        "결제는 항상 앱에서 확인하며 휴대폰 요청으로는 결제하지 않는다. 대상을 확인할 수 없으면 앱이 확인하거나 실행을 보류한다. 도구 설명의 확인 문구보다 앱의 현재 실행 설정과 실제 도구 결과를 따른다. 확인을 모델이 대신 승인하거나 설정을 변경할 수 없다. 실행하지 않은 동작을 완료했다고 말하지 않는다. " +
        (settings.PreviewBeforeSend ? "Claude 전송 전 미리 보기가 켜져 있다. 자유 모드라도 초안/내용을 확인하고 사용자가 보내기를 선택해야 전송한다. " : "") +
        (remote ? "이 요청은 휴대폰에서 왔다. PC 확인이 필요하다는 결과 뒤에는 다른 키·화면·위임 도구로 우회하지 않는다." : "");
    static readonly HashSet<string> NavigationLabels = new(new[]
    {
        "게시판", "게시물", "게시글", "주문내역", "구매내역", "결제내역", "결제수단", "보낸편지함", "보낸메일", "보낸항목",
        "sent", "sentitems", "posts", "orderhistory", "purchasehistory", "paymenthistory", "paymentmethods"
    });
    static readonly string[] Payments =
    [
        "결제", "구매", "구입", "주문하기", "주문확정", "주문완료", "지금주문", "송금", "이체", "pay", "buy", "purchase", "checkout", "placeorder", "confirmorder", "transfermoney", "sendmoney",
        "支払", "購入", "注文を確定", "送金", "支付", "付款", "购买", "購買", "确认订单", "確認訂單", "转账", "轉帳",
        "pagar", "comprar", "payer", "acheter", "bezahlen", "kaufen", "pagamento", "pagare", "acquistare", "thanhtoán", "muangay", "ชำระเงิน", "สั่งซื้อ", "pembayaran", "bayar", "membeli"
    ];
    public static string Normalize(string value) => string.Concat(value.Normalize(NormalizationForm.FormC).EnumerateRunes()
        .Where(r => Rune.IsLetterOrDigit(r) || Rune.GetUnicodeCategory(r) is System.Globalization.UnicodeCategory.NonSpacingMark or System.Globalization.UnicodeCategory.SpacingCombiningMark)).ToLowerInvariant();
    // Input must be the observed target label/context, never the model's description or text being entered.
    public static ActionEffect LabelEffect(string label, string context = "")
    {
        var name = Normalize(label);
        if (NavigationLabels.Contains(name)) return ActionEffect.None;
        var full = name + Normalize(context);
        if (Payments.Any(full.Contains)) return ActionEffect.Payment;
        if (new[] { "보내", "전송", "send", "送信", "发送", "發送", "enviar", "envoyer", "senden", "invia", "gửi", "ส่งข้อความ", "kirim" }.Any(name.Contains)) return ActionEffect.Send;
        if (new[] { "게시", "올리기", "업로드", "publish", "upload", "tweet", "post", "公開", "投稿", "发布", "發佈", "publicar", "publier", "veröffentlichen", "đăng", "เผยแพร่", "unggah" }.Any(name.Contains)) return ActionEffect.Post;
        if (new[] { "삭제", "지우기", "휴지통비우기", "delete", "emptytrash", "remove", "削除", "删除", "刪除", "eliminar", "supprimer", "löschen", "excluir", "elimina", "xóa", "ลบ", "hapus" }.Any(name.Contains)) return ActionEffect.Delete;
        if (new[] { "확인", "예", "다음", "계속", "ok", "yes", "continue", "next", "submit", "confirm", "確定", "確認", "确定", "繼續", "继续" }.Contains(name)) return ActionEffect.Unknown;
        return name.Length == 0 ? ActionEffect.Unknown : ActionEffect.None;
    }
    public static ActionEffect Effect(AssistantAction action, string? observedLabel = null, string context = "") => action.Kind switch
    {
        "kakao_send" or "kakao_send_draft" => ActionEffect.Send,
        "delegate" or "delegate_codex" or "delegate_session" => ActionEffect.Delegate,
        "forget_routine" => ActionEffect.Delete,
        "kakao_draft" or "fill_field" or "insert_field" or "screenshot" or "browser_close" or "browser_close_site" or "display_off" => ActionEffect.None,
        "key" when !KeyboardChord.Parse(action.Value).NeedsConfirmation => ActionEffect.None,
        "tap" or "press_control" or "click" or "key" => observedLabel == null ? ActionEffect.Unknown : LabelEffect(observedLabel, context),
        _ => ActionEffect.None
    };
    public static ConsentDecision Decide(bool freeMode, AssistantAction action, bool fromAi, ActionEffect effect, bool remote) => Decide(new UiSettings { FreeMode = freeMode }, action, fromAi, effect, remote);
    public static ConsentDecision Decide(UiSettings settings, AssistantAction action, bool fromAi, ActionEffect effect, bool remote)
    {
        if (effect == ActionEffect.Payment) return remote ? ConsentDecision.HoldRemote : ConsentDecision.Ask;
        if (!ActionPolicy.NeedsConfirmation(action, fromAi)) return ConsentDecision.Run;
        if (remote && settings.PreviewBeforeSend && effect == ActionEffect.Delegate) return ConsentDecision.HoldRemote;
        if (settings.FreeMode && effect != ActionEffect.Unknown) return ConsentDecision.Run;
        if (!remote && settings.ConfirmDelegate != "always" && (effect == ActionEffect.None || effect == ActionEffect.Delegate && !fromAi)) return ConsentDecision.Run;
        return remote ? ConsentDecision.HoldRemote : ConsentDecision.Ask;
    }
}

internal enum FreeModeAnswer { None, Yes, No }
internal sealed class FreeModeOffer(Func<DateTimeOffset>? clock = null)
{
    readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.UtcNow);
    DateTimeOffset expires;
    public bool Pending => now() < expires;
    public static bool Eligible(UiSettings settings, ActionEffect confirmedEffect, bool remote) =>
        !remote && !settings.FreeMode && !settings.FreeModeAsked && confirmedEffect is ActionEffect.Send or ActionEffect.Post or ActionEffect.Delete;
    public bool Begin(UiSettings settings, ActionEffect confirmedEffect, bool remote)
    {
        Cancel();
        if (!Eligible(settings, confirmedEffect, remote)) return false;
        expires = now().AddSeconds(20); return true;
    }
    public void Cancel() => expires = DateTimeOffset.MinValue;
    public FreeModeAnswer Answer(string text, bool remote)
    {
        if (remote || !Pending) { Cancel(); return FreeModeAnswer.None; }
        var answer = ExecutionConsent.ParseAnswer(text); Cancel(); return answer;
    }
}
