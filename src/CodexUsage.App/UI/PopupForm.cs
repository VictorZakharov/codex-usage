using System.Drawing.Drawing2D;
using CodexUsage.App.Settings;
using CodexUsage.Formatting;
using CodexUsage.Models;

namespace CodexUsage.App.UI;

public sealed class PopupForm : Form
{
    private const int LogicalDpi = 96;
    private const int LogicalWidth = 382;

    private readonly Label _titleLabel = new();
    private readonly Label _planLabel = new();
    private readonly Label _accountLabel = new();
    private readonly UsageMeterControl _primaryMeter = new();
    private readonly UsageMeterControl _secondaryMeter = new();
    private readonly UsageMeterControl _additionalMeter = new();
    private readonly Label _creditsLabel = new();
    private readonly Label _messageLabel = new();
    private readonly Label _updatedLabel = new();
    private readonly Button _refreshButton = new();
    private readonly Button _settingsButton = new();
    private readonly System.Windows.Forms.Timer _countdownTimer = new() { Interval = 30_000 };

    private ThemePalette _palette;
    private UsageSnapshot? _snapshot;
    private bool _refreshing;
    private bool _showAdditional;
    private bool _showCredits;
    private bool _showMessage;
    private string? _error;

    public PopupForm(AppSettings settings)
    {
        _palette = ThemePalette.Resolve(settings.Theme);
        AutoScaleDimensions = new SizeF(LogicalDpi, LogicalDpi);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(LogicalWidth, 364);
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        KeyPreview = true;
        Padding = new Padding(1);
        TopMost = true;

        ConfigureLabel(_titleLabel, 15f, FontStyle.Bold);
        _titleLabel.Text = "Codex usage";
        ConfigureLabel(_planLabel, 8.5f, FontStyle.Bold);
        _planLabel.TextAlign = ContentAlignment.MiddleCenter;
        ConfigureLabel(_accountLabel, 8.5f, FontStyle.Regular);
        ConfigureLabel(_creditsLabel, 9f, FontStyle.Regular);
        ConfigureLabel(_messageLabel, 8.25f, FontStyle.Regular);
        ConfigureLabel(_updatedLabel, 8f, FontStyle.Regular);

        ConfigureButton(_refreshButton, "Refresh");
        ConfigureButton(_settingsButton, "Settings");
        _refreshButton.Click += (_, _) => RefreshRequested?.Invoke(this, EventArgs.Empty);
        _settingsButton.Click += (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty);

        Controls.AddRange(
        [
            _titleLabel,
            _planLabel,
            _accountLabel,
            _primaryMeter,
            _secondaryMeter,
            _additionalMeter,
            _creditsLabel,
            _messageLabel,
            _updatedLabel,
            _refreshButton,
            _settingsButton,
        ]);

        _countdownTimer.Tick += (_, _) => RefreshDisplayText();
        VisibleChanged += (_, _) => _countdownTimer.Enabled = Visible;
        Deactivate += (_, _) => Hide();
        KeyDown += (_, eventArgs) =>
        {
            if (eventArgs.KeyCode == Keys.Escape)
            {
                Hide();
            }
        };

        ApplyTheme(settings.Theme);
        UpdateState(null, refreshing: true, error: null);
    }

    public event EventHandler? RefreshRequested;

    public event EventHandler? SettingsRequested;

    protected override bool ShowWithoutActivation => false;

    protected override CreateParams CreateParams
    {
        get
        {
            const int toolWindow = 0x00000080;
            var parameters = base.CreateParams;
            parameters.ExStyle |= toolWindow;
            return parameters;
        }
    }

    public void ApplyTheme(ThemeMode mode)
    {
        _palette = ThemePalette.Resolve(mode);
        BackColor = _palette.Background;
        ForeColor = _palette.Text;

        foreach (var label in new[]
                 {
                     _titleLabel, _planLabel, _accountLabel, _creditsLabel, _messageLabel, _updatedLabel,
                 })
        {
            label.BackColor = Color.Transparent;
            label.ForeColor = _palette.Text;
        }

        _accountLabel.ForeColor = _palette.SecondaryText;
        _messageLabel.ForeColor = _error is null ? _palette.SecondaryText : _palette.Danger;
        _updatedLabel.ForeColor = _palette.MutedText;
        _planLabel.BackColor = _palette.Card;
        StyleButton(_refreshButton);
        StyleButton(_settingsButton);
        RefreshDisplayText();
        Invalidate();
    }

    public void UpdateState(UsageSnapshot? snapshot, bool refreshing, string? error)
    {
        _snapshot = snapshot;
        _refreshing = refreshing;
        _error = error;

        _refreshButton.Enabled = !refreshing;
        _refreshButton.Text = refreshing ? "Refreshing…" : "Refresh";
        _messageLabel.ForeColor = error is null ? _palette.SecondaryText : _palette.Danger;
        RefreshDisplayText();
        LayoutContent();
    }

    public void ShowNearTray()
    {
        var cursor = Cursor.Position;
        var screen = Screen.FromPoint(cursor);
        PositionNearTray(cursor, screen);
        Show();

        // Creating or moving the window can switch it to the tray monitor's DPI.
        // Anchor it again after WinForms has applied that transition and resized it.
        PositionNearTray(cursor, screen);
        Activate();
        BringToFront();
    }

    protected override void OnShown(EventArgs eventArgs)
    {
        base.OnShown(eventArgs);
        LayoutContent();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs eventArgs)
    {
        base.OnDpiChanged(eventArgs);
        LayoutContent();
        Invalidate(invalidateChildren: true);
    }

    protected override void OnResize(EventArgs eventArgs)
    {
        base.OnResize(eventArgs);
        using var path = RoundedRectangle(
            new Rectangle(0, 0, Width, Height),
            LogicalToDeviceUnits(14));
        var region = new Region(path);
        var previous = Region;
        Region = region;
        previous?.Dispose();
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        base.OnPaint(eventArgs);
        eventArgs.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(_palette.Border);
        using var path = RoundedRectangle(
            new Rectangle(0, 0, Width - 1, Height - 1),
            LogicalToDeviceUnits(14));
        eventArgs.Graphics.DrawPath(pen, path);
    }

    private void PositionNearTray(Point cursor, Screen screen)
    {
        var work = screen.WorkingArea;
        var bounds = screen.Bounds;
        var gap = LogicalToDeviceUnits(8);
        var horizontalOffset = LogicalToDeviceUnits(24);
        var edgeThreshold = LogicalToDeviceUnits(64);
        var minimumX = work.Left + gap;
        var maximumX = Math.Max(minimumX, work.Right - Width - gap);
        var minimumY = work.Top + gap;
        var maximumY = Math.Max(minimumY, work.Bottom - Height - gap);

        var x = Math.Clamp(cursor.X - Width + horizontalOffset, minimumX, maximumX);
        int y;

        if (cursor.Y >= work.Bottom || Math.Abs(cursor.Y - bounds.Bottom) < edgeThreshold)
        {
            y = maximumY;
        }
        else if (cursor.Y <= work.Top || Math.Abs(cursor.Y - bounds.Top) < edgeThreshold)
        {
            y = minimumY;
        }
        else
        {
            y = Math.Clamp(cursor.Y - Height, minimumY, maximumY);
        }

        Location = new Point(x, y);
    }

    private void RefreshDisplayText()
    {
        if (_snapshot is null)
        {
            _planLabel.Text = "CODEX";
            _accountLabel.Text = "Uses your existing Codex sign-in";
            _primaryMeter.SetLoading("5-hour limit", _palette);
            _secondaryMeter.SetLoading("Weekly limit", _palette);
            _showAdditional = false;
            _additionalMeter.Visible = _showAdditional;
            _creditsLabel.Text = string.Empty;
            _showCredits = false;
            _creditsLabel.Visible = _showCredits;
            _messageLabel.Text = _error ?? (_refreshing ? "Contacting Codex…" : "Usage is not available yet.");
            _showMessage = true;
            _messageLabel.Visible = _showMessage;
            _updatedLabel.Text = string.Empty;
            return;
        }

        var now = DateTimeOffset.Now;
        _planLabel.Text = UsageText.PlanLabel(_snapshot.PlanType).ToUpperInvariant();
        _accountLabel.Text = string.IsNullOrWhiteSpace(_snapshot.AccountEmail)
            ? "OpenAI Codex"
            : _snapshot.AccountEmail;

        var primaryTitle = _snapshot.Primary is null
            ? "5-hour limit"
            : UsageText.WindowLabel(_snapshot.Primary, "Session limit");
        var secondaryTitle = _snapshot.Secondary is null
            ? "Weekly limit"
            : UsageText.WindowLabel(_snapshot.Secondary, "Weekly limit");
        _primaryMeter.SetData(
            primaryTitle,
            _snapshot.Primary,
            UsageText.ResetDescription(_snapshot.Primary?.ResetsAt, now),
            _palette);
        _secondaryMeter.SetData(
            secondaryTitle,
            _snapshot.Secondary,
            UsageText.ResetDescription(_snapshot.Secondary?.ResetsAt, now),
            _palette);

        var additional = _snapshot.AdditionalLimits.FirstOrDefault();
        _showAdditional = additional is not null;
        _additionalMeter.Visible = _showAdditional;
        if (additional is not null)
        {
            var window = additional.Primary ?? additional.Secondary;
            _additionalMeter.SetData(
                additional.Name,
                window,
                UsageText.ResetDescription(window?.ResetsAt, now),
                _palette);
        }

        _creditsLabel.Text = FormatCredits(_snapshot.Credits);
        _showCredits = !string.IsNullOrWhiteSpace(_creditsLabel.Text);
        _creditsLabel.Visible = _showCredits;
        _messageLabel.Text = _error ?? (_refreshing ? "Refreshing in the background…" : string.Empty);
        _showMessage = !string.IsNullOrWhiteSpace(_messageLabel.Text);
        _messageLabel.Visible = _showMessage;
        _updatedLabel.Text = $"Updated {_snapshot.FetchedAt.ToLocalTime():h:mm tt}";
    }

    private void LayoutContent()
    {
        const int left = 18;
        const int contentWidth = LogicalWidth - (left * 2);
        SetLogicalBounds(_titleLabel, left, 15, 220, 28);
        SetLogicalBounds(_planLabel, LogicalWidth - 112, 18, 92, 24);
        SetLogicalBounds(_accountLabel, left, 46, contentWidth, 21);

        var y = 77;
        SetLogicalBounds(_primaryMeter, left, y, contentWidth, 78);
        y += 86;
        SetLogicalBounds(_secondaryMeter, left, y, contentWidth, 78);
        y += 86;

        if (_showAdditional)
        {
            SetLogicalBounds(_additionalMeter, left, y, contentWidth, 78);
            y += 86;
        }

        if (_showCredits)
        {
            SetLogicalBounds(_creditsLabel, left, y, contentWidth, 24);
            y += 28;
        }

        if (_showMessage)
        {
            SetLogicalBounds(_messageLabel, left, y, contentWidth, 34);
            y += 38;
        }

        SetLogicalBounds(_updatedLabel, left, y + 4, 160, 25);
        SetLogicalBounds(_settingsButton, LogicalWidth - 180, y, 78, 30);
        SetLogicalBounds(_refreshButton, LogicalWidth - 96, y, 78, 30);
        ClientSize = new Size(
            LogicalToDeviceUnits(LogicalWidth),
            LogicalToDeviceUnits(y + 46));
    }

    private void SetLogicalBounds(Control control, int x, int y, int width, int height)
    {
        control.SetBounds(
            LogicalToDeviceUnits(x),
            LogicalToDeviceUnits(y),
            LogicalToDeviceUnits(width),
            LogicalToDeviceUnits(height));
    }

    private void ConfigureLabel(Label label, float size, FontStyle style)
    {
        label.AutoEllipsis = true;
        label.Font = new Font("Segoe UI", size, style);
        label.UseMnemonic = false;
    }

    private static void ConfigureButton(Button button, string text)
    {
        button.Text = text;
        button.Font = new Font("Segoe UI", 8.5f, FontStyle.Regular);
        button.FlatStyle = FlatStyle.Flat;
        button.Cursor = Cursors.Hand;
        button.TabStop = false;
    }

    private void StyleButton(Button button)
    {
        button.BackColor = _palette.Card;
        button.ForeColor = _palette.Text;
        button.FlatAppearance.BorderColor = _palette.Border;
        button.FlatAppearance.MouseOverBackColor = _palette.CardHover;
        button.FlatAppearance.MouseDownBackColor = _palette.Border;
    }

    private static string FormatCredits(CreditsInfo? credits)
    {
        if (credits is null || (!credits.HasCredits && credits.Balance is null && !credits.Unlimited))
        {
            return string.Empty;
        }

        if (credits.Unlimited)
        {
            return "Credits  ·  Unlimited";
        }

        return credits.Balance is null
            ? "Credits available"
            : $"Credits  ·  {credits.Balance:0.##}";
    }

    private static GraphicsPath RoundedRectangle(Rectangle rectangle, int radius)
    {
        var diameter = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(rectangle.Left, rectangle.Top, diameter, diameter, 180, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Top, diameter, diameter, 270, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rectangle.Left, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}
