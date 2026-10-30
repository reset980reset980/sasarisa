using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace VoiceAssistant.Desktop;

internal static class ScreenAccessAppChecks
{
    internal static async Task<int> RunAsync()
    {
        if(Path.GetFullPath(AppPaths.Home).TrimEnd('\\').Equals(Path.GetFullPath(AppPaths.StandardHome).TrimEnd('\\'),StringComparison.OrdinalIgnoreCase))return 2;
        var rows=new List<object>();int failed=0;
        var dispatcher=Application.Current.Dispatcher;
        void Require(bool value,string why){if(!value)throw new Exception(why);}
        async Task Check(string name,Func<Task> run)
        {try{await run();Require(!ScreenAccess.Full && WhisperEngine.Shared.ProcessId==0,"Scope leaked or an engine started");rows.Add(new{name,pass=true});}catch(Exception ex){failed++;rows.Add(new{name,pass=false,error=ex.ToString()});}}
        await Check("Real WPF dispatcher and background automation retain the active request permission",async()=>
        {
            using var request=ScreenAccess.Begin("full");
            var result=await Task.Run(async()=>await dispatcher.InvokeAsync(()=>{ScreenAccess.Require("full");return ScreenAccess.Full;}));Require(result,"Dispatcher lost permission");
            using(ScreenAccess.Begin("safe"))Require(!await Task.Run(async()=>await dispatcher.InvokeAsync(()=>ScreenAccess.Full)),"Safe dispatcher callback inherited full permission");
        });
        await Check("A transport callback without ExecutionContext can explicitly bind a request before dispatching to WPF",async()=>
        {
            using var request=ScreenAccess.Begin("full");Task<bool> work;
            using(ExecutionContext.SuppressFlow())work=Task.Run(async()=>{Require(!ScreenAccess.Full,"Transport unexpectedly inherited a capability");using(request.Enter())return await dispatcher.InvokeAsync(()=>{ScreenAccess.Require("full");return ScreenAccess.AllowsPasswordInput;});});
            Require(await work,"Explicit WPF callback binding failed");
        });
        await Check("A queued WPF operation cannot mutate after its full request has ended",async()=>
        {
            int writes=0;Task pending;
            using(ScreenAccess.Begin("full"))pending=dispatcher.InvokeAsync(()=>{try{ScreenAccess.Require("full");writes++;}catch(AgentStoppedException){}}).Task;
            await pending;Require(writes==0,"Revoked dispatcher operation mutated state");
        });
        File.WriteAllText(Path.Combine(AppPaths.Home,"screen-access-app-checks.json"),JsonSerializer.Serialize(new{total=rows.Count,failed,visibleWindows=0,microphoneCapture=false,speakerPlayback=false,checks=rows},SettingsStore.Json));return failed==0?0:1;
    }
}
