using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VoiceAssistant.Desktop;

public sealed record DesktopControl(string Key, string Role, string Name, string Context, string Operation, bool Editable, string State, string Revision, int? SelectionStart = null, int SelectionLength = 0);
public sealed record DesktopControlSnapshot(string Application, string Window, IReadOnlyList<DesktopControl> Controls, bool Truncated = false);
public sealed record ObservedControl(string Id, string Role, string Name, string Context, string Operation, bool Editable, string State, int? SelectionStart = null, int SelectionLength = 0);
public sealed record ControlPage(string Application, string Window, IReadOnlyList<ObservedControl> Controls, int Total, int? Next, bool Truncated);
public static class ControlObservation
{
    public static string Data(ControlPage page)
    {
        string Clip(string value, int limit) => value.Length <= limit ? value : value[..limit] + "…";
        string Format(int limit) => JsonSerializer.Serialize(page with
        {
            Application = Clip(page.Application, limit), Window = Clip(page.Window, limit),
            Controls = page.Controls.Select(x => x with { Name = Clip(x.Name, limit), Context = Clip(x.Context, limit) }).ToArray(),
            Truncated = page.Truncated || page.Application.Length > limit || page.Window.Length > limit || page.Controls.Any(x => x.Name.Length > limit || x.Context.Length > limit)
        }, new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        var data = Format(180); if (data.Length > 5800) data = Format(40); if (data.Length > 5800) data = Format(12);
        return data;
    }
}
public interface IDesktopControlDriver
{
    Task<DesktopControlSnapshot> ReadAsync(CancellationToken token);
    Task PressAsync(DesktopControl control, CancellationToken token);
    Task FillAsync(DesktopControl control, string text, CancellationToken token);
    Task InsertAsync(DesktopControl control, string text, CancellationToken token);
}

/// References live for one AI request and only for the latest displayed page.
/// Names are descriptive, never a substitute for the observed element identity.
public sealed class DesktopControls(IDesktopControlDriver driver, Func<DateTimeOffset>? clock = null)
{
    public const int PageSize = 12;
    readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.UtcNow);
    readonly Dictionary<string, DesktopControl> observed = new(StringComparer.Ordinal);
    DateTimeOffset expires;
    public async Task<ControlPage> ListAsync(string filter, int offset, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); observed.Clear();
        if (filter.Length > 120 || offset is < 0 or > 240) throw new ArgumentException("화면 목록의 검색어나 페이지를 확인하세요.");
        var snapshot = await driver.ReadAsync(token); token.ThrowIfCancellationRequested();
        var all = snapshot.Controls.Where(x => filter.Length == 0 || x.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) || x.Context.Contains(filter, StringComparison.OrdinalIgnoreCase)).Take(240).ToArray();
        var page = all.Skip(offset).Take(PageSize).Select(x =>
        {
            var id = Guid.NewGuid().ToString("N"); observed.Add(id, x);
            return new ObservedControl(id, x.Role, x.Name, x.Context, x.Operation, x.Editable, x.State, x.SelectionStart, x.SelectionLength);
        }).ToArray();
        expires = now().AddMinutes(2);
        return new(snapshot.Application, snapshot.Window, page, all.Length, offset + page.Length < all.Length ? offset + page.Length : null, snapshot.Truncated);
    }
    DesktopControl Find(string id)
    {
        if (now() >= expires || !observed.TryGetValue(id, out var control)) throw new AgentStoppedException("화면 목록이 오래됐거나 해당 항목이 없어요. 목록을 다시 확인한 뒤 요청하세요.");
        return control;
    }
    public string Describe(AssistantAction action)
    {
        var control = Find(action.Kind is "fill_field" or "insert_field" ? action.Target : action.Value);
        if (action.Kind is "fill_field" or "insert_field" && !control.Editable || action.Kind == "press_control" && control.Operation.Length == 0) throw new InvalidOperationException("이 항목은 요청한 동작을 지원하지 않아요.");
        if (action.Kind == "insert_field" && control.SelectionStart == null) throw new InvalidOperationException("이 입력칸의 커서 위치를 확인하지 못했어요. 입력칸을 선택한 뒤 다시 요청하세요.");
        var role = control.Role switch { "Edit" => "입력칸", "Document" => "문서", "Button" => "버튼", "Hyperlink" => "링크", "MenuItem" => "메뉴", "CheckBox" => "확인란", "RadioButton" => "선택 항목", "TabItem" => "탭", "ComboBox" => "선택 목록", _ => "항목" };
        var label = $"{control.Context} / {role} · {control.Name}";
        var operation = control.Operation switch { "invoke" => "누르기", "toggle" => "선택 상태 바꾸기", "select" => "선택하기", "expand_or_collapse" => control.State == "Expanded" ? "목록 접기" : "목록 펼치기", _ => "" };
        if (action.Kind == "insert_field") return label + (control.SelectionLength == 0 ? "\n\n현재 커서 위치에 다음 글을 넣습니다:\n" : "\n\n현재 선택된 부분만 다음 글로 바꿉니다. 나머지 글은 보존합니다:\n") + action.Value;
        return action.Kind == "fill_field" ? label + "\n\n입력칸 전체를 다음 내용으로 바꿉니다:\n" + action.Value : label + "\n동작: " + operation;
    }
    public ActionEffect Effect(AssistantAction action)
    {
        var control = Find(action.Kind is "fill_field" or "insert_field" ? action.Target : action.Value);
        return ExecutionConsent.Effect(action, control.Name, control.Context);
    }
    public async Task PressAsync(string id, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); var control = Find(id);
        if (control.Operation.Length == 0) throw new InvalidOperationException("누르기를 지원하는 항목이 아니에요.");
        try { await driver.PressAsync(control, token); token.ThrowIfCancellationRequested(); }
        finally { observed.Clear(); }
    }
    public async Task FillAsync(string id, string text, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); var control = Find(id);
        if (!control.Editable) throw new InvalidOperationException("읽기 전용이거나 글 입력을 지원하지 않는 항목이에요.");
        if (text.Length > 6000) throw new ArgumentException("입력할 내용은 6,000자 이내여야 해요.");
        try { await driver.FillAsync(control, text, token); token.ThrowIfCancellationRequested(); }
        finally { observed.Clear(); }
    }
    public async Task InsertAsync(string id, string text, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); var control = Find(id);
        if (!control.Editable || control.SelectionStart == null) throw new InvalidOperationException("편집 가능한 입력칸의 커서 위치를 먼저 확인하세요.");
        if (text.Length is < 1 or > 6000) throw new ArgumentException("입력할 내용은 1~6,000자여야 해요.");
        try { await driver.InsertAsync(control, text, token); token.ThrowIfCancellationRequested(); }
        finally { observed.Clear(); }
    }
}
