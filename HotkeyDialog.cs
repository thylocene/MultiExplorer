using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace MultiExplorer;

/// <summary>
/// Modal dialog that lets the user choose a global hotkey combination.
/// Requires at least one modifier key (Win/Ctrl/Alt/Shift) and a letter or function key.
/// </summary>
internal sealed class HotkeyDialog : Form
{
    public int SelectedModifiers { get; private set; }
    public int SelectedVk        { get; private set; }

    private readonly CheckBox _cbWin, _cbCtrl, _cbAlt, _cbShift;
    private readonly ComboBox _cboKey;
    private readonly Label    _lblPreview;
    private readonly Button   _btnOk;

    private static readonly (string Name, int Vk)[] _keys = BuildKeys();

    public HotkeyDialog(int currentModifiers, int currentVk)
    {
        Text            = "Set show-window hotkey";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox     = false;
        MinimizeBox     = false;
        ShowInTaskbar   = false;
        StartPosition   = FormStartPosition.CenterParent;
        Font            = SystemFonts.MessageBoxFont ?? new Font("Segoe UI", 9f);

        // ── Outer grid: 4 rows × 2 columns (label | control) ─────────────────
        var outer = new TableLayoutPanel
        {
            Padding      = new Padding(12),
            ColumnCount  = 2,
            RowCount     = 4,
            AutoSize     = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        outer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        outer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        outer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        outer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        outer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        outer.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        // ── Row 0: modifier checkboxes ────────────────────────────────────────
        // A 4-column inner TableLayoutPanel guarantees all four checkboxes stay
        // on a single row — unlike FlowLayoutPanel, a grid cannot wrap.
        outer.Controls.Add(RowLabel("Modifiers:"), 0, 0);
        var cbTable = new TableLayoutPanel
        {
            AutoSize     = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount  = 4,
            RowCount     = 1,
            Anchor       = AnchorStyles.Left | AnchorStyles.Top,
            Margin       = new Padding(0, 2, 0, 2),
        };
        cbTable.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        cbTable.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        cbTable.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        cbTable.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        cbTable.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _cbWin   = new CheckBox { Text = "Win",   AutoSize = true, Margin = new Padding(0, 0, 10, 0) };
        _cbCtrl  = new CheckBox { Text = "Ctrl",  AutoSize = true, Margin = new Padding(0, 0, 10, 0) };
        _cbAlt   = new CheckBox { Text = "Alt",   AutoSize = true, Margin = new Padding(0, 0, 10, 0) };
        _cbShift = new CheckBox { Text = "Shift", AutoSize = true, Margin = new Padding(0) };
        cbTable.Controls.Add(_cbWin,   0, 0);
        cbTable.Controls.Add(_cbCtrl,  1, 0);
        cbTable.Controls.Add(_cbAlt,   2, 0);
        cbTable.Controls.Add(_cbShift, 3, 0);
        outer.Controls.Add(cbTable, 1, 0);

        // ── Row 1: key selector ───────────────────────────────────────────────
        outer.Controls.Add(RowLabel("Key:"), 0, 1);
        _cboKey = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width         = 120,
            Anchor        = AnchorStyles.Left | AnchorStyles.Top,
            Margin        = new Padding(0, 3, 0, 3),
        };
        foreach (var (name, _) in _keys) _cboKey.Items.Add(name);
        outer.Controls.Add(_cboKey, 1, 1);

        // ── Row 2: live preview ───────────────────────────────────────────────
        _lblPreview = new Label
        {
            AutoSize  = true,
            TextAlign = ContentAlignment.MiddleCenter,
            Font      = new Font("Segoe UI", 11f, FontStyle.Bold),
            Anchor    = AnchorStyles.None,
            Margin    = new Padding(0, 8, 0, 8),
        };
        outer.SetColumnSpan(_lblPreview, 2);
        outer.Controls.Add(_lblPreview, 0, 2);

        // ── Row 3: OK / Cancel buttons (right-aligned) ────────────────────────
        // RightToLeft flow: Cancel added first (rightmost), OK added second.
        var btnFlow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize      = true,
            AutoSizeMode  = AutoSizeMode.GrowAndShrink,
            WrapContents  = false,
            Anchor        = AnchorStyles.Right | AnchorStyles.Top,
            Margin        = new Padding(0, 6, 0, 0),
        };
        var btnCancel = new RoundedButton
        {
            Text         = "Cancel",
            AutoSize     = false,
            Size         = new Size(80, 28),
            Margin       = new Padding(4, 0, 0, 0),
            DialogResult = DialogResult.Cancel,
        };
        _btnOk = new RoundedButton
        {
            Text         = "OK",
            AutoSize     = false,
            Size         = new Size(80, 28),
            Margin       = new Padding(4, 0, 0, 0),
            DialogResult = DialogResult.OK,
        };
        _btnOk.Click += (_, _) =>
        {
            SelectedModifiers = ReadModifiers();
            SelectedVk        = ReadVk();
        };
        btnFlow.Controls.Add(btnCancel);
        btnFlow.Controls.Add(_btnOk);
        outer.SetColumnSpan(btnFlow, 2);
        outer.Controls.Add(btnFlow, 0, 3);

        AcceptButton = _btnOk;
        CancelButton = btnCancel;
        Controls.Add(outer);

        // AutoScale is set AFTER controls are added so WinForms scales the
        // already-constructed control tree when the form handle is created.
        // MinimumSize is a belt-and-suspenders floor in case the AutoSize
        // computation produces a narrower form than the checkboxes need.
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode       = AutoScaleMode.Dpi;
        AutoSize            = true;
        AutoSizeMode        = AutoSizeMode.GrowAndShrink;
        MinimumSize         = new Size(460, 0);

        // ── Initialise from current values ────────────────────────────────────
        _cbWin.Checked   = (currentModifiers & NativeMethods.MOD_WIN)     != 0;
        _cbCtrl.Checked  = (currentModifiers & NativeMethods.MOD_CONTROL) != 0;
        _cbAlt.Checked   = (currentModifiers & NativeMethods.MOD_ALT)     != 0;
        _cbShift.Checked = (currentModifiers & NativeMethods.MOD_SHIFT)   != 0;

        int keyIdx = Array.FindIndex(_keys, k => k.Vk == currentVk);
        _cboKey.SelectedIndex = keyIdx >= 0 ? keyIdx : Array.FindIndex(_keys, k => k.Name == "M");

        _cbWin.CheckedChanged        += (_, _) => UpdatePreview();
        _cbCtrl.CheckedChanged       += (_, _) => UpdatePreview();
        _cbAlt.CheckedChanged        += (_, _) => UpdatePreview();
        _cbShift.CheckedChanged      += (_, _) => UpdatePreview();
        _cboKey.SelectedIndexChanged += (_, _) => UpdatePreview();

        UpdatePreview();
        ThemeManager.ApplyTo(this);
        Shown += (_, _) => ThemeManager.ApplyNativeWindow(Handle);
    }

    private void UpdatePreview()
    {
        int mods = ReadModifiers();
        _lblPreview.Text = Describe(mods, ReadVk());
        _btnOk.Enabled   = mods != 0 && _cboKey.SelectedIndex >= 0;
    }

    private int ReadModifiers()
    {
        int m = 0;
        if (_cbWin.Checked)   m |= NativeMethods.MOD_WIN;
        if (_cbCtrl.Checked)  m |= NativeMethods.MOD_CONTROL;
        if (_cbAlt.Checked)   m |= NativeMethods.MOD_ALT;
        if (_cbShift.Checked) m |= NativeMethods.MOD_SHIFT;
        return m;
    }

    private int ReadVk() =>
        _cboKey.SelectedIndex >= 0 ? _keys[_cboKey.SelectedIndex].Vk : NativeMethods.VK_M;

    /// <summary>Returns a human-readable description like "Win + Shift + M".</summary>
    internal static string Describe(int modifiers, int vk)
    {
        var parts = new List<string>();
        if ((modifiers & NativeMethods.MOD_WIN)     != 0) parts.Add("Win");
        if ((modifiers & NativeMethods.MOD_CONTROL) != 0) parts.Add("Ctrl");
        if ((modifiers & NativeMethods.MOD_ALT)     != 0) parts.Add("Alt");
        if ((modifiers & NativeMethods.MOD_SHIFT)   != 0) parts.Add("Shift");
        foreach (var (name, v) in _keys)
            if (v == vk) { parts.Add(name); break; }
        return parts.Count > 0 ? string.Join(" + ", parts) : "(none)";
    }

    private static Label RowLabel(string text) => new Label
    {
        Text     = text,
        AutoSize = true,
        Anchor   = AnchorStyles.Left | AnchorStyles.Top,
        Margin   = new Padding(0, 5, 8, 3),
    };

    private static (string Name, int Vk)[] BuildKeys()
    {
        var list = new List<(string, int)>();
        for (char c = 'A'; c <= 'Z'; c++) list.Add((c.ToString(), (int)c));
        for (int i = 1; i <= 12; i++)     list.Add(($"F{i}", 0x6F + i));
        return list.ToArray();
    }
}
