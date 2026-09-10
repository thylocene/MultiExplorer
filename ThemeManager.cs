using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Text;
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

    internal static ApplicationTheme Current { get; private set; } = ApplicationTheme.Light;
    internal static bool IsDark => Current == ApplicationTheme.Dark;

    internal static Color Background => IsDark ? Color.FromArgb(32, 32, 32) : Color.FromArgb(243, 243, 243);
    internal static Color Surface    => IsDark ? Color.FromArgb(40, 40, 40) : Color.FromArgb(250, 250, 250);
    internal static Color Window     => IsDark ? Color.FromArgb(30, 30, 30) : Color.White;
    internal static Color Text       => IsDark ? Color.FromArgb(245, 245, 245) : Color.FromArgb(25, 25, 25);
    internal static Color MutedText  => IsDark ? Color.FromArgb(190, 190, 190) : Color.FromArgb(96, 96, 96);
    internal static Color Border     => IsDark ? Color.FromArgb(82, 82, 82) : Color.FromArgb(190, 190, 190);
    internal static Color Hover      => IsDark ? Color.FromArgb(62, 62, 62) : Color.FromArgb(224, 224, 224);
    internal static Color Pressed    => IsDark ? Color.FromArgb(76, 76, 76) : Color.FromArgb(207, 207, 207);
    internal static Color Accent     => Color.FromArgb(0, 120, 212);
    // The standard accent is intended primarily for filled controls.  Use a
    // lighter variant when it is rendered as foreground text on dark surfaces.
    internal static Color AccentText => IsDark ? Color.FromArgb(96, 205, 255) : Accent;
    internal static Color AccentHover => IsDark ? Color.FromArgb(48, 68, 84) : Color.FromArgb(222, 238, 250);
    internal static Color NavigationBorder => IsDark ? Color.FromArgb(105, 105, 105) : Color.FromArgb(158, 158, 158);
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

        NativeDarkMode.AllowForWindow(hwnd, IsDark);

        int dark = IsDark ? 1 : 0;
        // Attribute 20 is supported by current Windows 10/11; 19 is the older name.
        if (NativeMethods.DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int)) != 0)
            NativeMethods.DwmSetWindowAttribute(hwnd, 19, ref dark, sizeof(int));

        var className = new StringBuilder(64);
        NativeMethods.GetClassName(hwnd, className, className.Capacity);
        string windowClass = className.ToString();

        string theme = IsDark ? "DarkMode_Explorer" : "Explorer";
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

    private static uint ToColorRef(Color color) =>
        (uint)(color.R | (color.G << 8) | (color.B << 16));

    private sealed class ThemeRenderer : ToolStripProfessionalRenderer
    {
        internal ThemeRenderer() : base(new ThemeColorTable()) { RoundedEdges = false; }

        protected override void OnRenderButtonBackground(ToolStripItemRenderEventArgs e) =>
            DrawThemedButtonBackground(e);

        protected override void OnRenderDropDownButtonBackground(ToolStripItemRenderEventArgs e) =>
            DrawThemedButtonBackground(e);

        private static void DrawThemedButtonBackground(ToolStripItemRenderEventArgs e)
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
            e.TextColor = e.Item.Enabled ? Text : MutedText;
            base.OnRenderItemText(e);
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
        public override Color StatusStripGradientBegin => Surface;
        public override Color StatusStripGradientEnd => Surface;
        public override Color ToolStripDropDownBackground => Surface;
        public override Color ImageMarginGradientBegin => Surface;
        public override Color ImageMarginGradientMiddle => Surface;
        public override Color ImageMarginGradientEnd => Surface;
        public override Color MenuBorder => Border;
        public override Color MenuItemBorder => Border;
        public override Color MenuItemSelected => Hover;
        public override Color MenuItemSelectedGradientBegin => Hover;
        public override Color MenuItemSelectedGradientEnd => Hover;
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
        public override Color SeparatorLight => Surface;
        public override Color CheckBackground => Hover;
        public override Color CheckSelectedBackground => Hover;
        public override Color CheckPressedBackground => Pressed;
    }

    private static class NativeDarkMode
    {
        internal static void SetPreferredMode(bool dark)
        {
            try
            {
                SetPreferredAppMode(dark ? 2 : 3); // ForceDark / ForceLight
                FlushMenuThemes();
            }
            catch (EntryPointNotFoundException) { }
            catch (DllNotFoundException) { }
        }

        internal static void AllowForWindow(IntPtr hwnd, bool dark)
        {
            try { AllowDarkModeForWindow(hwnd, dark); }
            catch (EntryPointNotFoundException) { }
            catch (DllNotFoundException) { }
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
