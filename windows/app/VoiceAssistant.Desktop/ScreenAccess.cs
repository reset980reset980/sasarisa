using System;
using System.Threading;
using System.Threading.Tasks;

namespace VoiceAssistant.Desktop;

// A request owns its capability. ExecutionContext copies only a reference to
// that lease; disposing/cancelling the request revokes late UIA workers too.
internal static class ScreenAccess
{
    static readonly AsyncLocal<Lease?> current = new();
    internal static string Mode => current.Value is { Valid:true } active ? active.Mode : "safe";
    internal static bool Full => Mode == "full";
    internal static Lease Begin(string mode,CancellationToken token=default)
    {
        BrainAccess.Validate(mode);token.ThrowIfCancellationRequested();return new Lease(mode,token,current.Value is {Valid:true} parent ? parent : null);
    }
    internal static void EnsureActive(){if(current.Value is {Valid:false})throw new AgentStoppedException(UiText.Get("이미 끝난 화면 요청이에요."));}
    internal static void Require(string observedMode)
    {
        EnsureActive();
        if(observedMode!=Mode)throw new AgentStoppedException(UiText.Get("화면 작업 권한이 바뀌어 중단했어요. 대상을 다시 확인하세요."));
    }
    internal static bool AllowsProtected(bool protectedApp,bool ownApp=false) => Full || !(protectedApp || ownApp);
    internal static bool AllowsPasswordInput => Full;
    internal static string TypedText(string text) => Full ? text : System.Text.RegularExpressions.Regex.Replace(text,@"[\r\n\u2028\u2029]+"," ");
    internal static string Prompt(string mode)
    {
        BrainAccess.Validate(mode);
        return mode=="full" ? "비서 화면 권한은 full이다. Claude/Codex/API의 비서 화면 도구로 보호 앱과 암호 입력칸에도 작업할 수 있다. Windows 계정 권한·대상 창/항목 ID·취소·앱 실행 확인 규칙은 그대로 적용된다. 암호 입력칸은 Sensitive=true이며 내용을 읽거나 자동으로 되돌릴 수 없다. 암호의 전체 교체는 fill_field, 현재 위치에 입력은 type을 사용한다. 암호 입력 결과의 내용을 확인했다고 주장하지 않는다.\n"
            : "비서 화면 권한은 safe다. 보호 앱·암호 입력칸은 조작할 수 없다. type의 줄바꿈은 공백으로 전달한다.\n";
    }
    internal sealed class Lease : IDisposable
    {
        readonly CancellationToken token;
        readonly Lease? parent;
        int live=1;
        internal string Mode { get; }
        internal bool Valid => Volatile.Read(ref live)!=0 && !token.IsCancellationRequested && (parent==null || parent.Valid);
        internal Lease(string mode,CancellationToken token,Lease? parent){Mode=mode;this.token=token;this.parent=parent;current.Value=this;}
        internal IDisposable Enter()
        {
            token.ThrowIfCancellationRequested();if(!Valid)throw new AgentStoppedException("이미 끝난 화면 요청이에요.");
            var before=current.Value;current.Value=this;return new Binding(this,before);
        }
        public void Dispose(){Interlocked.Exchange(ref live,0);if(ReferenceEquals(current.Value,this))current.Value=parent;}
    }
    sealed class Binding(Lease active,Lease? before):IDisposable
    {public void Dispose(){if(ReferenceEquals(current.Value,active))current.Value=before;}}
}

// Password fields have no readable before/after text. Dispatch only the explicit
// replacement, recheck the bound target for every chunk, and never read a value.
internal static class SensitiveFieldWriter
{
    internal static async Task ReplaceAsync(string text,Action guard,Action selectAll,Action clear,Action<string> type,CancellationToken token)
    {
        if(!ScreenAccess.AllowsPasswordInput)throw new AgentStoppedException(UiText.Get("암호 입력은 전체 작업 권한에서만 허용합니다."));
        if(text.Length>6000)throw new ArgumentException("입력할 내용은 6,000자 이내여야 해요.");
        void Check(){token.ThrowIfCancellationRequested();if(!ScreenAccess.AllowsPasswordInput)throw new AgentStoppedException("암호 입력 권한이 끝났어요.");guard();}
        Check();selectAll();
        if(text.Length==0){Check();clear();}
        for(int offset=0;offset<text.Length;)
        {
            Check();int count=Math.Min(64,text.Length-offset);
            if(offset+count<text.Length && char.IsHighSurrogate(text[offset+count-1]) && char.IsLowSurrogate(text[offset+count]))count--;
            type(text.Substring(offset,count));offset+=count;await Task.Yield();
        }
        Check();
    }
}
