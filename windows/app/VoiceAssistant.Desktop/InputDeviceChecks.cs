using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;

namespace VoiceAssistant.Desktop;

internal static class InputDeviceChecks
{
    internal static string Example(string letter = "a") => "input:v1:wasapi:" + string.Concat(Enumerable.Repeat(letter, 64));
    static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    static void Reject(Action action) { try { action(); } catch (ArgumentException) { return; } throw new Exception("Invalid input selection accepted"); }
    internal static int Run(List<object> rows)
    {
        int failed = 0;
        void Check(string name, Action action) { try { action(); rows.Add(new { name, pass = true }); } catch (Exception ex) { failed++; rows.Add(new { name, pass = false, error = ex.ToString() }); } }
        var chosen = new DeviceOption(Example(), "선택한 마이크") { LegacyId = "1", IdentityKind = "wasapi" };
        Check("Legacy device selection maps to the current identity without mutating the supplied inventory", () =>
        {
            var inventory = new[] { new DeviceOption("", "기본"), chosen };
            var result = InputDeviceSelection.Select(inventory, "1", "이전 이름");
            Require(result.Selected == chosen && inventory.Length == 2 && inventory[1].LegacyId == "1", "Legacy mapping lost the selected input");
            Require(InputDeviceSelection.Select(inventory, chosen.Id, chosen.Label).Selected == chosen, "Identity mapping lost input");
        });
        Check("A missing saved device remains selected and never falls back to a same-name or reused-index input", () =>
        {
            var other = new DeviceOption(Example("b"), chosen.Label) { LegacyId = "1" };
            var missing = InputDeviceSelection.Select(new[] { other }, chosen.Id, chosen.Label).Selected;
            Require(missing.Unavailable && missing.Id == chosen.Id && missing.RememberedLabel == chosen.Label, "Missing input changed device");
            Require(InputDeviceSelection.Help(missing).Contains("다시 선택"), "Missing device lacks recovery guidance");
        });
        Check("Duplicate reported identities remain unavailable instead of selecting the first match", () =>
        {
            var result = InputDeviceSelection.Select(new[] { chosen, chosen with { Label = "중복" } }, chosen.Id, chosen.Label);
            Require(result.Selected.Unavailable && result.Selected.Id == chosen.Id && result.Options.Count(x => x.Id == chosen.Id) == 1, "Ambiguous inventory silently selected a device");
        });
        Check("Settings export and device opt-in preserve identity and remembered label as a pair", () =>
        {
            var current = new UiSettings { Device = chosen.Id, DeviceLabel = chosen.Label };
            var imported = SettingsTransfer.Parse(SettingsTransfer.Serialize(current with { Device = Example("b"), DeviceLabel = "다른 마이크" }));
            Require(imported.Device == Example("b") && imported.DeviceLabel == "다른 마이크", "Device transfer lost fields");
            var kept = SettingsTransfer.Prepare(imported, current, false, false); var applied = SettingsTransfer.Prepare(imported, current, false, true);
            Require(kept.Device == current.Device && kept.DeviceLabel == current.DeviceLabel && applied.Device == imported.Device && applied.DeviceLabel == imported.DeviceLabel, "Device import separated identity from label");
            var mac = MacSettingsTransfer.Parse("{\"inputDevice\":\"Mac-UID\",\"assistantName\":\"코코\"}", current).Settings;
            Require(mac.Device == current.Device && mac.DeviceLabel == current.DeviceLabel, "Mac conversion replaced Windows selection");
        });
        Check("Reserved identity formats and labels validate before saving while legacy IDs remain supported", () =>
        {
            foreach (var id in new[] { "", "1", "legacy microphone", Example(), Example().Replace("wasapi", "named") }) SettingsStore.Validate(new UiSettings { Device = id });
            foreach (var id in new[] { null, "input:v0:wasapi:" + new string('a', 64), Example().Replace('a', 'g'), "input:v1:wasapi:../file", "line\nbreak" }) Reject(() => SettingsStore.Validate(new UiSettings { Device = id! }));
            foreach (var label in new[] { null, new string('가', 241), "line\nbreak" }) Reject(() => SettingsStore.Validate(new UiSettings { DeviceLabel = label! }));
        });
        Check("Name learning keeps the selected input identity and label when updating pronunciation", () =>
        {
            var current = new UiSettings { Device = chosen.Id, DeviceLabel = chosen.Label }; var training = new NameLearning(current, current.Name);
            for (int i = 0; i < NameLearning.TotalTakes; i++) training.Add(training.Prompt, "", true);
            var result = training.ApplyTo(current); Require(result.Device == current.Device && result.DeviceLabel == current.DeviceLabel, "Name training lost input choice");
        });
        return failed;
    }

    internal static async Task<int> RunNativeAsync(List<object> rows)
    {
        int failed = 0; IReadOnlyList<DeviceOption>? options = null;
        async Task Check(string name, Func<Task> action) { try { await action(); rows.Add(new { name, pass = true }); } catch (Exception ex) { failed++; rows.Add(new { name, pass = false, error = ex.ToString() }); } }
        await Check("Real packaged input inventory returns unique validated identities without opening capture", async () =>
        {
            options = await SpeechInput.DevicesAsync(); Require(options.Count >= 1 && options[0].Id == "" && options.Select(x => x.Id).Distinct().Count() == options.Count, "Input inventory malformed");
            foreach (var option in options) InputDeviceSelection.Validate(option.Id, option.Label);
        });
        await Check("PortAudio WASAPI keys match Windows capture endpoint IDs from an independent native API", () =>
        {
            Require(options != null, "No inventory");
            using var enumerator = new MMDeviceEnumerator(); using var endpoints = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
            var keys = new HashSet<string>();
            foreach (var endpoint in endpoints)
            {
                using (endpoint)
                {
                    var payload = JsonSerializer.Serialize(new[] { "v1", "wasapi", endpoint.ID }, new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
                    keys.Add("input:v1:wasapi:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant());
                }
            }
            var actual = options!.Where(x => x.IdentityKind == "wasapi").ToArray();
            Require(keys.Count == 0 || actual.Length > 0, "Windows capture endpoints lost stable identity");
            Require(actual.All(x => keys.Contains(x.Id)), "PortAudio input ID does not match a Windows capture endpoint");
            return Task.CompletedTask;
        });
        return failed;
    }
}
