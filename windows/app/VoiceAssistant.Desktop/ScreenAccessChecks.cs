using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace VoiceAssistant.Desktop;

internal static class ScreenAccessChecks
{
    static void Require(bool value,string why){if(!value)throw new Exception(why);}
    static async Task Reject(Func<Task> action){try{await action();}catch(Exception ex)when(ex is AgentStoppedException or OperationCanceledException or InvalidOperationException){return;}throw new Exception("Disallowed input was dispatched");}
    internal static async Task<int> RunAsync(List<object> rows)
    {
        int failed=0;
        async Task Check(string name,Func<Task> run){try{await run();Require(!ScreenAccess.Full,"Capability leaked into caller");rows.Add(new{name,pass=true});}catch(Exception ex){failed++;rows.Add(new{name,pass=false,error=ex.ToString()});}}
        await Check("Screen permission defaults safe and grants protected processes and password input only in a live full request",async()=>
        {
            Require(!ScreenAccess.Full && !ScreenAccess.AllowsPasswordInput && !ScreenAccess.AllowsProtected(true) && !ScreenAccess.AllowsProtected(false,true),"Unscoped authority");
            foreach(var mode in new[]{"safe","full"})using(ScreenAccess.Begin(mode))
            {
                foreach(var app in new[]{"cmd","powershell","Code","CredentialUIBroker","KakaoTalk"})Require(ScreenAccess.AllowsProtected(WindowsActions.IsProtectedProcess(app))==(mode=="full"),"Incorrect app boundary: "+app);
                Require(ScreenAccess.AllowsPasswordInput==(mode=="full"),"Incorrect password boundary");await Task.Yield();
            }
        });
        await Check("Nested screen permissions flow through real background automation without widening a safe child",async()=>
        {
            using var full=ScreenAccess.Begin("full");var worker=new BrowserAutomationWork();
            Require(await worker.RunAsync(_=>Task.FromResult(ScreenAccess.Full),CancellationToken.None),"MTA task lost full permission");
            using(ScreenAccess.Begin("safe")){await Task.Yield();Require(!await Task.Run(()=>ScreenAccess.Full),"Safe child inherited full");}
            Require(ScreenAccess.Full,"Parent scope not restored");
        });
        await Check("Concurrent full and safe requests do not share desktop capabilities",async()=>
        {
            var gate=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);int ready=0;
            var work=Enumerable.Range(0,32).Select(i=>Task.Run(async()=>
            {
                var expected=i%2==0;using var scope=ScreenAccess.Begin(expected?"full":"safe");if(Interlocked.Increment(ref ready)==32)gate.TrySetResult();await gate.Task;
                for(int n=0;n<6;n++){await Task.Yield();Require(ScreenAccess.Full==expected,"Cross-request privilege leak");}
            })).ToArray();await Task.WhenAll(work).WaitAsync(TimeSpan.FromSeconds(10));
        });
        await Check("Transport callbacks can bind only a live captured request and restore the caller context",async()=>
        {
            using var request=ScreenAccess.Begin("full");Task<bool> callback;
            using(ExecutionContext.SuppressFlow())callback=Task.Run(async()=>{Require(!ScreenAccess.Full,"Callback inherited ambient access");using(request.Enter()){await Task.Yield();Require(ScreenAccess.Full,"Captured request not restored");}return !ScreenAccess.Full;});
            Require(await callback,"Binding leaked into transport caller");request.Dispose();await Reject(()=>Task.Run(()=>{using var _=request.Enter();}));
        });
        await Check("Revoked screen requests cannot dispatch late password work even if a worker retains ExecutionContext",async()=>
        {
            var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);int writes=0;Task worker;
            using(var scope=ScreenAccess.Begin("full"))worker=Task.Run(async()=>{await release.Task;Require(!ScreenAccess.Full,"Ended lease remained full");await Reject(()=>SensitiveFieldWriter.ReplaceAsync("fixture",()=>{},()=>writes++,()=>writes++,_=>writes++,CancellationToken.None));await Reject(()=>Task.Run(()=>ScreenAccess.Require("safe")));});
            release.SetResult();await worker;Require(writes==0,"Late mutation occurred");
        });
        await Check("Cancelling an active screen request removes full permission before further input",async()=>
        {
            using var stop=new CancellationTokenSource();using var scope=ScreenAccess.Begin("full",stop.Token);Require(ScreenAccess.Full,"Full request unavailable");stop.Cancel();Require(!ScreenAccess.Full,"Cancelled request stayed full");
            await Reject(()=>Task.Run(()=>{using var _=scope.Enter();}));
        });
        await Check("Safe screen lists hide password fields and full lists expose only redacted editable metadata",async()=>
        {
            var driver=new SecretDriver();var controls=new DesktopControls(driver);
            var safe=await controls.ListAsync("",0,CancellationToken.None);Require(safe.Controls.All(x=>!x.Sensitive),"Password leaked into safe list");
            using var scope=ScreenAccess.Begin("full");var page=await controls.ListAsync("",0,CancellationToken.None);var secret=page.Controls.Single(x=>x.Sensitive);var data=ControlObservation.Data(page);
            Require(secret.Editable && secret.State=="contents-hidden" && secret.SelectionStart==null && !data.Contains(SecretDriver.Unreadable),"Password contents exposed");
            var action=new AssistantAction("fill_field",SecretDriver.Unreadable,Target:secret.Id);Require(!controls.Describe(action).Contains(SecretDriver.Unreadable),"Confirmation exposed password");
            await controls.FillAsync(secret.Id,"synthetic replacement",CancellationToken.None);Require(driver.Output=="synthetic replacement" && driver.Reads==2,"Password input did not use the observed field");
        });
        await Check("Observed full screen references cannot be reused after switching to safe permission",async()=>
        {
            var driver=new SecretDriver();var controls=new DesktopControls(driver);string id;
            using(ScreenAccess.Begin("full")){id=(await controls.ListAsync("",0,CancellationToken.None)).Controls.Single(x=>x.Sensitive).Id;}
            await Reject(()=>controls.FillAsync(id,"must-not-write",CancellationToken.None));Require(driver.Writes==0,"Stale full reference dispatched");
        });
        await Check("Password replacement preserves UTF16 boundaries and stops before the next chunk after focus changes",async()=>
        {
            using var scope=ScreenAccess.Begin("full");var parts=new List<string>();var text=new string('x',63)+"😀"+new string('y',100);int selects=0;bool focused=true;
            await SensitiveFieldWriter.ReplaceAsync(text,()=>Require(focused,"Focus changed"),()=>selects++,()=>{},part=>{_ = new UTF8Encoding(false,true).GetBytes(part);parts.Add(part);},CancellationToken.None);
            Require(selects==1 && string.Concat(parts)==text,"Incomplete or split Unicode password");parts.Clear();focused=true;
            await Reject(()=>SensitiveFieldWriter.ReplaceAsync(text,()=>{if(!focused)throw new AgentStoppedException("focus changed");},()=>{},()=>{},part=>{parts.Add(part);focused=false;},CancellationToken.None));Require(parts.Count==1,"Continued typing into a changed field");
        });
        await Check("Password writes reject safe mode and stop immediately on cancellation without reading previous contents",async()=>
        {
            int writes=0;await Reject(()=>SensitiveFieldWriter.ReplaceAsync("secret",()=>{},()=>writes++,()=>writes++,_=>writes++,CancellationToken.None));Require(writes==0,"Safe replacement mutated a field");
            using var stop=new CancellationTokenSource();using var scope=ScreenAccess.Begin("full",stop.Token);await Reject(()=>SensitiveFieldWriter.ReplaceAsync(new string('z',200),()=>{},()=>{},()=>{},_=>{writes++;stop.Cancel();},stop.Token));Require(writes==1,"Cancellation did not stop password dispatch");
        });
        await Check("Plain typing flattens line breaks only in safe mode and full preserves the original text",()=>
        {
            const string raw="line1\r\nline2\u2028line3\u2029line4";Require(ScreenAccess.TypedText(raw)=="line1 line2 line3 line4","Safe newline policy differs from original");
            using(ScreenAccess.Begin("full"))Require(ScreenAccess.TypedText(raw)==raw,"Full typing changed user text");return Task.CompletedTask;
        });
        return failed;
    }
    sealed class SecretDriver:IDesktopControlDriver
    {
        internal const string Unreadable="MUST_NOT_READ_PASSWORD";
        internal int Reads,Writes;internal string Output="";
        public Task<DesktopControlSnapshot> ReadAsync(CancellationToken token){Reads++;return Task.FromResult(new DesktopControlSnapshot("fixture","fixture",new[]{new DesktopControl("secret","Edit","Password","Account","",true,Unreadable,"metadata",3,2,true),new DesktopControl("plain","Edit","Name","Account","",true,"","metadata")}));}
        public Task PressAsync(DesktopControl control,CancellationToken token)=>throw new Exception("Unexpected press");
        public Task InsertAsync(DesktopControl control,string text,CancellationToken token)=>throw new Exception("Password cursor must not be read");
        public Task FillAsync(DesktopControl control,string text,CancellationToken token)=>SensitiveFieldWriter.ReplaceAsync(text,()=>{},()=>{Writes++;Output="";},()=>Output="",part=>Output+=part,token);
    }
}
