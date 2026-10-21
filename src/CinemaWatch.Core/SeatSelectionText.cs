using System.Globalization;

namespace CinemaWatch.Core;

/// <summary>Human readable description of the watched seats in one row, e.g. row "9": "10–14, 20".</summary>
public sealed record RowSelection(string Row, string Text, bool IsWholeRow, int Count);

/// <summary>Polish descriptions of a seat selection (used in the summary and in notifications).</summary>
public static class SeatSelectionText
{
    /// <summary>Describes the selection row by row, ordered from the screen to the back.</summary>
    public static IReadOnlyList<RowSelection> Describe(SeatPlan plan, IReadOnlySet<string> watchedKeys)
    {
        var result = new List<RowSelection>();
        var rows = plan.Seats
            .GroupBy(s => (s.Section, s.RowKey))
            .OrderBy(g => g.Average(s => s.Y));

        foreach (var row in rows)
        {
            var selected = row.Where(s => watchedKeys.Contains(s.Key)).ToList();
            if (selected.Count == 0) continue;

            var regular = row.Where(s => !s.IsWheelchair).ToList();
            var isWholeRow = regular.Count > 0 && regular.All(s => watchedKeys.Contains(s.Key));

            var parts = new List<string>();
            if (isWholeRow)
                parts.Add("cały rząd");
            else if (selected.Any(s => !s.IsWheelchair))
                parts.Add(CompressLabels(selected.Where(s => !s.IsWheelchair).Select(s => s.Label)));

            var wheelchair = selected.Where(s => s.IsWheelchair).Select(s => s.Label).OrderBy(l => l, StringComparer.Ordinal).ToList();
            if (wheelchair.Count > 0)
                parts.Add($"{string.Join(", ", wheelchair)} (dla osób na wózku)");

            result.Add(new RowSelection(row.First().Row, string.Join(" + ", parts), isWholeRow, selected.Count));
        }
        return result;
    }

    /// <summary>"1, 2, 3, 5, 7, 8, 1N" → "1–3, 5, 7, 8, 1N"</summary>
    public static string CompressLabels(IEnumerable<string> labels)
    {
        var numbers = new SortedSet<int>();
        var other = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var label in labels)
        {
            if (int.TryParse(label, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) numbers.Add(n);
            else other.Add(label);
        }

        var parts = new List<string>();
        var sorted = numbers.ToList();
        for (var i = 0; i < sorted.Count; i++)
        {
            var start = sorted[i];
            var end = start;
            while (i + 1 < sorted.Count && sorted[i + 1] == end + 1)
                end = sorted[++i];

            if (start == end) parts.Add(start.ToString(CultureInfo.InvariantCulture));
            else if (end == start + 1) parts.Add($"{start}, {end}");
            else parts.Add($"{start}–{end}");
        }
        parts.AddRange(other);
        return string.Join(", ", parts);
    }
}
