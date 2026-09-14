namespace MultiExplorer;

/// <summary>
/// Persisted application state. Serialised to / deserialised from
/// %APPDATA%\MultiExplorer\settings.json by <see cref="SettingsManager"/>.
/// </summary>
public sealed class AppSettings
{
    public const int CurrentSplitterPositionVersion = 1;
    public const int DefaultSplitterDistance = 1034;

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
    /// The product default is applied once per splitter-position version; after
    /// that, this stores the user's last position for compatibility.
    /// </summary>
    public int SplitterDistance { get; set; } = DefaultSplitterDistance;

    /// <summary>
    /// Left panel's share of the usable two-panel width (0 to 1).
    /// 0 means an older settings file; SplitterDistance is used once and migrated.
    /// </summary>
    public double SplitterRatio { get; set; } = 0;

    /// <summary>
    /// Version of the product-defined initial splitter position already applied.
    /// Older settings have 0, allowing a changed default to take effect once without
    /// preventing later user adjustments from being restored.
    /// </summary>
    public int SplitterPositionVersion { get; set; } = 0;

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

    // Most-recent-first address-bar histories, maintained independently per pane.
    public List<string> LeftPathHistory  { get; set; } = new();
    public List<string> RightPathHistory { get; set; } = new();

    // ── System tray ──────────────────────────────────────────────────────────

    /// <summary>When true, closing the main window hides it to the system tray instead of exiting.</summary>
    public bool MinimizeToTray { get; set; } = true;

    /// <summary>When true MultiExplorer is registered to start when this user signs in.</summary>
    public bool StartWithWindows { get; set; } = false;

    // ── QuickLook integration ─────────────────────────────────────────────────

    /// <summary>When true, pressing Space sends the selected item to QuickLook for preview.</summary>
    public bool QuickLookEnabled { get; set; } = false;

    // ── Appearance ───────────────────────────────────────────────────────────

    /// <summary>"Light" or "Dark". Stored as text for forward-compatible settings files.</summary>
    public string ApplicationTheme { get; set; } = "Light";

    // ── Global show-window hotkey ─────────────────────────────────────────────

    /// <summary>MOD_WIN/MOD_CONTROL/MOD_ALT/MOD_SHIFT flags (without MOD_NOREPEAT).</summary>
    public int ShowWindowModifiers { get; set; } = 0x000B; // MOD_WIN | MOD_CONTROL | MOD_ALT

    /// <summary>Virtual-key code for the show-window hotkey.</summary>
    public int ShowWindowVk { get; set; } = 0x4D; // 'M'
}
