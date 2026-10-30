using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VoiceAssistant.Desktop;

internal static class BrainAccessAppChecks
{
    internal static int Run()
    {
        if(Path.GetFullPath(AppPaths.Home).TrimEnd('\\').Equals(Path.GetFullPath(AppPaths.StandardHome).TrimEnd('\\'),StringComparison.OrdinalIgnoreCase))return 2;
        var rows=new List<object>();var windows=new List<Window>();int failed=0,saves=0;
        var current=new UiSettings{Name="설정",Speak=false,Desktop=new(){SetupComplete=true,StartupListening="off"}};SettingsStore.Save(current);
        var state=Path.Combine(AppPaths.Home,"listening-state.json");File.WriteAllText(state,"{\"Requested\":false}");var stateBefore=File.ReadAllBytes(state);
        var view=new MainWindow();windows.Add(view);view.LoadSettings(current);
        var input=(ComboBox)view.FindName("BrainAccessInput");var provider=(ComboBox)view.FindName("ProviderInput");var section=(StackPanel)view.FindName("BrainAccessFields");var help=(TextBlock)view.FindName("BrainAccessHelp");
        void Require(bool value,string why){if(!value)throw new Exception(why);}
        string Tag(ComboBox choice)=>(choice.SelectedItem as ComboBoxItem)?.Tag?.ToString()??"";
        void Select(ComboBox choice,string value)=>choice.SelectedItem=choice.Items.Cast<ComboBoxItem>().Single(x=>x.Tag?.ToString()==value);
        view.SettingsSaved+=next=>{SettingsStore.Save(next);current=next;view.LoadSettings(next);saves++;return null;};
        void Check(string name,Action run)
        {
            try{run();Require(windows.All(x=>!x.IsVisible && PresentationSource.FromVisual(x)==null),"A native test window was created");Require(File.ReadAllBytes(state).SequenceEqual(stateBefore) && WhisperEngine.Shared.ProcessId==0,"Permission UI changed listening");rows.Add(new{name,pass=true});}
            catch(Exception ex){failed++;rows.Add(new{name,pass=false,error=ex.ToString()});}
        }
        try
        {
            Check("Hidden Claude permission editor defaults safe and selecting full changes only the draft",()=>
            {
                Require(Tag(input)=="safe" && section.Visibility==Visibility.Visible && help.Text.Contains("웹 검색"),"Default permission or help missing");var before=File.ReadAllBytes(AppPaths.ConfigFile);Select(input,"full");
                Require(help.Text.Contains("휴대폰 포함") && help.Text.Contains("되돌리기") && SettingsStore.Load().BrainAccess=="safe" && saves==0 && File.ReadAllBytes(AppPaths.ConfigFile).SequenceEqual(before),"Draft silently applied authority");
            });
            Check("Saving full and safe permission uses actual settings persistence and backups without enabling speech",()=>
            {
                foreach(var value in new[]{"full","safe","full"}){var before=File.ReadAllBytes(AppPaths.ConfigFile);Select(input,value);view.SaveSettingsForCheck();Require(SettingsStore.Load().BrainAccess==value && File.ReadAllBytes(AppPaths.ConfigFile+".backup").SequenceEqual(before),"Saved permission or backup lost");}
                Require(saves==3 && !current.Speak && current.Desktop.StartupListening=="off" && current.Name=="설정","Permission changed unrelated settings");
            });
            Check("Switching AI providers hides unsupported permission controls while retaining the saved Claude choice",()=>
            {
                foreach(var id in new[]{"codex","ollama","aside","combo","claude"}){Select(provider,id);Require(section.Visibility==(id is "claude" or "combo"?Visibility.Visible:Visibility.Collapsed) && Tag(input)=="full","Provider switch changed permission");}
                Select(provider,"codex");view.SaveSettingsForCheck();Require(SettingsStore.Load().BrainAccess=="full","Saving Codex erased Claude permission");Select(provider,"claude");
            });
            Check("Invalid and rejected permission saves preserve both current settings and backup",()=>
            {
                var before=File.ReadAllBytes(AppPaths.ConfigFile);var backup=File.ReadAllBytes(AppPaths.ConfigFile+".backup");int count=saves;input.SelectedIndex=-1;view.SaveSettingsForCheck();
                Require(saves==count && File.ReadAllBytes(AppPaths.ConfigFile).SequenceEqual(before) && File.ReadAllBytes(AppPaths.ConfigFile+".backup").SequenceEqual(backup),"Invalid choice saved");view.LoadSettings(current);
                var rejected=new MainWindow();windows.Add(rejected);rejected.LoadSettings(current with{BrainProvider="claude"});rejected.SettingsSaved+=_=>"fixture-save-rejected";Select(rejected.BrainAccessInput,"safe");rejected.SaveSettingsForCheck();
                Require(File.ReadAllBytes(AppPaths.ConfigFile).SequenceEqual(before) && rejected.SettingsFeedback.Text=="fixture-save-rejected","Rejected save changed persisted permission");
            });
            Check("Windows permission import stays unchanged until the actual review checkbox is selected",()=>Review(false));
            Check("Mac permission import uses the same explicit review checkbox without changing Windows devices",()=>Review(true));
            void Review(bool mac)
            {
                var saved=SettingsStore.Load();var safe=saved with{BrainAccess="safe"};var imported=mac?MacSettingsTransfer.Parse("{\"brainAccess\":\"full\"}",safe).Settings:safe with{BrainAccess="full"};
                var review=new SettingsImportWindow(imported,safe,null,!mac);windows.Add(review);var before=File.ReadAllBytes(AppPaths.ConfigFile);
                Require(!review.ChangeDescription.Contains("Claude 작업 권한") && review.Result==null,"Permission change applied by default");review.SelectConnections(true);Require(!review.ChangeDescription.Contains("Claude 작업 권한"),"Connection import granted permission");
                review.SelectPermissions(true);Require(review.ChangeDescription.Contains("Claude 작업 권한") && review.ChangeDescription.Contains("전체 권한"),"Review hid full access");var selected=review.AcceptSelection();
                Require(selected.BrainAccess=="full" && selected.Device==safe.Device && selected.Desktop.StartupListening==safe.Desktop.StartupListening && File.ReadAllBytes(AppPaths.ConfigFile).SequenceEqual(before),"Review applied data before the caller saved");
            }
            Check("English permission labels keep safe and full tags and preserve user names",()=>
            {
                view.LoadSettings(current with{BrainProvider="claude",Language="en"});Require(AutomationProperties.GetName(input)=="Claude tool access" && input.Items.Cast<ComboBoxItem>().Any(x=>x.Tag?.ToString()=="full" && x.Content?.ToString()=="Full · commands, files and web"),"English labels changed tags");
                Require(help.Text.Contains("including phone requests") && view.NameInput.Text=="설정" && SettingsStore.Load().Language=="ko","Language display changed stored data");
            });
            Check("Claude full permission guidance renders offscreen without loading an engine or creating a native window",()=>
            {
                ((Panel)section.Parent).Children.Remove(section);var canvas=new Grid{Background=Brushes.White,Margin=new Thickness(20)};canvas.Children.Add(section);canvas.Measure(new Size(660,310));canvas.Arrange(new Rect(0,0,660,310));canvas.UpdateLayout();
                Require(section.Visibility==Visibility.Visible && input.ActualHeight>20 && help.ActualHeight>30,"Permission guidance missing: "+help.ActualHeight);var bmp=new RenderTargetBitmap(660,310,96,96,PixelFormats.Pbgra32);bmp.Render(canvas);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bmp));using var output=File.Create(Path.Combine(AppPaths.Home,"brain-access-en.png"));png.Save(output);Require(output.Length>5000,"Empty permission image");
            });
        }
        finally{foreach(var window in windows)window.Close();UiText.Apply("ko");}
        File.WriteAllText(Path.Combine(AppPaths.Home,"brain-access-app-checks.json"),JsonSerializer.Serialize(new{total=rows.Count,failed,visibleWindows=0,microphoneCapture=false,speakerPlayback=false,checks=rows},SettingsStore.Json));return failed==0?0:1;
    }
}
