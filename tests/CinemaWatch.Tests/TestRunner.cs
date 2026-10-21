using System.Runtime.CompilerServices;

namespace CinemaWatch.Tests;

/// <summary>Minimal test runner so the tests need no NuGet packages.</summary>
internal static class TestRunner
{
    private static readonly List<(string Name, Func<Task> Body)> Tests = [];

    public static void Add(string name, Action body) => Tests.Add((name, () => { body(); return Task.CompletedTask; }));
    public static void Add(string name, Func<Task> body) => Tests.Add((name, body));

    public static async Task<int> RunAsync()
    {
        var failed = 0;
        foreach (var (name, body) in Tests)
        {
            try
            {
                await body();
                Console.WriteLine($"  PASS  {name}");
            }
            catch (Exception ex)
            {
                failed++;
                Console.WriteLine($"  FAIL  {name}\n        {ex.GetType().Name}: {ex.Message}");
            }
        }
        Console.WriteLine($"\n{Tests.Count - failed}/{Tests.Count} passed");
        return failed == 0 ? 0 : 1;
    }
}

internal static class Assert
{
    public static void Equal<T>(T expected, T actual, [CallerArgumentExpression(nameof(actual))] string? expression = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{expression}: expected <{expected}>, got <{actual}>");
    }

    public static void True(bool condition, [CallerArgumentExpression(nameof(condition))] string? expression = null)
    {
        if (!condition) throw new InvalidOperationException($"Expected true: {expression}");
    }

    public static void SequenceEqual<T>(IEnumerable<T> expected, IEnumerable<T> actual, [CallerArgumentExpression(nameof(actual))] string? expression = null)
    {
        var e = expected.ToList();
        var a = actual.ToList();
        if (!e.SequenceEqual(a))
            throw new InvalidOperationException($"{expression}: expected [{string.Join(", ", e)}], got [{string.Join(", ", a)}]");
    }
}
