using System.Text.Json;
using CinemaWatch.Core;

namespace CinemaWatch.Tests;

/// <summary>
/// The hall map selection rules (<see cref="SeatSelection"/>). The cases in tests/Fixtures/seat-selection-cases.json
/// are shared with the site's JavaScript tests, so both clients behave the same.
/// </summary>
internal static class SeatSelectionTests
{
    public static void Register()
    {
        TestRunner.Add("Seat selection: every case of the shared case file", () =>
        {
            var (plan, cases) = LoadCases();
            Assert.True(cases.Count >= 30);
            var failures = new List<string>();
            foreach (var c in cases)
            {
                var actual = Run(plan, c).Order(StringComparer.Ordinal).ToList();
                if (!actual.SequenceEqual(c.After))
                    failures.Add($"{c.Name}: expected [{string.Join(", ", c.After)}], got [{string.Join(", ", actual)}]");
            }
            Assert.True(failures.Count == 0, string.Join(" | ", failures));
        });

        TestRunner.Add("Seat selection: the case file is well-formed (sorted keys, known seats, one kind per case)", () =>
        {
            var (plan, cases) = LoadCases();
            var keys = plan.Seats.Select(s => s.Key).ToHashSet(StringComparer.Ordinal);
            Assert.Equal(keys.Count, plan.Seats.Count);
            foreach (var c in cases)
            {
                Assert.True(c.Before.All(keys.Contains) && c.After.All(keys.Contains), c.Name);
                Assert.True(c.Before.SequenceEqual(c.Before.Order(StringComparer.Ordinal)), c.Name);
                Assert.True(c.After.SequenceEqual(c.After.Order(StringComparer.Ordinal)), c.Name);
                Assert.True((c.Path is null) != (c.Row is null), c.Name);
            }
            Assert.Equal(cases.Count, cases.Select(c => c.Name).Distinct().Count());
        });

        TestRunner.Add("Seat selection on the Sadyba plan: painting row 4, a rectangle over row 12 skips 1N/2N", () =>
        {
            var plan = CinemaCityParser.ParseSeatPlan(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "seatplan-13-1.json")));
            var row4 = plan.Seats.Where(s => s.RowKey == "4").OrderBy(s => s.Position).ToList();
            var empty = new HashSet<string>(StringComparer.Ordinal);

            // One long move from the first to the last seat of the row paints all 31, and only them.
            var painted = SeatSelection.ApplyGesture(plan, empty, [new(row4[0].X, row4[0].Y), new(row4[^1].X, row4[^1].Y)]);
            Assert.Equal(31, painted.Count);
            Assert.True(painted.SetEquals(row4.Select(s => s.Key)));

            // A rectangle around the whole of row 12 takes its regular seats, not the wheelchair spaces.
            var row12 = plan.Seats.Where(s => s.Row == "12").ToList();
            var r = plan.SeatRadius;
            var corner1 = new PlanPoint(row12.Min(s => s.X) - 2 * r, row12.Min(s => s.Y) - 0.9 * r);
            var corner2 = new PlanPoint(row12.Max(s => s.X) + 2 * r, row12.Max(s => s.Y) + 0.9 * r);
            Assert.True(SeatSelection.SeatAt(plan, corner1) is null);
            var rect = SeatSelection.ApplyGesture(plan, empty, [corner1, corner2]);
            Assert.True(rect.Count > 0);
            Assert.True(rect.SetEquals(row12.Where(s => !s.IsWheelchair).Select(s => s.Key)));

            // The same as clicking the row number of row 12.
            Assert.True(rect.SetEquals(SeatSelection.ToggleRow(empty, plan, "1", row12[0].RowKey)));
        });

        TestRunner.Add("Seat selection gesture: kind from the press, Result live, Before kept for a cancel", () =>
        {
            var (plan, _) = LoadCases();
            var before = new HashSet<string>(["1_5_1"], StringComparer.Ordinal);

            var paint = new SeatSelectionGesture(plan, before, new PlanPoint(100, 100));
            Assert.Equal(SeatSelectionGestureKind.Paint, paint.Kind);
            Assert.True(paint.Result.SetEquals(["1_1_1", "1_5_1"]));
            Assert.True(paint.MoveTo(new PlanPoint(170, 100)));
            Assert.True(!paint.MoveTo(new PlanPoint(175, 100)));
            Assert.True(paint.Result.SetEquals(["1_1_1", "1_2_1", "1_5_1"]));
            Assert.True(paint.Before.SetEquals(["1_5_1"]));

            var rect = new SeatSelectionGesture(plan, before, new PlanPoint(60, 60));
            Assert.Equal(SeatSelectionGestureKind.Rectangle, rect.Kind);
            Assert.True(rect.MoveTo(new PlanPoint(130, 130)));
            Assert.True(rect.MoveTo(new PlanPoint(80, 80)));
            Assert.True(rect.Result.SetEquals(before));
            Assert.Equal(new PlanPoint(80, 80), rect.Current);

            var wheelchair = new SeatSelectionGesture(plan, before, new PlanPoint(380, 240));
            Assert.Equal(SeatSelectionGestureKind.Toggle, wheelchair.Kind);
            Assert.True(!wheelchair.MoveTo(new PlanPoint(100, 240)));
            Assert.True(wheelchair.Result.SetEquals(["1_5_1", "1_5_3"]));
        });
    }

    internal sealed record SelectionCase(string Name, List<string> Before, List<PlanPoint>? Path, bool Cancel, (string Section, string RowKey)? Row, List<string> After);

    internal static (SeatPlan Plan, List<SelectionCase> Cases) LoadCases()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "seat-selection-cases.json")));
        var root = doc.RootElement;
        Assert.Equal(1, root.GetProperty("version").GetInt32());

        var seats = root.GetProperty("seats").EnumerateArray().Select(s =>
        {
            var key = s.GetProperty("key").GetString()!;
            var rowKey = s.GetProperty("rowKey").GetString()!;
            return new Seat(
                key,
                s.GetProperty("section").GetString()!,
                rowKey,
                Row: rowKey,
                Label: key,
                Position: int.Parse(key.Split('_')[1]),
                s.GetProperty("x").GetDouble(),
                s.GetProperty("y").GetDouble(),
                s.TryGetProperty("wheelchair", out var w) && w.GetBoolean());
        }).ToList();
        var plan = new SeatPlan(root.GetProperty("seatRadius").GetDouble(), [], seats);

        var cases = root.GetProperty("cases").EnumerateArray().Select(c => new SelectionCase(
            c.GetProperty("name").GetString()!,
            Keys(c.GetProperty("before")),
            c.TryGetProperty("path", out var path)
                ? path.EnumerateArray().Select(p => new PlanPoint(p[0].GetDouble(), p[1].GetDouble())).ToList()
                : null,
            c.TryGetProperty("cancel", out var cancel) && cancel.GetBoolean(),
            c.TryGetProperty("row", out var row)
                ? (row.GetProperty("section").GetString()!, row.GetProperty("rowKey").GetString()!)
                : null,
            Keys(c.GetProperty("after")))).ToList();
        return (plan, cases);

        static List<string> Keys(JsonElement array) => array.EnumerateArray().Select(k => k.GetString()!).ToList();
    }

    private static IReadOnlySet<string> Run(SeatPlan plan, SelectionCase c)
    {
        var before = new HashSet<string>(c.Before, StringComparer.Ordinal);
        if (c.Row is { } row)
            return SeatSelection.ToggleRow(before, plan, row.Section, row.RowKey);

        var gesture = new SeatSelectionGesture(plan, before, c.Path![0]);
        foreach (var point in c.Path.Skip(1))
            gesture.MoveTo(point);
        return c.Cancel ? gesture.Before : gesture.Result;
    }
}
