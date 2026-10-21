namespace CinemaWatch.Core;

/// <summary>Attribute-based screening filter (attribute ids as returned by the repertoire API).</summary>
public sealed record ScreeningFilter(IReadOnlyList<string> RequiredAttributes, IReadOnlyList<string> ExcludedAttributes)
{
    /// <summary>IMAX screenings in the original language; dubbed versions are excluded entirely.</summary>
    public static ScreeningFilter ImaxWithoutDubbing { get; } = new(["imax"], ["dubbed"]);

    public bool Matches(Screening screening) =>
        RequiredAttributes.All(screening.HasAttribute) && !ExcludedAttributes.Any(screening.HasAttribute);
}
