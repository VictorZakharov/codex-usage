using CodexUsage.App.Settings;
using CodexUsage.Formatting;
using CodexUsage.History;

namespace CodexUsage.App.UI;

public sealed class HistoryForm : Form
{
    private readonly Label _titleLabel = new();
    private readonly Label _subtitleLabel = new();
    private readonly Label _rangeLabel = new();
    private readonly Button _rangeButton = new();
    private readonly ContextMenuStrip _rangeMenu = new();
    private readonly UsageHistoryChart _chart = new();
    private readonly Label _statusLabel = new();
    private readonly IReadOnlyList<RangeOption> _rangeOptions =
    [
        new RangeOption("24 hours", TimeSpan.FromDays(1)),
        new RangeOption("7 days", TimeSpan.FromDays(7)),
        new RangeOption("30 days", TimeSpan.FromDays(30)),
        new RangeOption("90 days", UsageHistoryStore.Retention),
    ];
    private IReadOnlyList<UsageHistorySample> _samples = [];
    private ThemePalette _palette;
    private RangeOption _selectedRange;

    public HistoryForm(AppSettings settings)
    {
        _palette = ThemePalette.Resolve(settings.Theme);
        _selectedRange = _rangeOptions[1];
        Text = "Codex usage history";
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(860, 540);
        MinimumSize = new Size(640, 400);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9f, FontStyle.Regular);

        _titleLabel.Text = "Usage history";
        _titleLabel.Font = new Font("Segoe UI", 17f, FontStyle.Bold);
        _titleLabel.AutoSize = false;

        _subtitleLabel.Text = "▲ marks a reset; dashed red projects zero when the average pace would exhaust quota before reset.";
        _subtitleLabel.Font = new Font("Segoe UI", 9f, FontStyle.Regular);
        _subtitleLabel.AutoEllipsis = true;

        _rangeLabel.Text = "Range";
        _rangeLabel.TextAlign = ContentAlignment.MiddleRight;

        _rangeButton.Text = $"{_selectedRange.Label}  ▾";
        _rangeButton.Font = new Font("Segoe UI", 9f, FontStyle.Regular);
        _rangeButton.FlatStyle = FlatStyle.Flat;
        _rangeButton.Cursor = Cursors.Hand;
        _rangeButton.Click += (_, _) => _rangeMenu.Show(
            _rangeButton,
            new Point(0, _rangeButton.Height));
        foreach (var option in _rangeOptions)
        {
            var item = new ToolStripMenuItem(option.Label)
            {
                Checked = option == _selectedRange,
            };
            item.Click += (_, _) => SelectRange(option);
            _rangeMenu.Items.Add(item);
        }

        _statusLabel.AutoEllipsis = true;
        _statusLabel.TextAlign = ContentAlignment.MiddleLeft;

        Controls.AddRange(
        [
            _titleLabel,
            _subtitleLabel,
            _rangeLabel,
            _rangeButton,
            _chart,
            _statusLabel,
        ]);

        Resize += (_, _) => LayoutContent();
        ApplyTheme(settings.Theme);
        LayoutContent();
    }

    public void UpdateHistory(IReadOnlyList<UsageHistorySample> samples)
    {
        _samples = samples.OrderBy(sample => sample.RecordedAt).ToArray();
        UpdateChart();

        if (_samples.Count == 0)
        {
            _statusLabel.Text = "No samples yet. Keep Codex Usage running and history will appear automatically.";
            return;
        }

        var latest = _samples[^1];
        var primaryLabel = FormatWindowLabel(latest.PrimaryDuration, "Primary");
        var secondaryLabel = FormatWindowLabel(latest.SecondaryDuration, "Secondary");
        var latestValues = new List<string>();
        if (latest.PrimaryAvailablePercent is not null)
        {
            latestValues.Add($"{primaryLabel} {FormatAvailability(latest.PrimaryAvailablePercent)}");
        }

        if (latest.SecondaryAvailablePercent is not null)
        {
            latestValues.Add($"{secondaryLabel} {FormatAvailability(latest.SecondaryAvailablePercent)}");
        }

        _statusLabel.Text = $"{_samples.Count:N0} samples since {FormatStart(_samples[0].RecordedAt)}  ·  "
            + $"Latest: {(latestValues.Count == 0 ? "unavailable" : string.Join("  ·  ", latestValues))}";
    }

    public void ApplyTheme(ThemeMode mode)
    {
        _palette = ThemePalette.Resolve(mode);
        BackColor = _palette.Background;
        ForeColor = _palette.Text;
        _titleLabel.ForeColor = _palette.Text;
        _subtitleLabel.ForeColor = _palette.SecondaryText;
        _rangeLabel.ForeColor = _palette.SecondaryText;
        _statusLabel.ForeColor = _palette.MutedText;
        _rangeButton.BackColor = _palette.Card;
        _rangeButton.ForeColor = _palette.Text;
        _rangeButton.FlatAppearance.BorderColor = _palette.Border;
        _rangeButton.FlatAppearance.MouseOverBackColor = _palette.CardHover;
        _rangeButton.FlatAppearance.MouseDownBackColor = _palette.Border;
        UpdateChart();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _rangeMenu.Dispose();
        }

        base.Dispose(disposing);
    }

    private void UpdateChart()
    {
        _chart.SetData(_samples, _selectedRange.Duration, _palette);
    }

    private void SelectRange(RangeOption option)
    {
        _selectedRange = option;
        _rangeButton.Text = $"{option.Label}  ▾";
        foreach (ToolStripMenuItem item in _rangeMenu.Items)
        {
            item.Checked = item.Text == option.Label;
        }

        UpdateChart();
    }

    private void LayoutContent()
    {
        const int margin = 24;
        _titleLabel.SetBounds(margin, 16, Math.Max(200, ClientSize.Width - 330), 34);
        _subtitleLabel.SetBounds(margin, 51, Math.Max(260, ClientSize.Width - (margin * 2)), 24);
        _rangeLabel.SetBounds(ClientSize.Width - 230, 20, 54, 28);
        _rangeButton.SetBounds(ClientSize.Width - 168, 18, 144, 30);
        _chart.SetBounds(margin, 84, Math.Max(100, ClientSize.Width - (margin * 2)), Math.Max(180, ClientSize.Height - 130));
        _statusLabel.SetBounds(margin, ClientSize.Height - 38, Math.Max(100, ClientSize.Width - (margin * 2)), 26);
    }

    private static string FormatAvailability(double? value)
        => value is null ? "—" : $"{value:0.#}%";

    private static string FormatWindowLabel(TimeSpan? duration, string fallback)
    {
        const string suffix = " limit";
        var label = UsageText.WindowLabel(duration, fallback + suffix);
        return label.EndsWith(suffix, StringComparison.Ordinal)
            ? label[..^suffix.Length]
            : label;
    }

    private static string FormatStart(DateTimeOffset timestamp)
        => timestamp.ToLocalTime().ToString("MMM d, yyyy");

    private sealed record RangeOption(string Label, TimeSpan Duration)
    {
        public override string ToString() => Label;
    }
}
