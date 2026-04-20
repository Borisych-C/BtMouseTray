# BtMouseTray

BtMouseTray is a lightweight Windows tray utility that shows the battery level of a selected Bluetooth device directly in the notification area.

It is designed for a simple workflow:

- detect Bluetooth or PnP devices that expose battery information
- let you pick one device from the tray menu
- render the current battery percentage inside the tray icon
- remember the selected device between launches

## Highlights

- Native Windows Forms tray app
- No main window required
- Battery percentage rendered directly into the tray icon
- Persistent device selection in `%LocalAppData%\BtMouseTray\config.json`
- Optional device-name filtering through the command line
- Minimal codebase with the main logic centered in `Program.cs`

## Requirements

- Windows
- .NET 10 SDK

## Build

```powershell
dotnet build
```

## Run

```powershell
dotnet run --project .\BtMouseTray.csproj
```

Optional filtering by device name:

```powershell
dotnet run --project .\BtMouseTray.csproj -- --filter "mouse|mx"
```

## How It Works

On startup, BtMouseTray enumerates present devices and keeps only entries that expose battery information. You can select a device from the tray context menu, and the app will:

- display the tracked battery value in the tray icon
- show a tooltip with the tracked minimum and live reading
- save the selected device and settings locally

If the device is unavailable or no device is selected, the tray icon falls back to an unknown-state indicator.

## Configuration

The application stores its local settings in:

```text
%LocalAppData%\BtMouseTray\config.json
```

Current settings include values such as:

- refresh interval
- battery logging interval
- tooltip length
- low/medium battery thresholds
- tray icon text and sizing options

The app also writes a trace log to:

```text
%LocalAppData%\BtMouseTray\trace.log
```

## Project Structure

- `Program.cs` - application entry point, tray behavior, config handling, battery polling, and icon rendering
- `BtMouseTray.csproj` - project definition
- `AGENTS.md` - local project instructions used during development

## Notes

- This project is Windows-only by design.
- The repository intentionally avoids committing local build artifacts and user-specific files.

## License

MIT License. See [LICENSE](LICENSE).
