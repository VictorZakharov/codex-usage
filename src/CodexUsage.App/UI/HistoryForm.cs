using CodexUsage.App.Settings;
using CodexUsage.Formatting;
using CodexUsage.History;
using CodexUsage.Tokens;

namespace CodexUsage.App.UI;

public sealed class HistoryForm : Form
{
    private readonly Label _titleLabel = new();
    private readonly Label _subtitleLabel = new();
    private readonly Label _rangeLabel = new();
    private readonly Button _rangeButton = new();
    private readonly CheckBox _offHoursCheckBox = new();
    private readonly ContextMenuStrip _rangeMenu = new();
    private readonly UsageHistoryChart _chart = new();
    private readonly Label _tokenSummaryLabel = new();
    private readonly ProgressBar _tokenProgress = new();
    private readonly Label _statusLabel = new();
    private readonly Func<
        DateTimeOffset,
        DateTimeOffset,
        DateTimeOffset?,
        CancellationToken,
        CodexTokenUsageSummary> _readTokenUsage;
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
    private CancellationTokenSource? _tokenUsageCancellation;
    private DateTimeOffset _lastTokenRefresh;
    private TimeSpan? _lastTokenRange;

    public HistoryForm(AppSettings settings)
        : this(settings, new CodexTokenUsageReader().Read)
    {
    }

    public HistoryForm(
        AppSettings settings,
        Func<
            DateTimeOffset,
            DateTimeOffset,
            DateTimeOffset?,
            CancellationToken,
            CodexTokenUsageSummary> readTokenUsage)
    {
        _readTokenUsage = readTokenUsage ?? throw new ArgumentNullException(nameof(readTokenUsage));
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

        _subtitleLabel.Text = "▲ marks a reset; dashed red projects zero at the average pace since reset.";
        _subtitleLabel.Font = new Font("Segoe UI", 9f, FontStyle.Regular);
        _subtitleLabel.AutoEllipsis = true;

        _offHoursCheckBox.Text = "Show off-hour flats";
        _offHoursCheckBox.Checked = true;
        _offHoursCheckBox.Enabled = false;
        _offHoursCheckBox.AutoSize = false;
        _offHoursCheckBox.TextAlign = ContentAlignment.MiddleLeft;
        _offHoursCheckBox.CheckedChanged += (_, _) =>
            _chart.ShowOffHourSegments = _offHoursCheckBox.Checked;

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

        _tokenSummaryLabel.Text = "Local tokens · calculating…";
        _tokenSummaryLabel.AutoEllipsis = true;
        _tokenSummaryLabel.TextAlign = ContentAlignment.MiddleLeft;

        _tokenProgress.Style = ProgressBarStyle.Marquee;
        _tokenProgress.MarqueeAnimationSpeed = 24;
        _tokenProgress.Visible = false;

        Controls.AddRange(
        [
            _titleLabel,
            _subtitleLabel,
            _rangeLabel,
            _rangeButton,
            _offHoursCheckBox,
            _chart,
            _tokenSummaryLabel,
            _tokenProgress,
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
        QueueTokenSummaryRefresh(force: false);

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
        _tokenSummaryLabel.ForeColor = _palette.SecondaryText;
        _offHoursCheckBox.BackColor = _palette.Background;
        _offHoursCheckBox.ForeColor = _palette.SecondaryText;
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
            _tokenUsageCancellation?.Cancel();
            _tokenUsageCancellation?.Dispose();
            _rangeMenu.Dispose();
        }

        base.Dispose(disposing);
    }

    private void UpdateChart()
    {
        _chart.ShowOffHourSegments = _offHoursCheckBox.Checked;
        _chart.SetData(_samples, _selectedRange.Duration, _palette);
        _offHoursCheckBox.Enabled = _chart.HasLearnedOffHours;
        _subtitleLabel.Text = _chart.HasLearnedOffHours
            ? "▲ marks a reset; dashed red uses the learned schedule and pauses during assumed off hours."
            : "▲ marks a reset; dashed red uses elapsed time until 24 hours of history can identify off hours.";
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
        QueueTokenSummaryRefresh(force: true);
    }

    private void LayoutContent()
    {
        const int margin = 24;
        _titleLabel.SetBounds(margin, 16, Math.Max(200, ClientSize.Width - 330), 34);
        _subtitleLabel.SetBounds(margin, 51, Math.Max(240, ClientSize.Width - 235), 24);
        _offHoursCheckBox.SetBounds(ClientSize.Width - 196, 50, 172, 26);
        _rangeLabel.SetBounds(ClientSize.Width - 230, 20, 54, 28);
        _rangeButton.SetBounds(ClientSize.Width - 168, 18, 144, 30);
        _chart.SetBounds(margin, 84, Math.Max(100, ClientSize.Width - (margin * 2)), Math.Max(180, ClientSize.Height - 158));
        _tokenSummaryLabel.SetBounds(margin, ClientSize.Height - 66, Math.Max(100, ClientSize.Width - 164), 24);
        _tokenProgress.SetBounds(ClientSize.Width - 116, ClientSize.Height - 61, 92, 12);
        _statusLabel.SetBounds(margin, ClientSize.Height - 38, Math.Max(100, ClientSize.Width - (margin * 2)), 26);
    }

    private void QueueTokenSummaryRefresh(bool force)
    {
        var now = DateTimeOffset.Now;
        if (!force
            && _lastTokenRange == _selectedRange.Duration
            && now - _lastTokenRefresh < TimeSpan.FromMinutes(2))
        {
            return;
        }

        _lastTokenRefresh = now;
        _lastTokenRange = _selectedRange.Duration;
        _tokenUsageCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _tokenUsageCancellation = cancellation;
        _tokenSummaryLabel.Text = "Local tokens · calculating…";
        _tokenProgress.Visible = true;
        _ = UpdateTokenSummaryAsync(now, cancellation);
    }

    private async Task UpdateTokenSummaryAsync(
        DateTimeOffset now,
        CancellationTokenSource cancellation)
    {
        var (resetStartedAt, resetLabel) = CurrentResetStart();
        try
        {
            var summary = await Task.Run(
                () => _readTokenUsage(
                    now,
                    now - _selectedRange.Duration,
                    resetStartedAt,
                    cancellation.Token),
                cancellation.Token);
            if (cancellation.IsCancellationRequested || IsDisposed)
            {
                return;
            }

            _tokenProgress.Visible = false;
            if (summary.SessionFiles == 0)
            {
                _tokenSummaryLabel.Text = "Local tokens unavailable · no Codex session counters found";
                return;
            }

            var values = new List<string>
            {
                $"{_selectedRange.Label} {FormatTokenCount(summary.SelectedPeriodTokens)}",
            };
            if (summary.SinceResetTokens is { } sinceReset)
            {
                values.Add($"Since {resetLabel} reset {FormatTokenCount(sinceReset)}");
            }

            values.Add($"Today {FormatTokenCount(summary.TodayTokens)}");
            _tokenSummaryLabel.Text = "Local tokens · " + string.Join("  ·  ", values);
        }
        catch (OperationCanceledException)
        {
            // A newer range or refresh superseded this calculation.
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            if (!cancellation.IsCancellationRequested && !IsDisposed)
            {
                _tokenProgress.Visible = false;
                _tokenSummaryLabel.Text = "Local tokens unavailable · session counters could not be read";
            }
        }
        finally
        {
            if (ReferenceEquals(_tokenUsageCancellation, cancellation))
            {
                _tokenUsageCancellation = null;
                if (!IsDisposed)
                {
                    _tokenProgress.Visible = false;
                }
            }

            cancellation.Dispose();
        }
    }

    private (DateTimeOffset? StartedAt, string Label) CurrentResetStart()
    {
        var latest = _samples.LastOrDefault();
        if (latest?.PrimaryResetsAt is { } primaryReset
            && latest.PrimaryDuration is { } primaryDuration)
        {
            return (primaryReset - primaryDuration, FormatWindowLabel(primaryDuration, "Primary"));
        }

        if (latest?.SecondaryResetsAt is { } secondaryReset
            && latest.SecondaryDuration is { } secondaryDuration)
        {
            return (secondaryReset - secondaryDuration, FormatWindowLabel(secondaryDuration, "Secondary"));
        }

        return (null, "quota");
    }

    private static string FormatTokenCount(long tokens)
    {
        if (tokens >= 1_000_000_000)
        {
            return $"{tokens / 1_000_000_000d:0.##}B";
        }

        if (tokens >= 1_000_000)
        {
            return $"{tokens / 1_000_000d:0.##}M";
        }

        return tokens >= 1_000
            ? $"{tokens / 1_000d:0.##}K"
            : $"{tokens:N0}";
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
