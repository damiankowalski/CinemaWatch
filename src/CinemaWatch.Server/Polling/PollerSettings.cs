using CinemaWatch.Server.Data;
using CinemaWatch.Server.Profiles;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CinemaWatch.Server.Polling;

/// <summary>
/// The settings the poller works with: the configuration (<see cref="PollerOptions"/>, <see cref="LimitOptions"/>) with
/// the overrides the owner stored in the operator's panel (table poller_settings, <see cref="PollerTuning"/>). Read again
/// at the start of every poller cycle and right after the panel saves, so a change applies from the next cycle - also
/// in a separate worker process, which shares only the database. Everything that shapes the requests reads from here,
/// never from <see cref="IOptions{TOptions}"/> directly.
/// </summary>
public sealed class PollerSettings
{
    private readonly PollerOptions _config;
    private readonly LimitOptions _configLimits;
    private Snapshot _current;

    public PollerSettings(IOptions<PollerOptions> poller, IOptions<LimitOptions> limits)
    {
        _config = poller.Value.Copy();
        _configLimits = limits.Value.Copy();
        _current = Build(null);
    }

    private sealed record Snapshot(PollerOptions Poller, LimitOptions Limits, PollerSettingsRow Overrides);

    /// <summary>Effective poller settings (a copy that never changes; a change replaces it).</summary>
    public PollerOptions Poller => Volatile.Read(ref _current).Poller;

    /// <summary>Effective limits: the minimum interval may come from the panel, the profile limits from the configuration.</summary>
    public LimitOptions Limits => Volatile.Read(ref _current).Limits;

    /// <summary>The stored overrides (a copy).</summary>
    public PollerSettingsRow Overrides => Volatile.Read(ref _current).Overrides.Copy();

    /// <summary>The configuration's value: what "restore defaults" goes back to.</summary>
    public double DefaultOf(NumberSetting setting) => setting.ValueIn(_config, _configLimits);

    public double ValueOf(NumberSetting setting)
    {
        var current = Volatile.Read(ref _current);
        return setting.ValueIn(current.Poller, current.Limits);
    }

    /// <summary>Whether this value comes from the panel rather than from the configuration.</summary>
    public bool IsOverridden(NumberSetting setting)
    {
        var current = Volatile.Read(ref _current);
        return setting.Read(current.Overrides) is not null
               && Math.Abs(setting.ValueIn(current.Poller, current.Limits) - DefaultOf(setting)) >= NumberSetting.Epsilon;
    }

    public bool DefaultReuseScreeningDetails => _config.ReuseScreeningDetails;

    /// <summary>Uses these overrides from now on (null = the configuration only).</summary>
    public void Apply(PollerSettingsRow? row) => Volatile.Write(ref _current, Build(row));

    /// <summary>Reads the overrides from the database (another process may have changed them).</summary>
    public async Task RefreshAsync(AppDbContext db, CancellationToken cancellationToken = default)
    {
        var row = await db.PollerSettings.AsNoTracking()
            .SingleOrDefaultAsync(s => s.Id == PollerSettingsRow.SingleId, cancellationToken).ConfigureAwait(false);
        Apply(row);
    }

    /// <summary>The panel's form as overrides (see <see cref="PollerTuning.Parse"/>).</summary>
    public TuningParse Parse(IReadOnlyDictionary<TuningKey, string?> values, bool reuseScreeningDetails) =>
        PollerTuning.Parse(_config, _configLimits, values, reuseScreeningDetails);

    /// <summary>What would change if these overrides applied instead of the current ones.</summary>
    public IReadOnlyList<SettingChange> ChangesTo(PollerSettingsRow? row)
    {
        var current = Volatile.Read(ref _current);
        var next = Build(row);
        return PollerTuning.Changes((current.Poller, current.Limits), (next.Poller, next.Limits));
    }

    private Snapshot Build(PollerSettingsRow? row)
    {
        var (poller, limits) = PollerTuning.Apply(_config, _configLimits, row);
        return new Snapshot(poller, limits, row?.Copy() ?? new PollerSettingsRow());
    }
}
