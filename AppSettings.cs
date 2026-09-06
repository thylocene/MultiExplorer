namespace MultiExplorer;

/// <summary>
/// Persisted application state. Serialised to / deserialised from
/// %APPDATA%\MultiExplorer\settings.json by <see cref="SettingsManager"/>.
/// </summary>
public sealed class AppSettings
{
    // ── Window geometry ──────────────────────────────────────────────────────

    /// <summary>Left edge of the restored (non-maximised) window in screen coordinates.</summary>
    public int WindowX { get; set; } = 0;

    /// <summary>Top edge of the restored window in screen coordinates.</summary>
    public int WindowY { get; set; } = 0;

    /// <summary>
    /// Width of the restored window in pixels.
    /// 0 means "not yet saved" — MainForm will compute 80 % of the current screen width.
    /// </summary>
    public int WindowWidth { get; set; } = 0;

    /// <summary>
    /// Height of the restored window in pixels.
    /// 0 means "not yet saved" — MainForm will compute 80 % of the current screen height.
    /// </summary>
    public int WindowHeight { get; set; } = 0;

    /// <summary>"Normal" or "Maximized".</summary>
    public string WindowState { get; set; } = "Normal";

    // ── Splitter ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Width of the left panel in pixels (= left edge of the splitter bar).
    /// 0 means "not yet saved — use half the window width".
    /// </summary>
    public int SplitterDistance { get; set; } = 1034;

    /// <summary>When true the left explorer panel is collapsed on start-up.</summary>
    public bool LeftPanelCollapsed  { get; set; } = false;

    /// <summary>When true the right explorer panel is collapsed on start-up.</summary>
    public bool RightPanelCollapsed { get; set; } = false;

    // ── Explorer panel state ─────────────────────────────────────────────────

    public string LeftPanelPath  { get; set; } = @"C:\";
    public string RightPanelPath { get; set; } = @"C:\";

    // Per-panel tab paths. If empty on load, LeftPanelPath / RightPanelPath are used instead.
    public List<string> LeftPanelTabs  { get; set; } = new() { @"C:\" };
    public List<string> RightPanelTabs { get; set; } = new() { @"C:\" };

    // ── System tray ──────────────────────────────────────────────────────────

    /// <summary>When true, closing the main window hides it to the system tray instead of exiting.</summary>
    public bool MinimizeToTray { get; set; } = true;

    // ── QuickLook integration ─────────────────────────────────────────────────

    /// <summary>When true, pressing Space sends the selected item to QuickLook for preview.</summary>
    public bool QuickLookEnabled { get; set; } = false;

    // ── Global show-window hotkey ─────────────────────────────────────────────

    /// <summary>MOD_WIN/MOD_CONTROL/MOD_ALT/MOD_SHIFT flags (without MOD_NOREPEAT).</summary>
    public int ShowWindowModifiers { get; set; } = 0x000B; // MOD_WIN | MOD_CONTROL | MOD_ALT

    /// <summary>Virtual-key code for the show-window hotkey.</summary>
    public int ShowWindowVk { get; set; } = 0x4D; // 'M'
}
