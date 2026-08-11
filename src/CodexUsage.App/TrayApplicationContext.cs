using System.Diagnostics;
using CodexUsage.App.Settings;
using CodexUsage.App.UI;
using CodexUsage.Errors;
using CodexUsage.Formatting;
using CodexUsage.History;
using CodexUsage.Models;
using CodexUsage.Services;

namespace CodexUsage.App;

public sealed class TrayApplicationContext : ApplicationContext
{
    private readonly CodexUsageService _usageService = new();
    private readonly AppSettingsStore _settingsStore = new();
    private readonly UsageHistoryStore _historyStore = new();
    private readonly PopupForm _popup;
    private readonly NotifyIcon _trayIcon;
    private readonly System.Windows.Forms.Timer _refreshTimer = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ToolStripMenuItem _refreshMenuItem;
    private readonly ToolStripMenuItem _startupMenuItem;
    private readonly HashSet<string> _sentNotificationKeys = new(StringComparer.Ordinal);

    private AppSettings _settings;
    private UsageSnapshot? _snapshot;
    private string? _lastError;
    private Icon? _renderedIcon;
    private HistoryForm? _historyForm;
    private bool _refreshing;
    private bool _firstIdleHandled;

    public TrayApplicationContext()
    {
        _settings = _settingsStore.Load();
        _popup = new PopupForm(_settings);
        _popup.RefreshRequested += async (_, _) => await RefreshAsync();
        _popup.SettingsRequested += (_, _) => ShowSettings();

        _refreshMenuItem = new ToolStripMenuItem("Refresh now");
        _refreshMenuItem.Click += async (_, _) => await RefreshAsync();

        _startupMenuItem = new ToolStripMenuItem("Start with Windows")
        {
            CheckOnClick = true,
            Checked = SafeStartupEnabled(),
        };
        _startupMenuItem.Click += (_, _) => ToggleStartupFromMenu();

        var settingsItem = new ToolStripMenuItem("Settings…");
        settingsItem.Click += (_, _) => ShowSettings();

        var historyItem = new ToolStripMenuItem("Usage history…");
        historyItem.Click += (_, _) => ShowHistory();

        var openFolderItem = new ToolStripMenuItem("Open Codex folder");
        openFolderItem.Click += (_, _) => OpenCodexFolder();

        var quitItem = new ToolStripMenuItem("Quit");
        quitItem.Click += (_, _) => ExitApplication();

        var menu = new ContextMenuStrip();
        menu.Items.AddRange(
        [
            _refreshMenuItem,
            historyItem,
            new ToolStripSeparator(),
            _startupMenuItem,
            settingsItem,
            openFolderItem,
            new ToolStripSeparator(),
            quitItem,
        ]);

        _trayIcon = new NotifyIcon
        {
            ContextMenuStrip = menu,
            Text = "Codex Usage · Loading",
            Visible = true,
        };
        _trayIcon.MouseClick += OnTrayMouseClick;

        _refreshTimer.Interval = checked(_settings.RefreshIntervalMinutes * 60_000);
        _refreshTimer.Tick += async (_, _) => await RefreshAsync();
        _refreshTimer.Start();

        UpdateIcon(error: false);
        Application.Idle += OnFirstIdle;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _shutdown.Cancel();
            _refreshTimer.Stop();
            _refreshTimer.Dispose();
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _renderedIcon?.Dispose();
            _historyForm?.Dispose();
            _popup.Dispose();
            _usageService.Dispose();
            _shutdown.Dispose();
        }

        base.Dispose(disposing);
    }

    private async void OnFirstIdle(object? sender, EventArgs eventArgs)
    {
        if (_firstIdleHandled)
        {
            return;
        }

        _firstIdleHandled = true;
        Application.Idle -= OnFirstIdle;
        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        if (_refreshing)
        {
            return;
        }

        _refreshing = true;
        _refreshMenuItem.Enabled = false;
        _popup.UpdateState(_snapshot, refreshing: true, error: null);
        UpdateIcon(error: false);

        try
        {
            var previous = _snapshot;
            _snapshot = await _usageService.FetchAsync(_shutdown.Token);
            var history = _historyStore.Record(_snapshot);
            if (_historyForm is { IsDisposed: false })
            {
                _historyForm.UpdateHistory(history);
            }

            _lastError = null;
            ShowThresholdNotification(previous, _snapshot);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            return;
        }
        catch (CodexUsageException exception)
        {
            _lastError = exception.Message;
        }
        catch (Exception)
        {
            _lastError = "Codex usage could not be refreshed. Try again in a moment.";
        }
        finally
        {
            _refreshing = false;
            _refreshMenuItem.Enabled = true;
            _popup.UpdateState(_snapshot, refreshing: false, error: _lastError);
            UpdateIcon(error: _lastError is not null);
        }
    }

    private void OnTrayMouseClick(object? sender, MouseEventArgs eventArgs)
    {
        if (eventArgs.Button != MouseButtons.Left)
        {
            return;
        }

        if (_popup.Visible)
        {
            _popup.Hide();
            return;
        }

        _popup.ApplyTheme(_settings.Theme);
        _popup.UpdateState(_snapshot, _refreshing, _lastError);
        _popup.ShowNearTray();
    }

    private void UpdateIcon(bool error)
    {
        var palette = ThemePalette.Resolve(_settings.Theme);
        var iconSize = Math.Clamp(SystemInformation.SmallIconSize.Width, 16, 64);
        var icon = TrayIconRenderer.Create(
            _snapshot?.LowestAvailablePercent ?? 100,
            palette,
            error,
            _refreshing,
            iconSize);
        _trayIcon.Icon = icon;
        var previous = _renderedIcon;
        _renderedIcon = icon;
        previous?.Dispose();

        _trayIcon.Text = BuildTooltip(error);
    }

    private string BuildTooltip(bool error)
    {
        if (error && _snapshot is null)
        {
            return "Codex Usage · Refresh failed";
        }

        if (_snapshot is null)
        {
            return "Codex · 100% available · Loading";
        }

        var session = _snapshot.Primary is null ? "—" : $"{_snapshot.Primary.AvailablePercent:0}%";
        var weekly = _snapshot.Secondary is null ? "—" : $"{_snapshot.Secondary.AvailablePercent:0}%";
        var tooltip = $"Codex available · 5h {session} · Week {weekly}";
        return tooltip.Length <= 63 ? tooltip : tooltip[..63];
    }

    private void ShowThresholdNotification(UsageSnapshot? previous, UsageSnapshot current)
    {
        if (!_settings.NotificationsEnabled)
        {
            return;
        }

        var threshold = _settings.NotificationThresholdPercent;
        var windows = new[]
        {
            (Name: "5-hour", Current: current.Primary, Previous: previous?.Primary),
            (Name: "weekly", Current: current.Secondary, Previous: previous?.Secondary),
        };

        foreach (var window in windows)
        {
            if (window.Current is null || window.Current.AvailablePercent > threshold)
            {
                continue;
            }

            var resetKey = window.Current.ResetsAt?.ToUnixTimeSeconds().ToString() ?? "unknown";
            var key = $"{window.Name}:{threshold}:{resetKey}";
            var crossedThreshold = window.Previous is null || window.Previous.AvailablePercent > threshold;
            if (!crossedThreshold || !_sentNotificationKeys.Add(key))
            {
                continue;
            }

            _trayIcon.BalloonTipTitle = window.Current.AvailablePercent <= 0
                ? $"Codex {window.Name} limit reached"
                : $"Codex {window.Name} availability is low";
            _trayIcon.BalloonTipText =
                $"{window.Current.AvailablePercent:0.#}% available. {UsageText.ResetDescription(window.Current.ResetsAt, DateTimeOffset.Now)}";
            _trayIcon.BalloonTipIcon = window.Current.AvailablePercent <= 0
                ? ToolTipIcon.Warning
                : ToolTipIcon.Info;
            _trayIcon.ShowBalloonTip(5000);
        }

        if (_sentNotificationKeys.Count > 20)
        {
            _sentNotificationKeys.RemoveWhere(item => !item.EndsWith(
                current.Primary?.ResetsAt?.ToUnixTimeSeconds().ToString() ?? string.Empty,
                StringComparison.Ordinal)
                && !item.EndsWith(
                    current.Secondary?.ResetsAt?.ToUnixTimeSeconds().ToString() ?? string.Empty,
                    StringComparison.Ordinal));
        }
    }

    private void ShowSettings()
    {
        _popup.Hide();
        using var form = new SettingsForm(_settings, SafeStartupEnabled());
        if (form.ShowDialog() != DialogResult.OK)
        {
            return;
        }

        try
        {
            StartupManager.SetEnabled(form.LaunchAtStartup);
            _startupMenuItem.Checked = form.LaunchAtStartup;
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                $"Windows startup could not be updated: {exception.Message}",
                "Codex Usage",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }

        _settings = form.SelectedSettings.Normalize();
        _settingsStore.Save(_settings);
        _refreshTimer.Interval = checked(_settings.RefreshIntervalMinutes * 60_000);
        _popup.ApplyTheme(_settings.Theme);
        _historyForm?.ApplyTheme(_settings.Theme);
        UpdateIcon(error: false);
    }

    private void ShowHistory()
    {
        _popup.Hide();
        if (_historyForm is null || _historyForm.IsDisposed)
        {
            var form = new HistoryForm(_settings);
            form.FormClosed += (_, _) =>
            {
                if (ReferenceEquals(_historyForm, form))
                {
                    _historyForm = null;
                }
            };
            _historyForm = form;
        }

        _historyForm.ApplyTheme(_settings.Theme);
        _historyForm.UpdateHistory(_historyStore.Load());
        if (_historyForm.WindowState == FormWindowState.Minimized)
        {
            _historyForm.WindowState = FormWindowState.Normal;
        }

        _historyForm.Show();
        _historyForm.Activate();
        _historyForm.BringToFront();
    }

    private void ToggleStartupFromMenu()
    {
        try
        {
            StartupManager.SetEnabled(_startupMenuItem.Checked);
        }
        catch (Exception exception)
        {
            _startupMenuItem.Checked = !_startupMenuItem.Checked;
            MessageBox.Show(
                $"Windows startup could not be updated: {exception.Message}",
                "Codex Usage",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private static bool SafeStartupEnabled()
    {
        try
        {
            return StartupManager.IsEnabled();
        }
        catch
        {
            return false;
        }
    }

    private void OpenCodexFolder()
    {
        var folder = Path.GetDirectoryName(_usageService.AuthFilePath);
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            MessageBox.Show(
                "The Codex folder does not exist yet. Sign in to Codex first.",
                "Codex Usage",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"")
        {
            UseShellExecute = true,
        });
    }

    private void ExitApplication()
    {
        _shutdown.Cancel();
        _popup.Hide();
        _trayIcon.Visible = false;
        ExitThread();
    }
}
