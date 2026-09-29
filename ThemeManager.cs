using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace MultiExplorer;

public enum ApplicationTheme
{
    Light,
    Dark,
}

/// <summary>
/// Single source of truth for application colours and Windows dark-mode integration.
/// WinForms does not provide an application-wide dark theme, and IExplorerBrowser is
/// a native child window, so both managed controls and native HWNDs are handled here.
/// </summary>
internal static class ThemeManager
{
    private const int ButtonCornerRadius = 6;
    private const string NativeThemeMarkerProperty =
        "MultiExplorer.NativeTheme.4FD929EE-85DD-44C8-BC73-3A6864450CC8";

    internal static ApplicationTheme Current { get; private set; } = ApplicationTheme.Light;
    internal static bool IsDark => Current == ApplicationTheme.Dark;

    internal static Color Background => IsDark ? Color.FromArgb(32, 32, 32) : Color.FromArgb(243, 243, 243);
    internal static Color Surface    => IsDark ? Color.FromArgb(40, 40, 40) : Color.FromArgb(250, 250, 250);
    internal static Color Window     => IsDark ? Color.FromArgb(30, 30, 30) : Color.White;
    internal static Color Text       => IsDark ? Color.FromArgb(245, 245, 245) : Color.FromArgb(25, 25, 25);
    internal static Color MutedText  => IsDark ? Color.FromArgb(190, 190, 190) : Color.FromArgb(96, 96, 96);
    // Keep every dark-mode outline and separator on one neutral grey.  Native
    // ToolStrip rendering and inactive pane frames also consume this colour so
    // they do not fall back to the much brighter Windows border palette.
    internal static Color Border     => IsDark ? Color.FromArgb(105, 105, 105) : Color.FromArgb(190, 190, 190);
    internal static Color Hover      => IsDark ? Color.FromArgb(62, 62, 62) : Color.FromArgb(224, 224, 224);
    // Explorer-style selection colour requested for every application menu.
    internal static Color MenuHighlight => Color.FromArgb(229, 243, 255); // #e5f3ff
    internal static Color MenuHighlightText => Color.FromArgb(25, 25, 25);
    internal static Color Pressed    => IsDark ? Color.FromArgb(76, 76, 76) : Color.FromArgb(207, 207, 207);
    internal static Color Accent     => Color.FromArgb(0, 120, 212);
    internal static Color ActiveTabBackground => Color.FromArgb(224, 255, 192);
    internal static Color ActiveTabText => Color.FromArgb(25, 25, 25);
    internal static Color ActiveSelection => Color.FromArgb(43, 136, 197); // #2B88C5
    internal static Color DetailsFolderIcon => IsDark
        ? Color.FromArgb(166, 212, 247)
        : Color.FromArgb(126, 189, 237);
    // The standard accent is intended primarily for filled controls.  Use a
    // lighter variant when it is rendered as foreground text on dark surfaces.
    internal static Color AccentText => IsDark ? Color.FromArgb(96, 205, 255) : Accent;
    internal static Color AccentHover => IsDark ? Color.FromArgb(48, 68, 84) : Color.FromArgb(222, 238, 250);
    // Selected items in a pane that does not own keyboard focus. Keep the
    // selection visible without competing with the active pane's blue accent.
    internal static Color InactiveSelection => IsDark
        ? Color.FromArgb(65, 78, 91)
        : Color.FromArgb(213, 222, 231);
    internal static Color InactiveSelectionText => Text;
    internal static Color NavigationBorder => IsDark ? Border : Color.FromArgb(158, 158, 158);
    internal static Color Filter     => IsDark ? Color.FromArgb(55, 49, 28) : Color.FromArgb(255, 252, 224);
    internal static Color Error      => IsDark ? Color.FromArgb(255, 112, 112) : Color.Crimson;
    internal static Color Warning    => IsDark ? Color.FromArgb(255, 190, 80) : Color.DarkOrange;

    internal static ApplicationTheme Parse(string? value) =>
        Enum.TryParse(value, true, out ApplicationTheme result) ? result : ApplicationTheme.Light;

    /// <summary>Call before the first HWND is created, then again for live changes.</summary>
    internal static void SetCurrent(ApplicationTheme theme)
    {
        Current = theme;
        NativeDarkMode.SetPreferredMode(IsDark);
    }


    internal static void ApplyTo(Control root)
    {
        ApplyManagedControl(root);
        foreach (Control child in root.Controls)
            ApplyTo(child);

        if (root.IsHandleCreated)
            ApplyNativeWindow(root.Handle, includeChildren: false);
        root.Invalidate(true);
    }

    private static void ApplyManagedControl(Control control)
    {
        control.ForeColor = Text;

        switch (control)
        {
            case ToolStrip strip:
                ApplyToolStrip(strip);
                return;
            case TextBoxBase textBox:
                textBox.BackColor = Window;
                textBox.ForeColor = Text;
                return;
            case ListView list:
                list.BackColor = Window;
                list.ForeColor = Text;
                return;
            case ComboBox combo:
                combo.BackColor = Window;
                combo.ForeColor = Text;
                return;
            case Button button:
                button.BackColor = Surface;
                button.ForeColor = Text;
                button.FlatStyle = FlatStyle.Flat;
                button.FlatAppearance.BorderColor = Border;
                button.FlatAppearance.BorderSize = 0;
                button.FlatAppearance.MouseOverBackColor = Hover;
                button.FlatAppearance.MouseDownBackColor = Pressed;
                button.Invalidate();
                return;
            default:
                control.BackColor = Background;
                break;
        }
    }

    private static GraphicsPath CreateRoundedPath(RectangleF bounds, float radius)
    {
        var path = new GraphicsPath();
        float diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        if (diameter <= 1)
        {
            path.AddRectangle(bounds);
            return path;
        }

        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter,
            diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    internal static void ApplyToolStrip(ToolStrip strip)
    {
        strip.BackColor = Surface;
        strip.ForeColor = Text;
        if (strip is ToolStripDropDown rootDropDown)
            rootDropDown.DropShadowEnabled = false;
        strip.RenderMode = ToolStripRenderMode.ManagerRenderMode;
        strip.Renderer = new ThemeRenderer();
        ApplyToolStripItems(strip.Items);
    }

    private static void ApplyToolStripItems(ToolStripItemCollection items)
    {
        foreach (ToolStripItem item in items)
        {
            item.ForeColor = Text;
            if (item is ToolStripDropDownItem dropDown)
            {
                dropDown.DropDown.BackColor = Surface;
                dropDown.DropDown.ForeColor = Text;
                dropDown.DropDown.DropShadowEnabled = false;
                dropDown.DropDown.RenderMode = ToolStripRenderMode.ManagerRenderMode;
                dropDown.DropDown.Renderer = new ThemeRenderer();
                ApplyToolStripItems(dropDown.DropDownItems);
            }
        }
    }

    internal static void ApplyNativeWindow(IntPtr hwnd, bool includeChildren = true)
    {
        if (hwnd == IntPtr.Zero) return;
        ApplyNativeWindowCore(hwnd);
        if (includeChildren)
            NativeMethods.EnumChildWindows(hwnd, (child, _) =>
            {
                ApplyNativeWindowCore(child);
                return true;
            }, IntPtr.Zero);
    }

    private static void ApplyNativeWindowCore(IntPtr hwnd)
    {
        // SHELLDLL_DefView owns the file/folder item renderer. Retheming either
        // that window or one of its DirectUI descendants after view creation can
        // leave selection present in IFolderView while removing its painted
        // background. The shell already creates this subtree using the process's
        // preferred app mode, so leave it entirely under shell control.
        if (IsShellViewWindow(hwnd)) return;

        var className = new StringBuilder(64);
        NativeMethods.GetClassName(hwnd, className, className.Capacity);
        string windowClass = className.ToString();

        // ExplorerBrowser's legacy command band has no usable dark canvas on
        // current Windows 11. Applying DarkMode_Explorer makes its text light
        // while Windows leaves the canvas white. Keep only the enclosing
        // DirectUI frame on the readable light Explorer theme; its navigation
        // tree and file view descendants still receive their dedicated dark
        // styling below (the Shell view also creates itself in dark mode).
        bool legacyExplorerFrame = IsDark
            && !windowClass.Equals("SysTreeView32", StringComparison.OrdinalIgnoreCase)
            && (windowClass.Equals("ExplorerBrowserControl", StringComparison.OrdinalIgnoreCase)
                || windowClass.Equals("MultiExplorerBrowserHost", StringComparison.OrdinalIgnoreCase)
                || IsExplorerBrowserFrameWindow(hwnd));
        bool useDarkTheme = IsDark && !legacyExplorerFrame;
        // ExplorerBrowser keeps its navigation tree alive across folder changes.
        // Re-sending WM_THEMECHANGED and forcing a recursive redraw on every
        // navigation makes that tree flash through the default white canvas.
        // A window property is automatically discarded when the HWND is destroyed,
        // so newly-created/reused Shell children still receive the current theme.
        if (!MarkNativeThemeIfChanged(hwnd, useDarkTheme))
            return;

        NativeDarkMode.AllowForWindow(hwnd, useDarkTheme);

        int dark = useDarkTheme ? 1 : 0;
        // Attribute 20 is supported by current Windows 10/11; 19 is the older name.
        if (NativeMethods.DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int)) != 0)
            NativeMethods.DwmSetWindowAttribute(hwnd, 19, ref dark, sizeof(int));

        string theme = useDarkTheme ? "DarkMode_Explorer" : "Explorer";
        NativeMethods.SetWindowTheme(hwnd, theme, null);

        uint background = ToColorRef(Window);
        uint foreground = ToColorRef(Text);
        if (windowClass.Equals("SysListView32", StringComparison.OrdinalIgnoreCase))
        {
            NativeMethods.SendMessageI(hwnd, 0x1001, IntPtr.Zero, (IntPtr)background); // LVM_SETBKCOLOR
            NativeMethods.SendMessageI(hwnd, 0x1024, IntPtr.Zero, (IntPtr)foreground); // LVM_SETTEXTCOLOR
            NativeMethods.SendMessageI(hwnd, 0x1026, IntPtr.Zero, (IntPtr)background); // LVM_SETTEXTBKCOLOR
        }
        else if (windowClass.Equals("SysTreeView32", StringComparison.OrdinalIgnoreCase))
        {
            NativeMethods.SendMessageI(hwnd, 0x111D, IntPtr.Zero, (IntPtr)background); // TVM_SETBKCOLOR
            NativeMethods.SendMessageI(hwnd, 0x111E, IntPtr.Zero, (IntPtr)foreground); // TVM_SETTEXTCOLOR
        }

        NativeMethods.SendMessageI(hwnd, 0x031A, IntPtr.Zero, IntPtr.Zero); // WM_THEMECHANGED
        NativeMethods.RedrawWindow(hwnd, IntPtr.Zero, IntPtr.Zero, 0x0085); // invalidate + frame + children
    }

    internal static bool MarkNativeThemeIfChanged(IntPtr hwnd, bool dark)
    {
        if (hwnd == IntPtr.Zero)
            throw new ArgumentException("A native window handle is required.", nameof(hwnd));

        IntPtr themeMarker = dark ? (IntPtr)2 : (IntPtr)1;
        if (GetProp(hwnd, NativeThemeMarkerProperty) == themeMarker)
            return false;

        // If SetProp fails, apply the theme anyway. The only consequence is that
        // a later pass may try again rather than leaving a window unthemed.
        SetProp(hwnd, NativeThemeMarkerProperty, themeMarker);
        return true;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetPropW")]
    private static extern IntPtr GetProp(IntPtr window, string propertyName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SetPropW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProp(IntPtr window, string propertyName,
        IntPtr data);

    private static string WindowClass(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return string.Empty;
        var className = new StringBuilder(64);
        NativeMethods.GetClassName(hwnd, className, className.Capacity);
        return className.ToString();
    }

    private static bool IsShellViewWindow(IntPtr hwnd)
    {
        for (IntPtr current = hwnd; current != IntPtr.Zero; current = NativeMethods.GetParent(current))
        {
            if (WindowClass(current).Equals("SHELLDLL_DefView", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static bool IsExplorerBrowserFrameWindow(IntPtr hwnd)
    {
        for (IntPtr current = hwnd; current != IntPtr.Zero; current = NativeMethods.GetParent(current))
        {
            if (WindowClass(current).Equals(
                    "DUIViewWndClassName",
                    StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static uint ToColorRef(Color color) =>
        (uint)(color.R | (color.G << 8) | (color.B << 16));

    private sealed class ThemeRenderer : ToolStripProfessionalRenderer
    {
        internal ThemeRenderer() : base(new ThemeColorTable())
        {
            RoundedEdges = false;
        }

        protected override void OnRenderButtonBackground(ToolStripItemRenderEventArgs e) =>
            DrawThemedButtonBackground(e);

        protected override void OnRenderDropDownButtonBackground(ToolStripItemRenderEventArgs e) =>
            DrawThemedButtonBackground(e);

        private void DrawThemedButtonBackground(ToolStripItemRenderEventArgs e)
        {
            bool isChecked = e.Item is ToolStripButton button && button.Checked;
            bool drawBorder = e.Item.Selected || e.Item.Pressed || isChecked;
            Color fill = e.Item.Pressed ? Pressed
                : drawBorder ? Hover
                : Surface;
            int radius = Math.Max(2,
                ButtonCornerRadius * (e.ToolStrip?.DeviceDpi ?? 96) / 96);
            var bounds = new Rectangle(1, 1,
                Math.Max(1, e.Item.Width - 2), Math.Max(1, e.Item.Height - 2));
            using GraphicsPath path = CreateRoundedPath(bounds, radius);
            SmoothingMode previousMode = e.Graphics.SmoothingMode;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var brush = new SolidBrush(fill))
                e.Graphics.FillPath(brush, path);
            if (drawBorder)
            {
                using var pen = new Pen(Border);
                e.Graphics.DrawPath(pen, path);
            }
            e.Graphics.SmoothingMode = previousMode;
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            bool highlightedMenuItem = e.Item.Selected && e.ToolStrip is ToolStripDropDown;
            e.TextColor = highlightedMenuItem
                ? MenuHighlightText
                : e.Item.Enabled ? Text : MutedText;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected)
            {
                base.OnRenderMenuItemBackground(e);
                return;
            }

            using var brush = new SolidBrush(MenuHighlight);
            if (e.ToolStrip is ToolStripDropDown && IsLastVisibleItem(e.Item))
            {
                int radius = Math.Max(8,
                    PopupVisuals.CornerRadiusLogical * e.ToolStrip.DeviceDpi / 96);
                using GraphicsPath path = PopupVisuals.CreateBottomRoundedPath(
                    new RectangleF(1, 0, Math.Max(1, e.Item.Width - 2f),
                        Math.Max(1, e.Item.Height - 1f)), Math.Max(1, radius - 1));
                SmoothingMode previousMode = e.Graphics.SmoothingMode;
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                e.Graphics.FillPath(brush, path);
                e.Graphics.SmoothingMode = previousMode;
            }
            else
            {
                e.Graphics.FillRectangle(brush,
                    new Rectangle(Point.Empty, e.Item.Size));
            }
        }

        private static bool IsLastVisibleItem(ToolStripItem item)
        {
            if (item.Owner is null) return false;
            for (int index = item.Owner.Items.Count - 1; index >= 0; index--)
            {
                ToolStripItem candidate = item.Owner.Items[index];
                if (candidate.Available) return ReferenceEquals(candidate, item);
            }
            return false;
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            if (e.ToolStrip is not ToolStripDropDown)
            {
                base.OnRenderToolStripBorder(e);
                return;
            }

            int radius = Math.Max(8,
                PopupVisuals.CornerRadiusLogical * e.ToolStrip.DeviceDpi / 96);
            var bounds = new RectangleF(0.5f, 0.5f,
                Math.Max(1, e.ToolStrip.Width - 1f),
                Math.Max(1, e.ToolStrip.Height - 1f));
            using GraphicsPath path = PopupVisuals.CreateBottomRoundedPath(bounds, radius);
            using var pen = new Pen(Border);
            SmoothingMode previousMode = e.Graphics.SmoothingMode;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.DrawPath(pen, path);
            e.Graphics.SmoothingMode = previousMode;
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = e.Item?.Enabled != false ? Text : MutedText;
            base.OnRenderArrow(e);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            Rectangle r = e.ImageRectangle;
            int cx = r.Left + r.Width / 2;
            int cy = r.Top + r.Height / 2;
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var pen = new Pen(Text, 1.8f);
            e.Graphics.DrawLines(pen,
            new Point[]
            {
                new Point(cx - 5, cy),
                new Point(cx - 1, cy + 4),
                new Point(cx + 6, cy - 5),
            });
        }
    }

    private sealed class ThemeColorTable : ProfessionalColorTable
    {
        public override Color ToolStripGradientBegin => Surface;
        public override Color ToolStripGradientMiddle => Surface;
        public override Color ToolStripGradientEnd => Surface;
        public override Color ToolStripBorder => Border;
        public override Color StatusStripGradientBegin => Surface;
        public override Color StatusStripGradientEnd => Surface;
        public override Color ToolStripDropDownBackground => Surface;
        public override Color ImageMarginGradientBegin => Surface;
        public override Color ImageMarginGradientMiddle => Surface;
        public override Color ImageMarginGradientEnd => Surface;
        public override Color MenuBorder => Border;
        public override Color MenuItemBorder => MenuHighlight;
        public override Color MenuItemSelected => MenuHighlight;
        public override Color MenuItemSelectedGradientBegin => MenuHighlight;
        public override Color MenuItemSelectedGradientEnd => MenuHighlight;
        public override Color MenuItemPressedGradientBegin => Pressed;
        public override Color MenuItemPressedGradientMiddle => Pressed;
        public override Color MenuItemPressedGradientEnd => Pressed;
        public override Color ButtonSelectedBorder => Border;
        public override Color ButtonSelectedGradientBegin => Hover;
        public override Color ButtonSelectedGradientMiddle => Hover;
        public override Color ButtonSelectedGradientEnd => Hover;
        public override Color ButtonPressedBorder => Border;
        public override Color ButtonPressedGradientBegin => Pressed;
        public override Color ButtonPressedGradientMiddle => Pressed;
        public override Color ButtonPressedGradientEnd => Pressed;
        public override Color SeparatorDark => Border;
        public override Color SeparatorLight => Border;
        public override Color CheckBackground => Hover;
        public override Color CheckSelectedBackground => Hover;
        public override Color CheckPressedBackground => Pressed;
    }

    private static class NativeDarkMode
    {
        private static int _preferredModeWarningReported;
        private static int _windowModeWarningReported;

        internal static void SetPreferredMode(bool dark)
        {
            try
            {
                SetPreferredAppMode(dark ? 2 : 3); // ForceDark / ForceLight
                FlushMenuThemes();
            }
            catch (Exception ex) when (ex is EntryPointNotFoundException
                                       or DllNotFoundException)
            {
                ReportOnce(ref _preferredModeWarningReported, ex,
                    "Windows dark-mode integration is unavailable. Some native "
                    + "menus may not match the selected theme. See app.log for details.");
            }
        }

        internal static void AllowForWindow(IntPtr hwnd, bool dark)
        {
            try { AllowDarkModeForWindow(hwnd, dark); }
            catch (Exception ex) when (ex is EntryPointNotFoundException
                                       or DllNotFoundException)
            {
                ReportOnce(ref _windowModeWarningReported, ex,
                    "Windows could not apply the selected theme to a native window. "
                    + "See app.log for details.");
            }
        }

        private static void ReportOnce(
            ref int warningReported, Exception exception, string message)
        {
            if (Interlocked.Exchange(ref warningReported, 1) == 0)
                AppLog.Warn(exception, nameof(ThemeManager), message);
        }

        [DllImport("uxtheme.dll", EntryPoint = "#135")]
        private static extern int SetPreferredAppMode(int appMode);

        [DllImport("uxtheme.dll", EntryPoint = "#133")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AllowDarkModeForWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool allow);

        [DllImport("uxtheme.dll", EntryPoint = "#136")]
        private static extern void FlushMenuThemes();
    }
}
