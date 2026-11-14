using CinemaWatch.Core;

namespace CinemaWatch.Server.Wizard;

/// <summary>
/// A hall map's selection as a short text for the wizard's form and address (docs/SAAS.md §9): one bit per seat in the
/// order of the layout's seats, as Base64Url without padding - Sadyba's 385 seats are 66 characters, where the seat
/// keys would be about 3 KB. A code made for another layout (a different number of seats) reads as nothing selected;
/// the selection is checked again when the profile is saved.
/// </summary>
public static class SeatCode
{
    /// <summary>The longest code read (a hall of 8 000 seats); anything longer is not a selection.</summary>
    public const int MaxLength = 1400;

    public static string Encode(SeatPlan plan, IReadOnlySet<string> keys)
    {
        var bytes = new byte[(plan.Seats.Count + 7) / 8];
        var any = false;
        for (var i = 0; i < plan.Seats.Count; i++)
        {
            if (!keys.Contains(plan.Seats[i].Key))
                continue;
            bytes[i / 8] |= (byte)(1 << (i % 8));
            any = true;
        }
        return any ? Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_') : "";
    }

    public static IReadOnlySet<string> Decode(SeatPlan plan, string? code)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(code) || code.Length > MaxLength || !IsWellFormed(code))
            return keys;
        var text = code.Replace('-', '+').Replace('_', '/');
        text += new string('=', (4 - text.Length % 4) % 4);
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(text);
        }
        catch (FormatException)
        {
            return keys;
        }
        if (bytes.Length != (plan.Seats.Count + 7) / 8)
            return keys;
        for (var i = 0; i < plan.Seats.Count; i++)
        {
            if ((bytes[i / 8] & (1 << (i % 8))) != 0)
                keys.Add(plan.Seats[i].Key);
        }
        return keys;
    }

    /// <summary>Only Base64Url letters (A-Z, a-z, 0-9, '-', '_').</summary>
    public static bool IsWellFormed(string code) =>
        code.Length is > 0 and <= MaxLength && code.All(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_');
}
