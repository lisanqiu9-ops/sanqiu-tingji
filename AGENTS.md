# AGENTS.md

三秋听记 (SanqiuTingji): an independently maintained Windows desktop meeting assistant (derived from HuiYi / Cicada, Apache-2.0; see NOTICE). It turns what the PC plays and microphone input into live transcripts, optional translations, and AI meeting notes.

## Hard rules

- API keys live only in `%APPDATA%\SanqiuTingji\keys\<service id>.key`, DPAPI-encrypted (`AIKey`): never in settings, logs or exports.
- Every AI call goes through `AiClient` (start gate, priorities, the platform's own thinking switch from `ProviderPreset`, content in the system message). Services are OpenAI-compatible; platforms are presets in `Providers.cs`, never special cases elsewhere.
- Never bundle speech models; models are downloaded and checked against pinned SHA-256 (`ModelStore`).
- The live panel (实时面板) and the floating subtitles (悬浮字幕) never take focus (`WS_EX_NOACTIVATE`) and stay out of captures (`WDA_EXCLUDEFROMCAPTURE`) unless the user turns on 在屏幕共享和录屏中显示字幕窗口 (then they show 共享中可见).
- Engines in `SanqiuTingji.Core` are UI-free and raise events on worker threads; `AppModel` hops to the UI thread.
- `session.json` stays compatible with Cicada's format (camelCase, ISO-8601 UTC dates without fractions).
- The debug log never holds transcript text, translations or the key.

## Design

三秋听记 uses its own branding (`Brand.Name` in `AppModel.cs`), Win11 acrylic/glass styling (`Native.cs`), and unified motion (`Motion.cs`). Words: 发言人 (not 说话人), 实时面板, 悬浮字幕, 逐字稿. Colours live in `App.xaml` and `Theme.cs`. UI copy uses `SanqiuTingji.Localization.L` and `Localization/Strings*.resx` for Simplified Chinese and English. Use `{local:Loc}` in XAML and `UiText.Set` / `UiText.Bind` for code-created labels so changes apply immediately. Keep copy short. API identifiers, AI prompts, transcripts and user-authored names/templates must keep their original values.

## Commands

```powershell
dotnet build SanqiuTingji.sln
dotnet test tests/SanqiuTingji.Tests
dotnet run --project src/SanqiuTingji -- --listen          # or --show main|settings|setup
dotnet run --project tools/SanqiuTingji.Cli -- loopback <audio file>
powershell -File scripts/package.ps1
```

## Verifying

Run the app and check the behaviour, not just the build. Use `sanqiu-cli loopback` with a clip to drive the real capture path, read `%LOCALAPPDATA%\SanqiuTingji\debug.log`. To check the UI without taking over the desktop, run the app with `--snapshot <folder>`: every window renders to PNG off screen (the capture-proof ones too); `--feed <audio>` listens to a file instead of the PC. VB-CABLE, a real meeting and a real voice need a person.

## Conventions

Commit messages follow Conventional Commits (`feat:`, `fix:`, `docs:`, `build:`, `refactor:`, `test:`, `chore:`) with the subject written entirely in Chinese, never mixed with English: `feat:` / `fix:` subjects become the release notes and the in-app update box, so each says what the user gets in one plain sentence. One change per commit. Comments say why. Keep `README.md` in step with user-visible changes.
