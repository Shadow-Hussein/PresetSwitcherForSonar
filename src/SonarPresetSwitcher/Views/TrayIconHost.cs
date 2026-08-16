using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using SonarPresetSwitcher.Services;
using SonarPresetSwitcher.ViewModels;
using Application = System.Windows.Application;

namespace SonarPresetSwitcher.Views;

/// <summary>
/// System-tray presence, built on the WinForms NotifyIcon so the app needs no third-party package.
/// ShowBalloonTip is rendered as a real toast by Windows 10/11, which avoids retargeting the project
/// at a Windows SDK TFM and registering an AUMID shortcut just to raise a notification.
/// </summary>
public sealed class TrayIconHost : IDisposable
{
    private const int BalloonTimeoutMs = 4000;

    private readonly NotifyIcon _icon;
    private readonly MainWindow _window;
    private readonly MainViewModel _viewModel;
    private readonly ToolStripMenuItem _presetItem;
    private readonly ToolStripMenuItem _pauseItem;
    private readonly Icon? _appIcon;

    public TrayIconHost(MainWindow window, MainViewModel viewModel, PresetCoordinator coordinator)
    {
        _window = window;
        _viewModel = viewModel;

        _presetItem = new ToolStripMenuItem("Game preset: —") { Enabled = false };
        _pauseItem = new ToolStripMenuItem("Pause monitoring") { CheckOnClick = true };
        _pauseItem.CheckedChanged += (_, _) => _viewModel.MonitoringPaused = _pauseItem.Checked;

        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("Open", null, (_, _) => ShowWindow()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_presetItem);
        menu.Items.Add(_pauseItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => Exit()));

        _appIcon = LoadAppIcon();

        _icon = new NotifyIcon
        {
            Icon = _appIcon ?? SystemIcons.Application,
            Text = "Sonar Preset Switcher",
            Visible = true,
            ContextMenuStrip = menu,
        };

        // Right-click is handled separately by the ContextMenuStrip, so this only ever fires for a
        // plain left click. DoubleClick stays because ShowFromTray is idempotent.
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                ShowWindow();
        };
        _icon.DoubleClick += (_, _) => ShowWindow();
        _icon.BalloonTipClicked += (_, _) => ShowWindow();

        coordinator.StateChanged += OnStateChanged;
        coordinator.PresetSwitched += OnPresetSwitched;
    }

    /// <summary>
    /// The icon has to come out of the assembly rather than off disk: the publish profile is
    /// single-file and self-contained, so there is no loose Assets folder next to the exe.
    /// </summary>
    private static Icon? LoadAppIcon()
    {
        try
        {
            var resource = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"));
            if (resource is null)
                return null;

            using var stream = resource.Stream;
            return new Icon(stream, SystemInformation.SmallIconSize);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Tray icon load failed: {ex.Message}");
            return null;
        }
    }

    private void OnStateChanged(CoordinatorState state)
    {
        var preset = state.CurrentPresetName ?? "—";
        var status = state.Connected ? preset : "Sonar not running";

        Application.Current?.Dispatcher.Invoke(() =>
        {
            _presetItem.Text = $"Game preset: {preset}";

            // NotifyIcon.Text is capped at 63 characters by the shell.
            var tooltip = $"Sonar Preset Switcher — {status}";
            _icon.Text = tooltip.Length > 63 ? tooltip[..63] : tooltip;
        });
    }

    private void OnPresetSwitched(PresetSwitchNotice notice)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            if (!_viewModel.ShowNotifications)
                return;

            var body = notice.GameName is null
                ? $"Reverted to \"{notice.PresetName}\""
                : $"Applied \"{notice.PresetName}\" for {notice.GameName}";

            if (notice.OutputDeviceName is not null)
                body += $"\nOutput → {notice.OutputDeviceName}";

            _icon.ShowBalloonTip(BalloonTimeoutMs, "Sonar Preset Switcher", body, ToolTipIcon.Info);
        });
    }

    private void ShowWindow() => Application.Current?.Dispatcher.Invoke(_window.ShowFromTray);

    private void Exit()
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            _window.ForceClose = true;
            Application.Current.Shutdown();
        });
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _appIcon?.Dispose();
    }
}
