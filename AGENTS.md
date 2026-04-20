# AGENTS.md

## Project

`BtMouseTray` is a small Windows Forms tray app targeting `net10.0-windows`.

The app:
- starts from `Program.cs`
- enumerates Bluetooth or PnP devices with battery information
- lets the user choose one device from the tray menu
- shows the battery percentage in the tray icon
- persists the selected device in `%LocalAppData%\\BtMouseTray\\config.json`

## Working Rules

- Prefer minimal, targeted changes over refactors.
- Preserve the current tray-app behavior unless the task explicitly changes UX.
- Keep the app Windows-only and compatible with WinForms.
- Avoid adding dependencies unless there is a clear need.
- If editing Russian UI strings, make sure the file encoding is correct and the text is not mojibake.

## Key Files

- `Program.cs`: main app logic, tray icon, config persistence, device enumeration, icon rendering.
- `BtMouseTray.csproj`: project settings.
- `Form1.cs` and `Form1.Designer.cs`: currently not central to tray behavior.

## Useful Commands

- Build: `dotnet build`
- Run: `dotnet run --project .\\BtMouseTray.csproj`

## Session Context

When starting a fresh Codex session in this folder, assume:
- the user usually wants direct code changes, not just theory
- this is a local desktop utility, not a library
- preserving current behavior matters more than broad cleanup
- any proposed change should be verified with a build when possible

If the user asks to continue previous work, first inspect:
- `Program.cs`
- git diff/status
- this `AGENTS.md`
