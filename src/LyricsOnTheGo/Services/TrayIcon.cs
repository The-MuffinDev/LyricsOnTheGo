using System;
using System.Drawing;
using System.Windows.Forms;

namespace LyricsOnTheGo.Services;

/// <summary>
/// System tray icon (README §3.1) built on the framework's <see cref="NotifyIcon"/> — no
/// external dependency. Menu: Show/Hide, Click-through (checkable), Diagnostics, Quit. Left-click
/// toggles visibility. Labels follow the current language; the click-through item mirrors the
/// setting and is the recovery path when the overlay is click-through (README §3.7).
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _showHide;
    private readonly ToolStripMenuItem _clickThrough;
    private readonly ToolStripMenuItem _diagnostics;
    private readonly ToolStripMenuItem _quit;

    private bool _shown = true;

    /// <summary>Left-click or the Show/Hide menu item was activated.</summary>
    public event Action? ToggleVisibilityRequested;

    /// <summary>The click-through menu item was toggled; the bool is the new desired state.</summary>
    public event Action<bool>? ClickThroughToggled;

    /// <summary>The Diagnostics menu item was activated.</summary>
    public event Action? DiagnosticsRequested;

    /// <summary>The Quit menu item was activated (the only real exit).</summary>
    public event Action? QuitRequested;

    public TrayIcon()
    {
        _showHide = new ToolStripMenuItem();
        _showHide.Click += (_, _) => ToggleVisibilityRequested?.Invoke();

        _clickThrough = new ToolStripMenuItem { CheckOnClick = true };
        _clickThrough.Click += (_, _) => ClickThroughToggled?.Invoke(_clickThrough.Checked);

        _diagnostics = new ToolStripMenuItem();
        _diagnostics.Click += (_, _) => DiagnosticsRequested?.Invoke();

        _quit = new ToolStripMenuItem();
        _quit.Click += (_, _) => QuitRequested?.Invoke();

        var menu = new ContextMenuStrip();
        menu.Items.Add(_showHide);
        menu.Items.Add(_clickThrough);
        menu.Items.Add(_diagnostics);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_quit);

        _icon = new NotifyIcon
        {
            Text = "LyricsOnTheGo",
            Visible = true,
            ContextMenuStrip = menu,
            Icon = LoadAppIcon(),
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                ToggleVisibilityRequested?.Invoke();
        };

        Localize();
        I18n.Changed += Localize;
    }

    /// <summary>Reflect the overlay's current visibility in the Show/Hide label.</summary>
    public void SetVisibleState(bool shown)
    {
        _shown = shown;
        _showHide.Text = shown ? I18n.T("trayHide") : I18n.T("trayShow");
    }

    /// <summary>Sync the checkable item with the click-through setting (no event raised).</summary>
    public void SetClickThroughChecked(bool on) => _clickThrough.Checked = on;

    private void Localize()
    {
        _showHide.Text = _shown ? I18n.T("trayHide") : I18n.T("trayShow");
        _clickThrough.Text = I18n.T("trayClickThrough");
        _diagnostics.Text = I18n.T("trayDiagnostics");
        _quit.Text = I18n.T("trayQuit");
    }

    private static Icon LoadAppIcon()
    {
        try
        {
            string? exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
            {
                Icon? extracted = Icon.ExtractAssociatedIcon(exe);
                if (extracted is not null)
                    return extracted;
            }
        }
        catch { /* fall back to the generic application icon */ }
        return SystemIcons.Application;
    }

    public void Dispose()
    {
        I18n.Changed -= Localize;
        _icon.Visible = false;
        _icon.Dispose();
    }
}
