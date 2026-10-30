using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace VoiceAssistant.Desktop;

internal static class BrainAccessChecks
{
    static void Require(bool value,string message){if(!value)throw new Exception(message);}
    static void Reject(Action action){try{action();}catch(ArgumentException){return;}throw new Exception("Invalid access accepted");}
    internal static int Run(List<object> rows)
    {
        int failed=0;
        void Check(string name,Action run){try{run();rows.Add(new{name,pass=true});}catch(Exception ex){failed++;rows.Add(new{name,pass=false,error=ex.ToString()});}}
        Check("Claude access defaults stay safe for legacy settings and invalid values cannot reach CLI arguments",()=>
        {
            Require(JsonSerializer.Deserialize<UiSettings>("{}")!.BrainAccess=="safe","Legacy default changed");
            foreach(var value in new[]{null,"","SAFE","full --tools default","full\nBash","Full"})
            {
                var settings=new UiSettings{BrainAccess=value!};Reject(()=>SettingsStore.Validate(settings));Reject(()=>Brain.ClaudeInfo("fixture.exe",settings));
                Reject(()=>MacSettingsTransfer.Parse(JsonSerializer.Serialize(new{brainAccess=value}),new()));
            }
            Reject(()=>MacSettingsTransfer.Parse("{\"brainAccess\":true}",new()));
        });
        Check("Claude safe exposes only web search and app MCP while full adds the original six native tools",()=>
        {
            var mcp=new McpEndpoint("fixture-pipe",new string('a',64));
            foreach(var access in new[]{"safe","full"})
            {
                var args=Brain.ClaudeInfo("fixture.exe",new(){BrainAccess=access},mcp).ArgumentList.ToArray();string Next(string key)=>args[Array.IndexOf(args,key)+1];
                var tools=Next("--tools").Split(',');Require(tools.SequenceEqual(access=="full"?new[]{"Bash","Read","Glob","Grep","WebSearch","WebFetch"}:new[]{"WebSearch"}),"Unexpected native authority");
                Require(args.Contains("--restricted")== (access=="safe") && Next("--mcp-config")==mcp.ClaudeConfig && args.Count(x=>x=="--mcp-config")==1,"Wrong mode or MCP isolation");
                Require(Next("--allowedTools").Split(',').SequenceEqual(tools.Append("mcp__sasarisa__*")),"Allowlist differs from advertised tools");
                Require(Next("--setting-sources")=="" && Next("--permission-mode")=="default" && args.Contains("--no-session-persistence") && args.Contains("--strict-mcp-config") && !args.Any(x=>x.Contains("skip-permissions")),"Global settings or permission bypass enabled");
                Require(new[]{"SendMessage","ToolSearch","Task","Agent","Write","Edit","PowerShell"}.All(Next("--disallowedTools").Split(',').Contains),"Extra tools allowed");
            }
        });
        Check("Full Claude settings still produce a tool-free isolated text-refinement process",()=>
        {
            var settings=new UiSettings{BrainAccess="full"};var mcp=new McpEndpoint("fixture",new string('b',64));
            var args=Brain.ClaudeInfo("fixture.exe",settings,mcp,textOnly:true).ArgumentList.ToArray();string Next(string key)=>args[Array.IndexOf(args,key)+1];
            Require(args.Contains("--restricted") && Next("--tools")=="" && Next("--mcp-config")=="{\"mcpServers\":{}}" && !args.Contains("--allowedTools"),"Text refinement inherited tool authority");
            Require(settings.BrainAccess=="full","Purpose-specific override changed saved preference");
        });
        Check("AI tool permission import requires its own explicit selection independent of connections or devices",()=>
        {
            foreach(var currentAccess in new[]{"safe","full"})
            {
                var current=new UiSettings{BrainAccess=currentAccess,Speak=false,Desktop=new(){StartupListening="off"}};var original=JsonSerializer.Serialize(current);
                var incoming=SettingsTransfer.Parse(SettingsTransfer.Serialize(current with{BrainAccess=currentAccess=="safe"?"full":"safe"}));
                foreach(var connection in new[]{false,true})foreach(var devices in new[]{false,true})
                {
                    Require(SettingsTransfer.Prepare(incoming,current,connection,devices).BrainAccess==currentAccess,"Import silently changed permission");
                    var selected=SettingsTransfer.Prepare(incoming,current,connection,devices,true);Require(selected.BrainAccess==incoming.BrainAccess && !selected.Speak && selected.Desktop.StartupListening=="off","Explicit import lost access or changed audio preference");
                }
                Require(SettingsTransfer.Copy(incoming).BrainAccess==incoming.BrainAccess && JsonSerializer.Serialize(current)==original,"Import/copy mutated source");
                Require(SettingsTransfer.Describe(current,incoming).Contains("Claude 작업 권한"),"Review hid permission change");
            }
        });
        Check("Mac safe and full values are converted but omitted access and rejected values preserve the Windows profile",()=>
        {
            var current=new UiSettings{BrainAccess="full"};var original=JsonSerializer.Serialize(current);
            foreach(var value in new[]{"safe","full"})
            {
                var converted=MacSettingsTransfer.Parse(JsonSerializer.Serialize(new{brainAccess=value}),current);
                Require(converted.Settings.BrainAccess==value && converted.Notices.Any(x=>x.Contains("AI 권한")),"Mac access or opt-in notice lost");
            }
            Require(MacSettingsTransfer.Parse("{\"assistantName\":\"설정\"}",current).Settings.BrainAccess=="full" && JsonSerializer.Serialize(current)==original,"Partial import reset current access");
        });
        Check("Combo keeps Claude access without expanding Codex permissions or app confirmation rules",()=>
        {
            var settings=new UiSettings{BrainProvider="combo",BrainAccess="full"};
            foreach(var provider in new[]{"claude","codex","aside"})Require(ComboBrain.Resolve(settings,"code",new[]{provider}).Settings.BrainAccess=="full","Combo fallback lost access");
            var safe=Brain.CodexInfo("fixture.exe","schema.json",settings with{BrainProvider="codex",BrainAccess="safe"}).ArgumentList;
            var full=Brain.CodexInfo("fixture.exe","schema.json",settings with{BrainProvider="codex"}).ArgumentList;Require(safe.SequenceEqual(full),"Claude option expanded Codex authority");
            foreach(var access in new[]{"safe","full"})Require(ExecutionConsent.Decide(new UiSettings{BrainAccess=access,FreeMode=true},new("press_control","fixture"),true,ActionEffect.Payment,false)==ConsentDecision.Ask,"App payment confirmation bypassed");
        });
        return failed;
    }
}
