using System.Globalization;
using CinemaWatch.Server.Data;
using CinemaWatch.Server.Profiles;

namespace CinemaWatch.Server.Polling;

/// <summary>The numbers the operator's panel can change (docs/SAAS.md §4.3).</summary>
public enum TuningKey
{
    MinIntervalMinutes,
    RequestsPerHour,
    RequestGapMinSeconds,
    RequestGapMaxSeconds,
    ManualGapMinSeconds,
    ManualGapMaxSeconds,
    ManualFreshMinutes,
    JitterPercent,
    FirstBlockPauseMinutes,
    MaxBlockPauseMinutes,
    WizardRequestsPerHour,
    WizardBurst,
    WizardAccountRequestsPerHour,
    WizardGapSeconds,
}

/// <summary>
/// One number the operator's panel can change: its fixed range, where the override is stored and which setting it
/// changes. The range is a code decision, not a setting: widening it means a code change and a review.
/// </summary>
public sealed class NumberSetting
{
    private readonly Func<PollerSettingsRow, double?> _read;
    private readonly Action<PollerSettingsRow, double?> _write;
    private readonly Func<PollerOptions, LimitOptions, double> _value;
    private readonly Action<PollerOptions, LimitOptions, double> _apply;

    internal NumberSetting(
        TuningKey key, double min, double max, bool wholeNumber,
        Func<PollerSettingsRow, double?> read, Action<PollerSettingsRow, double?> write,
        Func<PollerOptions, LimitOptions, double> value, Action<PollerOptions, LimitOptions, double> apply)
    {
        Key = key;
        Min = min;
        Max = max;
        WholeNumber = wholeNumber;
        _read = read;
        _write = write;
        _value = value;
        _apply = apply;
    }

    public TuningKey Key { get; }

    public double Min { get; }

    public double Max { get; }

    /// <summary>Minutes, counts and percentages are whole numbers; the pauses between requests take decimals.</summary>
    public bool WholeNumber { get; }

    /// <summary>The override stored in the database, or null (the configuration's value applies).</summary>
    public double? Read(PollerSettingsRow row) => _read(row);

    public void Write(PollerSettingsRow row, double? value) => _write(row, value);

    /// <summary>The value in these settings.</summary>
    public double ValueIn(PollerOptions poller, LimitOptions limits) => _value(poller, limits);

    internal void Apply(PollerOptions poller, LimitOptions limits, double value) => _apply(poller, limits, value);

    public bool InRange(double value) => value >= Min - Epsilon && value <= Max + Epsilon;

    /// <summary>"10", "2.5" - invariant, as the form sends it back.</summary>
    public string Format(double value) => value.ToString(WholeNumber ? "0" : "0.##", CultureInfo.InvariantCulture);

    internal const double Epsilon = 1e-9;
}

public enum SettingProblemKind
{
    /// <summary>Not a number (or not a whole number where one is needed).</summary>
    NotANumber,

    /// <summary>Outside the fixed range of <see cref="NumberSetting"/>.</summary>
    OutOfRange,

    /// <summary>The upper value of a pair is below the lower one (pauses between requests, pauses after a block).</summary>
    BelowMinimum,
}

public sealed record SettingProblem(TuningKey Key, SettingProblemKind Kind);

/// <summary>One effective value that changes: "RequestsPerHour", "120", "90".</summary>
public sealed record SettingChange(string Name, string From, string To);

/// <summary>The overrides from the panel's form, or what is wrong with it.</summary>
public sealed record TuningParse(PollerSettingsRow Row, IReadOnlyList<SettingProblem> Problems)
{
    public bool IsValid => Problems.Count == 0;
}

/// <summary>
/// The settings the operator's panel can change and their fixed ranges (docs/SAAS.md §4.3, §9). The configuration
/// (appsettings.json, environment variables) gives the defaults; the panel stores overrides in poller_settings; a
/// value equal to the default is not stored, so a later change of the configuration applies again. The configuration
/// itself may be outside these ranges (Development pauses only 2 minutes after a block): the panel shows such a value
/// but does not let anyone set one.
/// </summary>
public static class PollerTuning
{
    public static readonly IReadOnlyList<NumberSetting> Numbers =
    [
        new(TuningKey.MinIntervalMinutes, LimitOptions.MinIntervalFloor, 60, true,
            r => r.MinIntervalMinutes, (r, v) => r.MinIntervalMinutes = Whole(v),
            (_, l) => l.MinIntervalMinutes, (_, l, v) => l.MinIntervalMinutes = (int)Math.Round(v)),
        new(TuningKey.RequestsPerHour, 10, 300, true,
            r => r.RequestsPerHour, (r, v) => r.RequestsPerHour = Whole(v),
            (p, _) => p.RequestsPerHour, (p, _, v) => p.RequestsPerHour = (int)Math.Round(v)),
        new(TuningKey.RequestGapMinSeconds, 2, 60, false,
            r => r.RequestGapMinSeconds, (r, v) => r.RequestGapMinSeconds = v,
            (p, _) => p.RequestGapMinSeconds, (p, _, v) => p.RequestGapMinSeconds = v),
        new(TuningKey.RequestGapMaxSeconds, 2, 60, false,
            r => r.RequestGapMaxSeconds, (r, v) => r.RequestGapMaxSeconds = v,
            (p, _) => p.RequestGapMaxSeconds, (p, _, v) => p.RequestGapMaxSeconds = v),
        new(TuningKey.ManualGapMinSeconds, 1, 10, false,
            r => r.ManualGapMinSeconds, (r, v) => r.ManualGapMinSeconds = v,
            (p, _) => p.ManualGapMinSeconds, (p, _, v) => p.ManualGapMinSeconds = v),
        new(TuningKey.ManualGapMaxSeconds, 1, 10, false,
            r => r.ManualGapMaxSeconds, (r, v) => r.ManualGapMaxSeconds = v,
            (p, _) => p.ManualGapMaxSeconds, (p, _, v) => p.ManualGapMaxSeconds = v),
        new(TuningKey.ManualFreshMinutes, 0, 10, true,
            r => r.ManualFreshMinutes, (r, v) => r.ManualFreshMinutes = Whole(v),
            (p, _) => p.ManualFreshMinutes, (p, _, v) => p.ManualFreshMinutes = (int)Math.Round(v)),
        new(TuningKey.JitterPercent, 0, 50, true,
            r => r.JitterPercent, (r, v) => r.JitterPercent = Whole(v),
            (p, _) => p.JitterPercent, (p, _, v) => p.JitterPercent = (int)Math.Round(v)),
        new(TuningKey.FirstBlockPauseMinutes, 15, 12 * 60, true,
            r => r.FirstBlockPauseMinutes, (r, v) => r.FirstBlockPauseMinutes = Whole(v),
            (p, _) => p.FirstBlockPauseMinutes, (p, _, v) => p.FirstBlockPauseMinutes = (int)Math.Round(v)),
        new(TuningKey.MaxBlockPauseMinutes, 15, 7 * 24 * 60, true,
            r => r.MaxBlockPauseMinutes, (r, v) => r.MaxBlockPauseMinutes = Whole(v),
            (p, _) => p.MaxBlockPauseMinutes, (p, _, v) => p.MaxBlockPauseMinutes = (int)Math.Round(v)),
        new(TuningKey.WizardRequestsPerHour, 10, 300, true,
            r => r.WizardRequestsPerHour, (r, v) => r.WizardRequestsPerHour = Whole(v),
            (p, _) => p.Wizard.RequestsPerHour, (p, _, v) => p.Wizard.RequestsPerHour = (int)Math.Round(v)),
        new(TuningKey.WizardBurst, 10, 300, true,
            r => r.WizardBurst, (r, v) => r.WizardBurst = Whole(v),
            (p, _) => p.Wizard.Burst, (p, _, v) => p.Wizard.Burst = (int)Math.Round(v)),
        new(TuningKey.WizardAccountRequestsPerHour, 10, 300, true,
            r => r.WizardAccountRequestsPerHour, (r, v) => r.WizardAccountRequestsPerHour = Whole(v),
            (p, _) => p.Wizard.AccountRequestsPerHour, (p, _, v) => p.Wizard.AccountRequestsPerHour = (int)Math.Round(v)),
        new(TuningKey.WizardGapSeconds, 1, 10, false,
            r => r.WizardGapSeconds, (r, v) => r.WizardGapSeconds = v,
            (p, _) => p.Wizard.GapSeconds, (p, _, v) => p.Wizard.GapSeconds = v),
    ];

    /// <summary>Pairs whose upper value must not be below the lower one: (lower, upper).</summary>
    private static readonly (TuningKey Lower, TuningKey Upper)[] Pairs =
    [
        (TuningKey.RequestGapMinSeconds, TuningKey.RequestGapMaxSeconds),
        (TuningKey.ManualGapMinSeconds, TuningKey.ManualGapMaxSeconds),
        (TuningKey.FirstBlockPauseMinutes, TuningKey.MaxBlockPauseMinutes),
    ];

    public static NumberSetting Get(TuningKey key) => Numbers.Single(n => n.Key == key);

    /// <summary>
    /// The effective settings: copies of the configuration with the stored overrides applied. An override outside
    /// today's range (a manual change in the database, a range narrowed by a later version) is ignored; if the result
    /// is still not valid (e.g. a pair the wrong way round), the configuration applies unchanged.
    /// </summary>
    public static (PollerOptions Poller, LimitOptions Limits) Apply(PollerOptions config, LimitOptions configLimits, PollerSettingsRow? row)
    {
        var poller = config.Copy();
        var limits = configLimits.Copy();
        if (row is null)
            return (poller, limits);
        foreach (var setting in Numbers)
        {
            if (setting.Read(row) is { } value && setting.InRange(value) && (!setting.WholeNumber || IsWhole(value)))
                setting.Apply(poller, limits, value);
        }
        if (row.ReuseScreeningDetails is { } reuse)
            poller.ReuseScreeningDetails = reuse;
        return poller.IsValid() && limits.IsValid() && PairsInOrder(poller, limits) ? (poller, limits) : (config.Copy(), configLimits.Copy());
    }

    /// <summary>
    /// The panel's form as overrides: an empty field or the default's value means "no override"; anything else must be
    /// a number in its range, and each pair must stay in order. All problems are reported together.
    /// </summary>
    public static TuningParse Parse(PollerOptions config, LimitOptions configLimits, IReadOnlyDictionary<TuningKey, string?> values, bool reuse)
    {
        var row = new PollerSettingsRow();
        var problems = new List<SettingProblem>();
        foreach (var setting in Numbers)
        {
            var text = values.TryGetValue(setting.Key, out var raw) ? raw?.Trim().Replace(',', '.') : null;
            if (string.IsNullOrEmpty(text))
                continue;
            if (!double.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)
                || double.IsNaN(value) || double.IsInfinity(value) || (setting.WholeNumber && !IsWhole(value)))
            {
                problems.Add(new SettingProblem(setting.Key, SettingProblemKind.NotANumber));
                continue;
            }
            if (Math.Abs(value - setting.ValueIn(config, configLimits)) < NumberSetting.Epsilon)
                continue;
            if (!setting.InRange(value))
            {
                problems.Add(new SettingProblem(setting.Key, SettingProblemKind.OutOfRange));
                continue;
            }
            setting.Write(row, value);
        }
        if (reuse != config.ReuseScreeningDetails)
            row.ReuseScreeningDetails = reuse;

        if (problems.Count == 0)
        {
            // The pairs are compared as they would apply: an empty field takes the configuration's value.
            var poller = config.Copy();
            var limits = configLimits.Copy();
            foreach (var setting in Numbers)
            {
                if (setting.Read(row) is { } value)
                    setting.Apply(poller, limits, value);
            }
            foreach (var (lower, upper) in Pairs)
            {
                if (Get(upper).ValueIn(poller, limits) < Get(lower).ValueIn(poller, limits) - NumberSetting.Epsilon)
                    problems.Add(new SettingProblem(upper, SettingProblemKind.BelowMinimum));
            }
        }
        return new TuningParse(row, problems);
    }

    /// <summary>The effective values that differ between two sets of settings (for the event log and the message).</summary>
    public static IReadOnlyList<SettingChange> Changes(
        (PollerOptions Poller, LimitOptions Limits) before, (PollerOptions Poller, LimitOptions Limits) after)
    {
        var changes = new List<SettingChange>();
        foreach (var setting in Numbers)
        {
            var from = setting.ValueIn(before.Poller, before.Limits);
            var to = setting.ValueIn(after.Poller, after.Limits);
            if (Math.Abs(from - to) >= NumberSetting.Epsilon)
                changes.Add(new SettingChange(setting.Key.ToString(), setting.Format(from), setting.Format(to)));
        }
        if (before.Poller.ReuseScreeningDetails != after.Poller.ReuseScreeningDetails)
            changes.Add(new SettingChange(nameof(PollerOptions.ReuseScreeningDetails),
                before.Poller.ReuseScreeningDetails ? "true" : "false", after.Poller.ReuseScreeningDetails ? "true" : "false"));
        return changes;
    }

    /// <summary>"RequestsPerHour 120 → 90; MinIntervalMinutes 10 → 15"</summary>
    public static string Describe(IReadOnlyList<SettingChange> changes) =>
        string.Join("; ", changes.Select(c => $"{c.Name} {c.From} → {c.To}"));

    private static bool PairsInOrder(PollerOptions poller, LimitOptions limits) =>
        Pairs.All(pair => Get(pair.Upper).ValueIn(poller, limits) >= Get(pair.Lower).ValueIn(poller, limits) - NumberSetting.Epsilon);

    private static bool IsWhole(double value) => Math.Abs(value - Math.Round(value)) < NumberSetting.Epsilon;

    private static int? Whole(double? value) => value is { } v ? (int)Math.Round(v) : null;
}
