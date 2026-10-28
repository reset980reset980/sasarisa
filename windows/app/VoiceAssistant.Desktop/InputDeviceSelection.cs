using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace VoiceAssistant.Desktop;

internal static class InputDeviceSelection
{
    public static void Validate(string id, string label)
    {
        if (id == null || id.Length > 1024 || id.Any(char.IsControl) || label == null || label.Length > 240 || label.Any(char.IsControl))
            throw new ArgumentException("입력 마이크의 식별자와 이름을 확인하세요.");
        if (id.StartsWith("input:", StringComparison.Ordinal) && !Regex.IsMatch(id, @"\Ainput:v1:(?:wasapi|named):[0-9a-f]{64}\z"))
            throw new ArgumentException("저장된 마이크 식별자 형식을 확인하세요.");
    }

    public static (DeviceOption[] Options, DeviceOption Selected) Select(IReadOnlyList<DeviceOption> devices, string id, string label)
    {
        var options = devices.ToList();
        if (!options.Any(x => x.Id == "")) options.Insert(0, new("", "Windows 기본 마이크"));
        var exact = options.Where(x => x.Id == id).ToArray();
        var matches = exact.Length > 0 ? exact : options.Where(x => x.LegacyId.Length > 0 && x.LegacyId == id).ToArray();
        if (matches.Length == 1) return (options.ToArray(), matches[0]);
        var missing = new DeviceOption(id, (label.Length > 0 ? label : "이전에 선택한 마이크") + (matches.Length > 1 ? " · 중복 식별자, 다시 선택" : " · 현재 연결 없음")) { Unavailable = true, RememberedLabel = label };
        options.RemoveAll(x => x.Id == id); options.Add(missing);
        return (options.ToArray(), missing);
    }

    public static string Help(DeviceOption? selected) => selected switch
    {
        null => "마이크 목록을 확인하고 있어요. 기존 선택은 유지합니다.",
        { Unavailable: true } => "이전에 선택한 마이크를 유지합니다. 연결을 확인하거나 사용할 마이크를 다시 선택하고 저장하세요.",
        { Id: "" } => "Windows 기본 마이크를 사용합니다. 특정 마이크를 기억하려면 목록에서 선택하고 설정을 저장하세요.",
        { IdentityKind: "wasapi" } => "Windows 장치 식별자로 기억합니다. 목록 순서가 바뀌어도 같은 입력을 찾습니다. 설정 저장 후 적용됩니다.",
        { IdentityKind: "named" } => "오디오 방식과 장치 이름으로 기억합니다. 같은 이름이 중복되면 다시 선택해야 합니다. 설정 저장 후 적용됩니다.",
        _ => "장치 순번으로 선택 중입니다. 같은 이름의 장치가 중복되면 연결 후 목록을 확인하고 다시 선택하세요."
    };
}
