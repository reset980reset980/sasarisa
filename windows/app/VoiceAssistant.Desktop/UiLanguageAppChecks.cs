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

internal static class UiLanguageAppChecks
{
    static IEnumerable<T> Children<T>(DependencyObject root) where T:DependencyObject
    {
        if(root is T item) yield return item;
        foreach(var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>()) foreach(var result in Children<T>(child)) yield return result;
    }
    internal static int Run()
    {
        if(Path.GetFullPath(AppPaths.Home).TrimEnd('\\').Equals(Path.GetFullPath(AppPaths.StandardHome).TrimEnd('\\'),StringComparison.OrdinalIgnoreCase)) return 2;
        var rows=new List<object>();var windows=new List<Window>();int failed=0;
        var original=new UiSettings{Name="설정",BrainModel="대화",Speak=false,Desktop=new(){SetupComplete=true,StartupListening="off"}};
        SettingsStore.Save(original);var listening=Path.Combine(AppPaths.Home,"listening-state.json");File.WriteAllText(listening,"{\"Requested\":false}");var listeningBefore=File.ReadAllBytes(listening);
        void Require(bool value,string why){if(!value)throw new Exception(why);}
        T Track<T>(T view) where T:Window{windows.Add(view);return view;}
        MainWindow Main(string language="ko"){var view=Track(new MainWindow());view.LoadSettings(original with{Language=language});return view;}
        void Click(MainWindow view,string name)=>((Button)view.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        void Hidden()
        {
            foreach(var view in windows)Require(!view.IsVisible && PresentationSource.FromVisual(view)==null,"A native window was created");
            Require(File.ReadAllBytes(listening).SequenceEqual(listeningBefore) && WhisperEngine.Shared.ProcessId==0,"UI translation changed listening or started an engine");
        }
        void Check(string name,Action run){try{run();Hidden();rows.Add(new{name,pass=true});}catch(Exception ex){failed++;rows.Add(new{name,pass=false,error=ex.ToString()});}}
        void RenderWindow(Window view,string file,double width=1080,double height=850)
        {
            var content=(FrameworkElement)view.Content;view.Content=null;
            var canvas=new Grid{Background=(Brush)Application.Current.FindResource("CanvasBrush")};canvas.Children.Add(content);canvas.Measure(new Size(width,height));canvas.Arrange(new Rect(0,0,width,height));canvas.UpdateLayout();
            var bitmap=new RenderTargetBitmap((int)width,(int)height,96,96,PixelFormats.Pbgra32);bitmap.Render(canvas);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using var stream=File.Create(Path.Combine(AppPaths.Home,file));png.Save(stream);Require(stream.Length>10000,"Empty UI image");
        }
        void Render(MainWindow view,string file,bool settings){Click(view,settings?"SettingsNav":"ChatNav");RenderWindow(view,file);}
        try
        {
            Check("UI catalogue is collision-free and all conversation languages follow original Korean or English UI rules",()=>
            {
                var before=File.ReadAllBytes(AppPaths.ConfigFile);Require(UiText.Catalog.Count>=700 && UiText.Catalog.Keys.Select(UiText.Key).Distinct().Count()==UiText.Catalog.Count,"Invalid catalogue");
                foreach(var language in ConversationLanguage.All){UiText.Apply(language.Code);Require(UiText.Get("설정")== (language.Code=="ko"?"설정":"Settings"),"Wrong fallback: "+language.Code);}
                bool rejected=false;try{UiText.Apply("unknown");}catch(ArgumentException){rejected=true;}
                Require(rejected && UiText.Language=="id" && File.ReadAllBytes(AppPaths.ConfigFile).SequenceEqual(before),"Invalid language or view-only operation changed settings");UiText.Apply("ko");
            });
            Check("English labels and accessibility names preserve assistant names model IDs typed text and recognition data",()=>
            {
                var view=Main("en");view.CommandInput.Text="설정";view.SetPartial("대화");
                Require(view.ListenButton.Content?.ToString()=="Start listening" && view.StatusPill.Text=="Microphone off" && view.AiUsageButton.Content?.ToString()=="AI usage · limits and 7-day history","Static labels missing");
                Require(AutomationProperties.GetName(view.NameInput)=="Assistant's name","Accessible label missing");
                Require(view.NameInput.Text=="설정" && view.BrandNameText.Text=="설정" && view.Title=="설정 · Windows" && view.ModelInput.Text=="대화" && view.CommandInput.Text=="설정" && view.PartialText.Text=="대화","User data translated");
                Click(view,"SettingsNav");Require(view.PageTitle.Text=="Make 설정 yours","Name argument translated");
            });
            Check("Changing language updates role chrome while keeping conversation bodies and raw progress data unchanged",()=>
            {
                var view=Main();view.AddMessage("user","설정");view.AddMessage("assistant","대화");view.SetStatus("처리 중","설정",false,true);UiText.Apply("en");
                Require(view.StatusTitle.Text=="Working" && view.StatusDetail.Text=="설정" && view.StopButton.IsEnabled,"Raw status changed");
                var bodies=Children<TextBox>(view.HistoryPanel).Select(x=>x.Text).ToArray();Require(bodies.Contains("설정") && bodies.Contains("대화"),"Message body translated");
                Require(Children<TextBlock>(view.HistoryPanel).Any(x=>x.Tag?.ToString()=="ui-role-user" && x.Text=="You") && Children<TextBlock>(view.HistoryPanel).Any(x=>x.Tag?.ToString()=="assistant-role" && x.Text=="설정"),"Role chrome or assistant name wrong");
                UiText.Apply("ko");Require(view.StatusDetail.Text=="설정" && view.StatusTitle.Text=="처리 중","Round trip lost state");
            });
            Check("UI phrases re-render with nested UI phrases and literal data arguments",()=>
            {
                var view=Main();view.SetStatus("처리 중",UiText.Copy("{0}에 연결하고 있어요.","설정"),false,true);UiText.Apply("en");
                Require(view.StatusDetail.Text=="Connecting to 설정.","Template did not update or changed data");
                Require(UiText.Copy("중지 버튼{0}로 멈출 수 있어요.",UiText.Copy("이나 {0}","Ctrl+Alt+Backspace")).Render()=="Use the Stop button or Ctrl+Alt+Backspace to stop.","Nested phrase failed");
                UiText.Apply("ko");Require(view.StatusDetail.Text=="설정에 연결하고 있어요.","Korean restoration failed");
            });
            Check("Unsaved language selection and a rejected save leave the saved and visible UI language unchanged",()=>
            {
                var before=File.ReadAllBytes(AppPaths.ConfigFile);var view=Main();view.LanguageInput.SelectedValue="en";
                Require(UiText.Korean && view.ListenButton.Content?.ToString()=="듣기 시작","Draft changed UI language");int calls=0;view.SettingsSaved+=_=>{calls++;return "fixture-save-rejected";};Click(view,"SaveSettingsButton");
                Require(calls==1 && view.SettingsFeedback.Text=="fixture-save-rejected" && UiText.Korean && File.ReadAllBytes(AppPaths.ConfigFile).SequenceEqual(before),"Rejected save changed data or language");
            });
            Check("Explicit language save persists without changing command tags or enabling audio",()=>
            {
                var view=Main();view.LanguageInput.SelectedValue="en";int calls=0;view.SettingsSaved+=next=>{calls++;SettingsStore.Save(next);view.LoadSettings(next);return null;};Click(view,"SaveSettingsButton");
                Require(calls==1 && SettingsStore.Load().Language=="en" && !UiText.Korean && view.ListenButton.Content?.ToString()=="Start listening","Saved language not applied");
                Require((view.ActivationInput.SelectedItem as ComboBoxItem)?.Tag?.ToString()=="voice" && (view.CodexEffortInput.SelectedItem as ComboBoxItem)?.Tag?.ToString()=="low" && !SettingsStore.Load().Speak && SettingsStore.Load().Name=="설정","Tags preferences or name changed");SettingsStore.Save(original);
            });
            Check("Overlay and phone translations preserve reply data and do not invoke connection or speech actions",()=>
            {
                _=Main("en");var overlay=Track(new OverlayWindow(false));overlay.SetContent("설정","대화",true);Require(overlay.OverlayText.Text=="대화" && overlay.OverlayTitle.Text=="설정" && overlay.OverlayState.Text=="Working on your request","Overlay data changed");
                int starts=0,stops=0,speech=0;var phone=Track(new PhoneWindow(_=>{starts++;return System.Threading.Tasks.Task.CompletedTask;},()=>{stops++;return System.Threading.Tasks.Task.CompletedTask;},()=>null,speechEnabled:()=>false,setSpeech:_=>{speech++;return null;}));
                Require(phone.StartButton.Content?.ToString()=="Enable connection" && phone.StatusText.Text=="Connection off" && phone.CodeText.Text=="—" && phone.AddressText.Text=="Enable the connection to see its address.","Phone labels or data incorrect");
                UiText.Apply("ko");UiText.Apply("en");Require(starts==0 && stops==0 && speech==0,"Language change invoked a connection or audio action");
            });
            Check("Tray labels follow saved language without executing commands and unsubscribe when disposed",()=>
            {
                UiText.Apply("ko");int clicks=0;var item=UiText.MenuItem("듣기 시작 / 중지",(_,_)=>clicks++);
                Require(item.Text=="듣기 시작 / 중지","Korean tray label missing");UiText.Apply("en");Require(item.Text=="Start / stop listening" && clicks==0,"Language change ran a command");
                item.PerformClick();Require(clicks==1,"Tray callback lost");item.Dispose();UiText.Apply("ko");Require(item.Text=="Start / stop listening" && clicks==1,"Disposed tray item retained a subscription");
            });
            Check("First-run and voice settings show English labels while preserving names and provider values",()=>
            {
                UiText.Apply("en");var before=File.ReadAllBytes(AppPaths.ConfigFile);int saves=0;var setup=Track(new SetupWindow(original,_=>{saves++;return null;}));var voice=Track(new VoiceSettingsWindow(original));
                Require(setup.Title=="설정 · First-time setup" && voice.Title=="Voice provider settings","Dialog title missing");
                Require(Children<TextBox>(setup).Any(x=>x.Text=="설정" && AutomationProperties.GetName(x)=="Assistant's name"),"Setup changed assistant data or missed its accessible label");
                Require(Children<TextBlock>(setup).Any(x=>x.Text=="Name to call the assistant") && Children<Button>(setup).Any(x=>x.Content?.ToString()=="Next"),"Setup labels missing");
                Require(Children<ComboBox>(voice).SelectMany(x=>x.Items.OfType<DeviceOption>()).Any(x=>x.Id=="http" && x.Label=="Custom voice server"),"Voice option label or provider ID changed");
                Require(saves==0 && File.ReadAllBytes(AppPaths.ConfigFile).SequenceEqual(before),"Opening a dialog saved settings");RenderWindow(setup,"ui-setup-en.png",650,750);RenderWindow(voice,"ui-voice-en.png",620,700);
            });
            Check("Personal-data editor and import review localize controls without translating or applying personal content",()=>
            {
                UiText.Apply("en");var fixture=Path.Combine(AppPaths.Home,"ui-personal-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(fixture);var store=new Personalization(Path.Combine(fixture,"personal.json"));var apps=new AppRegistry(Path.Combine(fixture,"apps.json"));
                store.SaveNote(null,"설정",store.Version);store.SaveLearned("설정","대화",new[]{new AssistantAction("learned_type","사용자 문장")},null,store.Version);var before=File.ReadAllBytes(store.StoragePath);
                var editor=Track(new PersonalWindow(store));Require(editor.Title=="Manage memory, routines and learned actions","Personal editor title missing");
                Require(Children<ComboBox>(editor).SelectMany(x=>x.Items.OfType<DeviceOption>()).Any(x=>x.Id=="notes" && x.Label=="Memories"),"Category IDs or labels changed");
                Children<ComboBox>(editor).Single(x=>x.Items.OfType<DeviceOption>().Any(v=>v.Id=="learned")).SelectedValue="learned";
                Children<ListBox>(editor).Single(x=>AutomationProperties.GetName(x)==UiText.Get("저장 항목 목록")).SelectedIndex=0;
                Require(Children<TextBox>(editor).Any(x=>x.Text=="설정") && Children<TextBox>(editor).Any(x=>x.Text=="대화") && Children<TextBlock>(editor).Any(x=>x.Text.Contains("key ctrl+c") && x.Text.Contains("Safe access")),"Learned screen editor lost user data or English help");
                var package=new PersonalDataPackage(PersonalDataTransfer.Format,1,new PersonalState{Notes=new(){"대화"}},Array.Empty<PortableApp>());
                var review=Track(new PersonalImportWindow(new(package,Array.Empty<string>()),store,apps));
                Require(review.Title=="Review memories and routines to import" && review.Apply.Content?.ToString()=="Apply reviewed personal data" && review.Description.StartsWith("Add 1 · Replace 0 · Keep existing / duplicate 0\nAdd memory · 대화",StringComparison.Ordinal),"Review copy missing or data translated");
                var restore=Track(new DataRestoreWindow("설정","대화",()=>throw new Exception("Unexpected restore")));Require(restore.Description=="대화" && restore.Title=="설정","Caller-supplied data translated");
                Require(!review.Applied && File.ReadAllBytes(store.StoragePath).SequenceEqual(before),"Viewing changed personal data");RenderWindow(editor,"ui-personal-en.png",850,790);RenderWindow(review,"ui-import-en.png",900,790);
            });
            Check("Approval and calibration views preserve descriptions and microphone names without capturing audio",()=>
            {
                _=Main("en");var approval=Track(new ActionApprovalWindow(null,"설정"));var training=Track(new NameTrainingWindow(original,new("fixture-microphone","설정"),_=>throw new Exception("Unexpected calibration save")));
                Require(approval.Title=="Review this action" && Children<TextBox>(approval).Any(x=>x.Text=="설정\n\nRun this action?"),"Approval changed its description");
                Require(training.Title=="Train the name with my voice" && Children<Button>(training).Any(x=>x.Content?.ToString()=="Reconnect microphone"),"Calibration labels missing");
                Require(Children<TextBlock>(training).Any(x=>x.Text.EndsWith("설정",StringComparison.Ordinal)),"Microphone name translated");
            });
            Check("Voice option labels refresh across all UI languages without changing voice IDs",()=>
            {
                UiText.Apply("ko");var ids=ProAccountClient.Voices.Select(x=>x.Id).ToArray();
                foreach(var language in ConversationLanguage.All){UiText.Apply(language.Code);Require(ProAccountClient.Voices.Select(x=>x.Id).SequenceEqual(ids),"Voice ID changed");Require(ProAccountClient.Voices[0].Label==(language.Code=="ko"?"테오 · 한국어 남성":"Theo · Korean male"),"Stale voice label: "+language.Code);}
            });
            Check("Korean and English chat and settings views render offscreen without native windows",()=>
            {
                Render(Main("en"),"ui-chat-en.png",false);Render(Main("en"),"ui-settings-en.png",true);Render(Main("ko"),"ui-settings-ko.png",true);
            });
        }
        finally{foreach(var view in windows)view.Close();UiText.Apply("ko");}
        File.WriteAllText(Path.Combine(AppPaths.Home,"ui-language-app-checks.json"),JsonSerializer.Serialize(new{total=rows.Count,failed,visibleWindows=0,microphoneCapture=false,speakerPlayback=false,checks=rows},SettingsStore.Json));return failed==0?0:1;
    }
}
