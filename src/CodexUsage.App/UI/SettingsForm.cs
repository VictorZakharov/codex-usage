using CodexUsage.App.Settings;

namespace CodexUsage.App.UI;

public sealed class SettingsForm : Form
{
    private readonly ComboBox _refreshInterval = new();
    private readonly ComboBox _notificationThreshold = new();
    private readonly ComboBox _theme = new();
    private readonly CheckBox _notifications = new();
    private readonly CheckBox _launchAtStartup = new();
    private readonly Button _saveButton = new();
    private readonly Button _cancelButton = new();
    private readonly ThemePalette _palette;

    public SettingsForm(AppSettings settings, bool launchAtStartup)
    {
        _palette = ThemePalette.Resolve(settings.Theme);
        Text = "Codex Usage settings";
        ClientSize = new Size(430, 322);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 9f);
        BackColor = _palette.Background;
        ForeColor = _palette.Text;

        var heading = CreateLabel("Codex Usage", 18, 18, 250, 30, bold: true, size: 14f);
        var caption = CreateLabel(
            "A focused Windows tray meter for your Codex plan.",
            18,
            48,
            390,
            24,
            bold: false,
            size: 8.75f);
        caption.ForeColor = _palette.SecondaryText;

        Controls.Add(heading);
        Controls.Add(caption);
        AddRow("Refresh every", _refreshInterval, 88);
        _refreshInterval.DropDownStyle = ComboBoxStyle.DropDownList;
        _refreshInterval.Items.AddRange(["1 minute", "2 minutes", "5 minutes", "15 minutes", "30 minutes"]);
        _refreshInterval.SelectedIndex = Array.IndexOf(new[] { 1, 2, 5, 15, 30 }, settings.RefreshIntervalMinutes);

        AddRow("Theme", _theme, 130);
        _theme.DropDownStyle = ComboBoxStyle.DropDownList;
        _theme.Items.AddRange(["System", "Dark", "Light"]);
        _theme.SelectedIndex = (int)settings.Theme;

        _notifications.Text = "Notify me when available quota drops to";
        _notifications.Checked = settings.NotificationsEnabled;
        _notifications.SetBounds(18, 178, 282, 26);
        StyleCheckBox(_notifications);
        Controls.Add(_notifications);

        _notificationThreshold.DropDownStyle = ComboBoxStyle.DropDownList;
        _notificationThreshold.Items.AddRange(["30%", "20%", "10%", "0%"]);
        _notificationThreshold.SetBounds(315, 176, 92, 28);
        _notificationThreshold.SelectedIndex = Array.IndexOf(
            new[] { 30, 20, 10, 0 },
            settings.NotificationThresholdPercent);
        if (_notificationThreshold.SelectedIndex < 0)
        {
            _notificationThreshold.SelectedIndex = 1;
        }
        StyleComboBox(_notificationThreshold);
        Controls.Add(_notificationThreshold);

        _launchAtStartup.Text = "Start Codex Usage when I sign in to Windows";
        _launchAtStartup.Checked = launchAtStartup;
        _launchAtStartup.SetBounds(18, 216, 365, 26);
        StyleCheckBox(_launchAtStartup);
        Controls.Add(_launchAtStartup);

        _saveButton.Text = "Save";
        _saveButton.DialogResult = DialogResult.OK;
        _saveButton.SetBounds(320, 272, 88, 32);
        StyleButton(_saveButton, primary: true);
        Controls.Add(_saveButton);

        _cancelButton.Text = "Cancel";
        _cancelButton.DialogResult = DialogResult.Cancel;
        _cancelButton.SetBounds(224, 272, 88, 32);
        StyleButton(_cancelButton, primary: false);
        Controls.Add(_cancelButton);

        AcceptButton = _saveButton;
        CancelButton = _cancelButton;
    }

    public AppSettings SelectedSettings
    {
        get
        {
            var refreshValues = new[] { 1, 2, 5, 15, 30 };
            var thresholdValues = new[] { 30, 20, 10, 0 };
            return new AppSettings
            {
                RefreshIntervalMinutes = refreshValues[Math.Max(0, _refreshInterval.SelectedIndex)],
                NotificationsEnabled = _notifications.Checked,
                NotificationThresholdPercent = thresholdValues[Math.Max(0, _notificationThreshold.SelectedIndex)],
                Theme = (ThemeMode)Math.Max(0, _theme.SelectedIndex),
            };
        }
    }

    public bool LaunchAtStartup => _launchAtStartup.Checked;

    private void AddRow(string labelText, ComboBox comboBox, int y)
    {
        var label = CreateLabel(labelText, 18, y + 3, 160, 24, bold: false, size: 9f);
        Controls.Add(label);
        comboBox.SetBounds(204, y, 204, 28);
        StyleComboBox(comboBox);
        Controls.Add(comboBox);
    }

    private Label CreateLabel(string text, int x, int y, int width, int height, bool bold, float size)
    {
        return new Label
        {
            Text = text,
            Bounds = new Rectangle(x, y, width, height),
            Font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular),
            ForeColor = _palette.Text,
            BackColor = Color.Transparent,
        };
    }

    private void StyleComboBox(ComboBox comboBox)
    {
        comboBox.BackColor = _palette.Card;
        comboBox.ForeColor = _palette.Text;
        comboBox.FlatStyle = FlatStyle.Flat;
    }

    private void StyleCheckBox(CheckBox checkBox)
    {
        checkBox.ForeColor = _palette.Text;
        checkBox.BackColor = Color.Transparent;
    }

    private void StyleButton(Button button, bool primary)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.Cursor = Cursors.Hand;
        button.BackColor = primary ? _palette.Accent : _palette.Card;
        button.ForeColor = primary ? Color.White : _palette.Text;
        button.FlatAppearance.BorderColor = primary ? _palette.Accent : _palette.Border;
    }
}
