using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VoiceAssistant.Desktop;

internal static class BrainAccessNativeChecks
{
    internal static async Task<int> RunAsync()
    {
        if(Path.GetFullPath(AppPaths.Home).TrimEnd('\\').Equals(Path.GetFullPath(AppPaths.StandardHome).TrimEnd('\\'),StringComparison.OrdinalIgnoreCase))return 2;
        var rows=new List<object>();int failed=0;Directory.CreateDirectory(Path.Combine(AppPaths.Home,"work"));
        void Require(bool value,string why){if(!value)throw new Exception(why);}
        foreach(var mode in new[]{"safe","full","text-only"})
        {
            var name="Installed Claude advertises and executes the configured "+mode+" tool profile";
            try
            {
                var marker="SASARISA_ACCESS_"+Guid.NewGuid().ToString("N");
                var info=Brain.ClaudeInfo(Brain.ClaudeBinary()??throw new Exception("Claude CLI missing"),new(){BrainAccess=mode=="safe"?"safe":"full",Speak=false},textOnly:mode=="text-only");
                info.ArgumentList.Add("--max-turns");info.ArgumentList.Add("4");
                using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(120));using var process=new Process{StartInfo=info};process.Start();
                var stdout=process.StandardOutput.ReadToEndAsync(timeout.Token);var stderr=process.StandardError.ReadToEndAsync(timeout.Token);
                var prompt=mode=="full"?"Use Bash exactly once to run this exact read-only command: printf '"+marker+"'. Do not run any other command, read files, use the web or make changes. Then return JSON with reply equal to the command output and actions an empty array.":"Do not call any tools, access files, use the web or run commands. Return JSON with reply exactly "+marker+" and actions an empty array.";
                try{await process.StandardInput.WriteAsync(prompt.AsMemory(),timeout.Token);process.StandardInput.Close();await process.WaitForExitAsync(timeout.Token);}
                catch{if(!process.HasExited)process.Kill(true);throw;}
                var output=await stdout;var error=await stderr;Require(process.ExitCode==0,"CLI failed: "+error[..Math.Min(error.Length,700)]);Require(output.Length<1_000_000,"Unexpected CLI output size");
                string[]? advertised=null;var calls=new List<(string Name,string Command)>();JsonElement? result=null;
                foreach(var line in output.Split('\n'))
                {
                    if(!line.TrimStart().StartsWith('{'))continue;using var doc=JsonDocument.Parse(line);var item=doc.RootElement;
                    if(item.TryGetProperty("type",out var type) && type.GetString()=="system" && item.TryGetProperty("tools",out var tools))advertised=tools.EnumerateArray().Select(x=>x.GetString()??"").ToArray();
                    if(type.ValueKind==JsonValueKind.String && type.GetString()=="assistant" && item.TryGetProperty("message",out var message) && message.TryGetProperty("content",out var blocks))foreach(var block in blocks.EnumerateArray())
                        if(block.TryGetProperty("type",out var blockType) && blockType.GetString()=="tool_use")calls.Add((block.GetProperty("name").GetString()??"",block.TryGetProperty("input",out var input) && input.TryGetProperty("command",out var command)?command.GetString()??"":""));
                    if(type.ValueKind==JsonValueKind.String && type.GetString()=="result")result=item.Clone();
                }
                Require(advertised!=null,"CLI init did not report tool availability");var natives=advertised!.Where(x=>x is not ("StructuredOutput" or "EndConversation")).Order().ToArray();
                Require(natives.SequenceEqual((mode=="full"?BrainAccess.FullTools.Split(','):mode=="safe"?new[]{"WebSearch"}:Array.Empty<string>()).Order()),"CLI tool availability differs from the selected profile: "+string.Join(",",natives));
                var bash=calls.Where(x=>x.Name=="Bash").ToArray();Require(mode=="full"?bash.Length==1 && bash[0].Command=="printf '"+marker+"'":bash.Length==0,"Unexpected command execution");Require(calls.All(x=>x.Name is "Bash" or "StructuredOutput" or "EndConversation"),"Unexpected external tool call");
                Require(result!=null && !(result.Value.TryGetProperty("is_error",out var err) && err.ValueKind==JsonValueKind.True),"No completed result");
                var final=result!.Value;if(final.TryGetProperty("structured_output",out var structured)){Require(structured.GetProperty("reply").GetString()==marker && structured.GetProperty("actions").GetArrayLength()==0,"Incorrect structured result");}
                else{using var parsed=JsonDocument.Parse(final.GetProperty("result").GetString()??"");Require(parsed.RootElement.GetProperty("reply").GetString()==marker && parsed.RootElement.GetProperty("actions").GetArrayLength()==0,"Incorrect final result");}
                rows.Add(new{name,pass=true,tools=natives,bashCalls=bash.Length});
            }
            catch(Exception ex){failed++;rows.Add(new{name,pass=false,error=ex.ToString()});}
        }
        File.WriteAllText(Path.Combine(AppPaths.Home,"brain-access-native-checks.json"),JsonSerializer.Serialize(new{total=rows.Count,failed,visibleWindows=0,microphoneCapture=false,speakerPlayback=false,checks=rows},SettingsStore.Json));return failed==0?0:1;
    }
}
