using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Forms;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // Initialize WinForms application defaults before creating any UI objects.
        ApplicationConfiguration.Initialize();

        // Read an optional device-name filter from command-line arguments.
        string? filter = null;
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals("--filter", StringComparison.OrdinalIgnoreCase))
                filter = args[i + 1];
        }

        // Create and run the tray application instance for the current process.
        using var app = new BtMouseTrayApp(filter);
        app.Run();
    }
}

internal sealed class BtMouseTrayApp : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _menu;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly string _configPath;
    private readonly Regex? _nameFilter;
    private List<BatteryDevice> _devices = new();
    private string? _selectedInstanceId;
    private int? _lastPercent;
    private Dictionary<string, int> _minPercentByInstanceId = new(StringComparer.OrdinalIgnoreCase);
    private DateTime? _nextBatteryLogAt;
    private long _maxBatteryReadMsSinceLog;
    private int _batteryReadFailuresSinceLog;
    private int _batteryReadSuccessesSinceLog;
    private AppSettings _settings = AppSettings.CreateDefault();

    public BtMouseTrayApp(string? nameFilterRegex)
    {
        // Compile the optional name filter once so device discovery can reuse it.
        if (!string.IsNullOrWhiteSpace(nameFilterRegex))
        {
            try { _nameFilter = new Regex(nameFilterRegex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant); } catch { }
        }

        // Store user settings under LocalAppData so selection and minimum values survive restarts.
        var configDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BtMouseTray");
        _configPath = Path.Combine(configDir, "config.json");
        Directory.CreateDirectory(configDir);
        AppLog.Initialize(Path.Combine(configDir, "trace.log"));

        // Restore previous settings and preload the initial device list.
        LoadConfig();
        EnsureConfigExists();
        RefreshDeviceList();

        // Create the tray icon and attach the context menu shell object.
        _menu = new ContextMenuStrip();
        _notifyIcon = new NotifyIcon
        {
            Visible = true,
            Icon = IconFactory.MakeUnknownIcon(_settings),
            Text = "BT device: initializing...",
            ContextMenuStrip = _menu
        };

        // Show the context menu on right-click.
        _notifyIcon.MouseUp += (s, e) => { if (e.Button == MouseButtons.Right) { BuildMenu(); _menu.Show(Cursor.Position); } };

        _timer = new System.Windows.Forms.Timer { Interval = _settings.RefreshIntervalMs };
        _timer.Tick += (_, _) => UpdateTrayIcon();
        _timer.Start();

        // Render the first tray state immediately after startup.
        UpdateTrayIcon();
    }

    // Start the WinForms message loop without showing a main window.
    public void Run() => Application.Run();

    // Refresh the cached list of battery-capable Bluetooth devices.
    private void RefreshDeviceList()
    {
        _devices = PnpBatteryDevices.GetBatteryDevices(_nameFilter);
    }

    private void BuildMenu()
    {
        // Re-enumerate devices before drawing menu entries.
        RefreshDeviceList();
        _menu.Items.Clear();
        // Add one selectable menu item per detected device.
        if (_devices.Count > 0)
        {
            foreach (var d in _devices)
            {
                var item = new ToolStripMenuItem(d.Name)
                {
                    CheckOnClick = true,
                    Checked = string.Equals(d.InstanceId, _selectedInstanceId, StringComparison.OrdinalIgnoreCase)
                };
                item.Click += (_, _) => { _selectedInstanceId = d.InstanceId; _lastPercent = null; SaveConfig(); UpdateTrayIcon(); };
                _menu.Items.Add(item);
            }
        }
        else
        {
            _menu.Items.Add(new ToolStripMenuItem("No BT devices with battery information") { Enabled = false });
        }

        // Add utility actions below the device list.
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Reset minimum", null, (_, _) => ResetMinimumToCurrent());
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Exit", null, (_, _) => { Dispose(); Application.Exit(); });
    }

    private void UpdateTrayIcon()
    {
        // Without a selected device there is nothing meaningful to display.
        if (string.IsNullOrWhiteSpace(_selectedInstanceId))
        {
            SetIcon(IconFactory.MakeUnknownIcon(_settings), "No device selected");
            return;
        }

        // Read the current battery state from the selected device once per timer tick.
        if (!PnpBatteryDevices.TryGetBatteryPercent(_selectedInstanceId, out var name, out var pct, out var batteryReadMs))
        {
            RecordBatteryRead(success: false, batteryReadMs);
            SetIcon(IconFactory.MakeUnknownIcon(_settings), "Device unavailable");
            return;
        }

        RecordBatteryRead(success: true, batteryReadMs);

        // Display the tracked minimum while still showing the live reading in the tooltip.
        int shownPercent = GetDisplayPercent(_selectedInstanceId, pct);
        string tooltip = $"{name}: min {shownPercent}%, now {pct}%";
        if (tooltip.Length > _settings.TooltipMaxLength) tooltip = tooltip.Substring(0, _settings.TooltipMaxLength);

        // Recreate the icon only when the visible percentage changes to avoid extra GDI work.
        if (_lastPercent != shownPercent)
        {
            SetIcon(IconFactory.MakePercentIcon(shownPercent, _settings), tooltip);
            _lastPercent = shownPercent;
        }
        else
        {
            _notifyIcon.Text = tooltip;
        }

        MaybeLogBatterySnapshot(shownPercent, pct);
    }

    // Swap the tray icon and release the previous unmanaged icon handle.
    private void SetIcon(Icon icon, string text)
    {
        var old = _notifyIcon.Icon;
        _notifyIcon.Icon = icon;
        _notifyIcon.Text = text;
        if (old != null && old != icon) DestroyIcon(old.Handle);
    }

    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr hIcon);

    private void LoadConfig()
    {
        try
        {
            // Restore the selected device and per-device minimum values from disk.
            if (File.Exists(_configPath))
            {
                var config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(_configPath));
                _selectedInstanceId = config?.SelectedInstanceId;
                _minPercentByInstanceId = config?.MinPercentByInstanceId ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                _settings = AppSettings.FromConfig(config);
            }
        }
        catch { }
    }

    private void SaveConfig()
    {
        try
        {
            // Persist the active selection and tracked minimum values for the next run.
            var json = JsonSerializer.Serialize(new AppConfig
            {
                SelectedInstanceId = _selectedInstanceId,
                MinPercentByInstanceId = _minPercentByInstanceId,
                RefreshIntervalMs = _settings.RefreshIntervalMs,
                BatteryLogIntervalMinutes = _settings.BatteryLogIntervalMinutes,
                TooltipMaxLength = _settings.TooltipMaxLength,
                BatteryResetJumpPercent = _settings.BatteryResetJumpPercent,
                LowBatteryThresholdPercent = _settings.LowBatteryThresholdPercent,
                MediumBatteryThresholdPercent = _settings.MediumBatteryThresholdPercent,
                FullBatteryTextThresholdPercent = _settings.FullBatteryTextThresholdPercent,
                FullBatteryIconText = _settings.FullBatteryIconText,
                UnknownBatteryIconText = _settings.UnknownBatteryIconText,
                PercentIconFontSize = _settings.PercentIconFontSize,
                UnknownIconFontSize = _settings.UnknownIconFontSize,
                TrayIconSize = _settings.TrayIconSize
            }, new JsonSerializerOptions
            {
                WriteIndented = true
            });
            File.WriteAllText(_configPath, json);
        }
        catch { }
    }

    private void EnsureConfigExists()
    {
        // Materialize the config file on first run so users can edit supported settings directly.
        if (!File.Exists(_configPath))
            SaveConfig();
    }

    private int GetDisplayPercent(string instanceId, int currentPercent)
    {
        // Initialize the minimum when this device is seen for the first time.
        if (!_minPercentByInstanceId.TryGetValue(instanceId, out int minPercent))
            return UpdateStoredMinimum(instanceId, currentPercent);

        // Treat a jump of 20 or more as a reset event and start tracking from the new value.
        if (currentPercent >= minPercent + _settings.BatteryResetJumpPercent)
            return UpdateStoredMinimum(instanceId, currentPercent);

        // Update the stored minimum whenever the device reports an even lower value.
        if (currentPercent < minPercent)
            return UpdateStoredMinimum(instanceId, currentPercent);

        // Otherwise keep showing the previously observed minimum.
        return minPercent;
    }

    private void RecordBatteryRead(bool success, long elapsedMs)
    {
        if (elapsedMs > _maxBatteryReadMsSinceLog)
            _maxBatteryReadMsSinceLog = elapsedMs;

        if (success)
            _batteryReadSuccessesSinceLog++;
        else
            _batteryReadFailuresSinceLog++;
    }

    private void MaybeLogBatterySnapshot(int shownPercent, int livePercent)
    {
        var now = DateTime.Now;
        if (string.IsNullOrWhiteSpace(_selectedInstanceId))
            return;

        // Log immediately on the first successful battery read after startup.
        if (_nextBatteryLogAt.HasValue && now < _nextBatteryLogAt.Value)
            return;

        AppLog.BatterySnapshot(
            _selectedInstanceId,
            shownPercent,
            livePercent,
            _batteryReadSuccessesSinceLog,
            _batteryReadFailuresSinceLog,
            _maxBatteryReadMsSinceLog);

        _nextBatteryLogAt = now.AddMinutes(_settings.BatteryLogIntervalMinutes);
        _maxBatteryReadMsSinceLog = 0;
        _batteryReadFailuresSinceLog = 0;
        _batteryReadSuccessesSinceLog = 0;
    }

    private int UpdateStoredMinimum(string instanceId, int percent)
    {
        _minPercentByInstanceId[instanceId] = percent;
        SaveConfig();
        return percent;
    }

    private void ResetMinimumToCurrent()
    {
        // Ignore the command when no device is selected.
        if (string.IsNullOrWhiteSpace(_selectedInstanceId))
            return;

        // Read the live value and replace the stored minimum for the selected device.
        if (!PnpBatteryDevices.TryGetBatteryPercent(_selectedInstanceId, out _, out int currentPercent))
            return;

        UpdateStoredMinimum(_selectedInstanceId, currentPercent);
        _lastPercent = null;
        UpdateTrayIcon();
    }

    // Release WinForms resources when the app exits from the tray menu.
    public void Dispose()
    {
        _timer.Dispose();
        _menu.Dispose();
        _notifyIcon.Dispose();
    }

    internal sealed class AppConfig
    {
        public string? SelectedInstanceId { get; set; }
        public Dictionary<string, int>? MinPercentByInstanceId { get; set; }
        public int? RefreshIntervalMs { get; set; }
        public int? BatteryLogIntervalMinutes { get; set; }
        public int? TooltipMaxLength { get; set; }
        public int? BatteryResetJumpPercent { get; set; }
        public int? LowBatteryThresholdPercent { get; set; }
        public int? MediumBatteryThresholdPercent { get; set; }
        public int? FullBatteryTextThresholdPercent { get; set; }
        public string? FullBatteryIconText { get; set; }
        public string? UnknownBatteryIconText { get; set; }
        public float? PercentIconFontSize { get; set; }
        public float? UnknownIconFontSize { get; set; }
        public int? TrayIconSize { get; set; }
    }

    internal sealed class AppSettings
    {
        public int RefreshIntervalMs { get; init; }
        public int BatteryLogIntervalMinutes { get; init; }
        public int TooltipMaxLength { get; init; }
        public int BatteryResetJumpPercent { get; init; }
        public int LowBatteryThresholdPercent { get; init; }
        public int MediumBatteryThresholdPercent { get; init; }
        public int FullBatteryTextThresholdPercent { get; init; }
        public string FullBatteryIconText { get; init; } = "F";
        public string UnknownBatteryIconText { get; init; } = "?";
        public float PercentIconFontSize { get; init; }
        public float UnknownIconFontSize { get; init; }
        public int TrayIconSize { get; init; }

        internal static AppSettings CreateDefault() => new()
        {
            RefreshIntervalMs = 10_000,
            BatteryLogIntervalMinutes = 15,
            TooltipMaxLength = 63,
            BatteryResetJumpPercent = 20,
            LowBatteryThresholdPercent = 10,
            MediumBatteryThresholdPercent = 20,
            FullBatteryTextThresholdPercent = 100,
            FullBatteryIconText = "F",
            UnknownBatteryIconText = "?",
            PercentIconFontSize = 7.5f,
            UnknownIconFontSize = 9f,
            TrayIconSize = 16
        };

        internal static AppSettings FromConfig(AppConfig? config)
        {
            var defaults = CreateDefault();
            return new AppSettings
            {
                RefreshIntervalMs = config?.RefreshIntervalMs > 0 ? config.RefreshIntervalMs.Value : defaults.RefreshIntervalMs,
                BatteryLogIntervalMinutes = config?.BatteryLogIntervalMinutes > 0 ? config.BatteryLogIntervalMinutes.Value : defaults.BatteryLogIntervalMinutes,
                TooltipMaxLength = config?.TooltipMaxLength > 0 ? config.TooltipMaxLength.Value : defaults.TooltipMaxLength,
                BatteryResetJumpPercent = config?.BatteryResetJumpPercent > 0 ? config.BatteryResetJumpPercent.Value : defaults.BatteryResetJumpPercent,
                LowBatteryThresholdPercent = config?.LowBatteryThresholdPercent >= 0 ? config.LowBatteryThresholdPercent.Value : defaults.LowBatteryThresholdPercent,
                MediumBatteryThresholdPercent = config?.MediumBatteryThresholdPercent >= 0 ? config.MediumBatteryThresholdPercent.Value : defaults.MediumBatteryThresholdPercent,
                FullBatteryTextThresholdPercent = config?.FullBatteryTextThresholdPercent > 0 ? config.FullBatteryTextThresholdPercent.Value : defaults.FullBatteryTextThresholdPercent,
                FullBatteryIconText = !string.IsNullOrWhiteSpace(config?.FullBatteryIconText) ? config.FullBatteryIconText : defaults.FullBatteryIconText,
                UnknownBatteryIconText = !string.IsNullOrWhiteSpace(config?.UnknownBatteryIconText) ? config.UnknownBatteryIconText : defaults.UnknownBatteryIconText,
                PercentIconFontSize = config?.PercentIconFontSize > 0 ? config.PercentIconFontSize.Value : defaults.PercentIconFontSize,
                UnknownIconFontSize = config?.UnknownIconFontSize > 0 ? config.UnknownIconFontSize.Value : defaults.UnknownIconFontSize,
                TrayIconSize = config?.TrayIconSize > 0 ? config.TrayIconSize.Value : defaults.TrayIconSize
            };
        }
    }
}

internal static class IconFactory
{
    [DllImport("user32.dll", SetLastError = true)] private static extern bool DestroyIcon(IntPtr hIcon);

    public static Icon MakePercentIcon(int percent, BtMouseTrayApp.AppSettings settings)
    {
        // Render a small colored square with the visible battery value.
        string text = percent >= settings.FullBatteryTextThresholdPercent ? settings.FullBatteryIconText : percent.ToString();
        Color bg = percent < settings.LowBatteryThresholdPercent ? Color.LightCoral
            : percent < settings.MediumBatteryThresholdPercent ? Color.Yellow
            : Color.LimeGreen;
        return Render(bg, text, settings.PercentIconFontSize, settings.TrayIconSize);
    }

    // Render a fallback icon when battery state is unknown or unavailable.
    public static Icon MakeUnknownIcon(BtMouseTrayApp.AppSettings settings) =>
        Render(Color.LightCoral, settings.UnknownBatteryIconText, settings.UnknownIconFontSize, settings.TrayIconSize);

    private static Icon Render(Color bg, string text, float fontSize, int iconSize)
    {
        // Draw into a square bitmap and convert it into a tray icon handle.
        using var bmp = new Bitmap(iconSize, iconSize);
        using var g = Graphics.FromImage(bmp);

        g.Clear(bg);
        using var font = new Font("Segoe UI", fontSize, FontStyle.Bold);
        g.DrawString(text, font, Brushes.Black, new RectangleF(0, 0, iconSize, iconSize),
            new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center });

        IntPtr hIcon = bmp.GetHicon();
        var icon = (Icon)Icon.FromHandle(hIcon).Clone();
        DestroyIcon(hIcon);
        return icon;
    }
}

internal sealed record BatteryDevice(string InstanceId, string Name);

internal static class PnpBatteryDevices
{
    private const uint DIGCF_PRESENT = 0x02;
    private const uint DIGCF_ALLCLASSES = 0x04;
    private const uint SPDRP_DEVICEDESC = 0x00;
    private const uint SPDRP_FRIENDLYNAME = 0x0C;
    private const uint SPDRP_SERVICE = 0x04;
    private const int CR_SUCCESS = 0x00000000;
    private const uint DN_STARTED = 0x00000008;
    private const uint DN_DEVICE_DISCONNECTED = 0x02000000;

    private static DEVPROPKEY BatteryPercentKey = new(new Guid("104EA319-6EE2-4701-BD47-8DDBF425BBE5"), 2);

    public static List<BatteryDevice> GetBatteryDevices(Regex? nameFilter)
    {
        // Enumerate present devices and keep only BT devices that are currently connected
        // and expose a battery percentage.
        return WithPresentDeviceInfoSet(
            new List<BatteryDevice>(),
            h =>
            {
                var presentAudioEndpointNames = GetPresentAudioEndpointNames(h);
                var audioFamilies = GetBluetoothAudioFamilies(h);
                var result = new List<BatteryDevice>();
                uint index = 0;
                var data = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };

                // Walk through all devices returned by SetupAPI.
                while (SetupDiEnumDeviceInfo(h, index++, ref data))
                {
                    var id = GetInstanceId(h, ref data);
                    if (id == null || !id.StartsWith("BTH", StringComparison.OrdinalIgnoreCase)) continue;

                    var name = GetDeviceName(h, ref data);
                    if (!IsConnected(ref data)) continue;
                    if (nameFilter != null && !nameFilter.IsMatch(name)) continue;
                    if (audioFamilies.Contains(GetBluetoothBaseName(name)) &&
                        !HasMatchingAudioEndpoint(name, presentAudioEndpointNames))
                        continue;

                    if (TryReadBatteryPercent(h, ref data, out _))
                        result.Add(new BatteryDevice(id, name));
                }

                return result;
            });
    }

    public static bool TryGetBatteryPercent(string instanceId, out string name, out int percent) =>
        TryGetBatteryPercent(instanceId, out name, out percent, out _);

    public static bool TryGetBatteryPercent(string instanceId, out string name, out int percent, out long elapsedMs)
    {
        var totalSw = Stopwatch.StartNew();
        // Open a device by instance id and read the battery property once.
        var result = WithPresentDeviceInfoSet(
            (Success: false, Name: instanceId, Percent: 0),
            h =>
            {
                var data = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
                if (!SetupDiOpenDeviceInfoW(h, instanceId, IntPtr.Zero, 0, ref data))
                    return (Success: false, Name: instanceId, Percent: 0);

                var deviceName = GetDeviceName(h, ref data);
                return TryReadBatteryPercent(h, ref data, out var batteryPercent)
                    ? (Success: true, Name: deviceName, Percent: batteryPercent)
                    : (Success: false, Name: deviceName, Percent: 0);
            });

        totalSw.Stop();
        elapsedMs = totalSw.ElapsedMilliseconds;
        name = result.Name;
        percent = result.Percent;
        return result.Success;
    }

    private static T WithPresentDeviceInfoSet<T>(T fallback, Func<IntPtr, T> action)
    {
        var h = SetupDiGetClassDevsW(IntPtr.Zero, null, IntPtr.Zero, DIGCF_PRESENT | DIGCF_ALLCLASSES);
        if (h == (IntPtr)(-1)) return fallback;

        try
        {
            return action(h);
        }
        finally { SetupDiDestroyDeviceInfoList(h); }
    }

    private static bool TryReadBatteryPercent(IntPtr h, ref SP_DEVINFO_DATA data, out int percent)
    {
        // Read the battery percentage property into a raw buffer and validate its range.
        percent = 0;
        var buffer = new byte[256];
        if (!SetupDiGetDevicePropertyW(h, ref data, ref BatteryPercentKey, out _, buffer, (uint)buffer.Length, out var reqSize, 0))
            return false;

        if (reqSize > 0 && buffer[0] <= 100)
        {
            percent = buffer[0];
            return true;
        }
        return false;
    }

    private static HashSet<string> GetPresentAudioEndpointNames(IntPtr h)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        uint index = 0;
        var data = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
        while (SetupDiEnumDeviceInfo(h, index++, ref data))
        {
            var id = GetInstanceId(h, ref data);
            if (id == null || !id.StartsWith(@"SWD\MMDEVAPI\", StringComparison.OrdinalIgnoreCase))
                continue;

            var name = GetDeviceName(h, ref data);
            if (!string.IsNullOrWhiteSpace(name))
                result.Add(name);
        }

        return result;
    }

    private static bool IsConnected(ref SP_DEVINFO_DATA data)
    {
        if (CM_Get_DevNode_Status(out var status, out _, data.DevInst, 0) != CR_SUCCESS)
            return false;

        return (status & DN_STARTED) != 0 && (status & DN_DEVICE_DISCONNECTED) == 0;
    }

    private static HashSet<string> GetBluetoothAudioFamilies(IntPtr h)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        uint index = 0;
        var data = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
        while (SetupDiEnumDeviceInfo(h, index++, ref data))
        {
            var id = GetInstanceId(h, ref data);
            if (id == null || !id.StartsWith("BTH", StringComparison.OrdinalIgnoreCase))
                continue;

            var name = GetDeviceName(h, ref data);
            var service = GetRegistryProperty(h, ref data, SPDRP_SERVICE);
            if (LooksLikeBluetoothAudioFunction(name, service))
                result.Add(GetBluetoothBaseName(name));
        }

        return result;
    }

    private static bool LooksLikeBluetoothAudioFunction(string name, string? service)
    {
        if (service != null &&
            (service.Equals("BthA2dp", StringComparison.OrdinalIgnoreCase) ||
             service.Equals("BthHFAud", StringComparison.OrdinalIgnoreCase) ||
             service.Equals("BthHFEnum", StringComparison.OrdinalIgnoreCase) ||
             service.Equals("Microsoft_Bluetooth_AvrcpTransport", StringComparison.OrdinalIgnoreCase)))
            return true;

        return name.EndsWith(" Stereo", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith(" Hands-Free AG", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith(" Hands-Free AG Audio", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith(" Avrcp Transport", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasMatchingAudioEndpoint(string name, HashSet<string> presentAudioEndpointNames)
    {
        var baseName = GetBluetoothBaseName(name);
        foreach (var endpointName in presentAudioEndpointNames)
        {
            if (endpointName.Contains(baseName, StringComparison.OrdinalIgnoreCase) ||
                baseName.Contains(endpointName, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static string GetBluetoothBaseName(string name)
    {
        foreach (var suffix in new[]
        {
            " Hands-Free AG Audio",
            " Hands-Free AG",
            " Avrcp Transport",
            " Stereo"
        })
        {
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return name[..^suffix.Length].Trim();
        }

        return name.Trim();
    }

    private static string GetInstanceId(IntPtr h, ref SP_DEVINFO_DATA data)
    {
        // Resolve the stable PnP instance id used for persistence.
        var sb = new StringBuilder(512);
        SetupDiGetDeviceInstanceIdW(h, ref data, sb, sb.Capacity, out _);
        return sb.ToString();
    }

    private static string GetDeviceName(IntPtr h, ref SP_DEVINFO_DATA data)
    {
        // Keep a local helper so the ref parameter is passed explicitly.
        // Try the friendly name first, then fall back to the device description.
        return GetRegistryProperty(h, ref data, SPDRP_FRIENDLYNAME) ?? GetRegistryProperty(h, ref data, SPDRP_DEVICEDESC) ?? "Unknown";
    }

    private static string? GetRegistryProperty(IntPtr h, ref SP_DEVINFO_DATA data, uint prop)
    {
        var buffer = new byte[1024];
        if (!SetupDiGetDeviceRegistryPropertyW(h, ref data, prop, out _, buffer, (uint)buffer.Length, out var reqSize) || reqSize == 0)
            return null;

        return Encoding.Unicode.GetString(buffer, 0, (int)reqSize).TrimEnd('\0').Trim();
    }

    [StructLayout(LayoutKind.Sequential)] struct SP_DEVINFO_DATA { public uint cbSize; public Guid ClassGuid; public uint DevInst; public IntPtr Reserved; }
    [StructLayout(LayoutKind.Sequential)] struct DEVPROPKEY { public Guid fmtid; public uint pid; public DEVPROPKEY(Guid g, uint p) { fmtid = g; pid = p; } }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr SetupDiGetClassDevsW(IntPtr ClassGuid, string? Enum, IntPtr hwnd, uint Flags);
    [DllImport("setupapi.dll", SetLastError = true)] static extern bool SetupDiDestroyDeviceInfoList(IntPtr h);
    [DllImport("setupapi.dll", SetLastError = true)] static extern bool SetupDiEnumDeviceInfo(IntPtr h, uint Index, ref SP_DEVINFO_DATA data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool SetupDiOpenDeviceInfoW(IntPtr h, string Id, IntPtr hwnd, uint Flags, ref SP_DEVINFO_DATA data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)] static extern bool SetupDiGetDeviceInstanceIdW(IntPtr h, ref SP_DEVINFO_DATA data, StringBuilder id, int size, out int req);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)] static extern bool SetupDiGetDeviceRegistryPropertyW(IntPtr h, ref SP_DEVINFO_DATA data, uint prop, out uint type, byte[] buf, uint size, out uint req);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)] static extern bool SetupDiGetDevicePropertyW(IntPtr h, ref SP_DEVINFO_DATA data, ref DEVPROPKEY key, out uint type, byte[] buf, uint size, out uint req, uint flags);
    [DllImport("cfgmgr32.dll")] static extern int CM_Get_DevNode_Status(out uint status, out uint problemNumber, uint devInst, uint flags);
}

internal static class AppLog
{
    private static readonly object Sync = new();
    private static string? _logPath;

    public static void Initialize(string logPath)
    {
        _logPath = logPath;
    }

    public static void BatterySnapshot(string instanceId, int shownPercent, int livePercent, int successCount, int failureCount, long maxReadMs) =>
        Write($"device='{instanceId}', shown={shownPercent}, live={livePercent}, polls={successCount}, failures={failureCount}, max_read_ms={maxReadMs}");

    public static void Info(string message) => Write(message);

    public static void Warn(string message) => Write(message);

    private static void Write(string message)
    {
        if (string.IsNullOrWhiteSpace(_logPath))
            return;

        try
        {
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}";
            lock (Sync)
            {
                File.AppendAllText(_logPath, line, Encoding.UTF8);
            }
        }
        catch
        {
        }
    }
}


