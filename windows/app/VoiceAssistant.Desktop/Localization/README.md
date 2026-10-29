# UI copy

The original Mac app selects Korean UI for `ko` and English for other conversation languages. `UiText` follows that rule. Conversation output still uses all twelve supported languages.

Use `UiText.Get` only for owned UI copy. Use `UiText.Format` or `UiText.Copy` for templates and pass names, model IDs, device names and other data as arguments. A `UiPhrase` argument is explicitly translatable; an ordinary string argument is always data. Never translate a complete user message, transcript, saved note, imported name, raw progress message or control tag.

XAML uses stable SHA-256 resource keys. Run `windows/tools/localize_xaml.py` after adding fixed copy to `Ui.en.json`; editable text and the assistant's name are excluded. The dynamic and dialogs catalogues cover explicitly marked C# copy. Embedded JSON resources must use `WithCulture="false"`.

Apply language only after successful settings persistence. Main, overlay, phone and tray copy refresh on a language change; modal dialogs use the language when opened. Existing diagnostic, conversion and restore details still include some Korean copy. `AiUsageWindow` retains its existing per-window bilingual implementation.

`--self-test --ui-language-app-only` checks real controls and renders content offscreen in an isolated `VOICE_ASSISTANT_HOME`. It must never call `Show`, `ShowDialog`, microphone capture or audio playback.
