using System.Net;
using CinemaWatch.Core;
using CinemaWatch.Tests;

// Fixtures are real responses captured on 2026-10-21 for "Diuna: Część trzecia",
// Cinema City Sadyba, IMAX, screening 2026-12-16 16:00 (id 1765592).

static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
var day = new DateOnly(2026, 12, 16);

TestRunner.Add("Repertoire: films and screenings are parsed", () =>
{
    var rep = CinemaCityParser.ParseRepertoire(Fixture("repertoire-1060-2026-12-16.json"), day);
    Assert.Equal(25, rep.Screenings.Count);
    var dune = rep.Films.Single(f => f.Id == "8105s2r");
    Assert.Equal("Diuna: Część trzecia", dune.Name);
    Assert.Equal(150, dune.LengthMinutes);
    var s = rep.Screenings.Single(x => x.Id == "1765592");
    Assert.Equal(new DateTime(2026, 12, 16, 16, 0, 0), s.StartsAt);
    Assert.Equal("IMAX BNP Paribas", s.Auditorium);
    Assert.SequenceEqual(["pl"], s.SubtitleLanguages);
    Assert.Equal(0.5195, s.AvailabilityRatio);
});

TestRunner.Add("Filter: IMAX without dubbing keeps the 4 subtitled IMAX screenings", () =>
{
    var rep = CinemaCityParser.ParseRepertoire(Fixture("repertoire-1060-2026-12-16.json"), day);
    var imax = rep.Screenings
        .Where(s => s.FilmId == "8105s2r" && ScreeningFilter.ImaxWithoutDubbing.Matches(s))
        .OrderBy(s => s.StartsAt)
        .ToList();
    Assert.SequenceEqual(["12:45", "16:00", "19:15", "22:30"], imax.Select(s => s.StartsAt.ToString("HH:mm")));
    Assert.True(imax.Single(s => s.Id == "1765621").SoldOut);
    // The 09:30 IMAX screening is dubbed and must be excluded.
    Assert.True(rep.Screenings.Any(s => s.Id == "1765492" && s.HasAttribute("imax") && s.HasAttribute("dubbed")));
});

TestRunner.Add("Presentation: venue and seat plan ids", () =>
{
    var p = CinemaCityParser.ParsePresentation(Fixture("presentation-1765592.json"));
    Assert.Equal("1765592", p.Id);
    Assert.Equal(13, p.VenueId);
    Assert.Equal(1, p.SeatplanId);
    Assert.Equal(1, p.VenueTypeId);
    Assert.Equal(1, p.IsReserved);
    Assert.Equal(new DateTime(2026, 12, 16, 16, 15, 0), p.TicketSaleStop);
    Assert.True(!p.SoldOut);
});

TestRunner.Add("Seat plan: 385 seats in 12 rows, 2 wheelchair spaces", () =>
{
    var plan = CinemaCityParser.ParseSeatPlan(Fixture("seatplan-13-1.json"));
    Assert.Equal(385, plan.Seats.Count);
    Assert.Equal(12, plan.Rows.Count);
    Assert.Equal(30.0, plan.SeatRadius);
    Assert.SequenceEqual(["2N", "1N"], plan.Seats.Where(s => s.IsWheelchair).OrderBy(s => s.Position).Select(s => s.Label));
    Assert.Equal(31, plan.SeatsInRow("4").Count());
    var seat = plan.Seats.Single(s => s.Key == "1_18_4");
    Assert.Equal("19", seat.Label);
    Assert.Equal("4", seat.Row);
    // Positions are physically ordered left to right with a constant spacing.
    var row4 = plan.SeatsInRow("4").ToList();
    Assert.True(row4.Zip(row4.Skip(1)).All(p => p.Second.Position == p.First.Position + 1 && p.Second.X > p.First.X));
});

TestRunner.Add("Seat status: 200 free seats; row 4 seats 17 and 16 taken (matches the website)", () =>
{
    var plan = CinemaCityParser.ParseSeatPlan(Fixture("seatplan-13-1.json"));
    var free = CinemaCityParser.ParseFreeSeats(Fixture("seats-status-1765592.json"));
    Assert.Equal(200, free.Count);
    var taken = plan.SeatsInRow("4").Where(s => !free.Contains(s.Key)).Select(s => s.Label);
    Assert.SequenceEqual(["17", "16"], taken);
});

TestRunner.Add("Seat status: sold out responses (empty object or array) give no free seats", () =>
{
    Assert.Equal(0, CinemaCityParser.ParseFreeSeats("""{"seats":{}}""").Count);
    Assert.Equal(0, CinemaCityParser.ParseFreeSeats("""{"seats":[]}""").Count);
});

TestRunner.Add("Counts: free wheelchair spaces alone do not count as free seats (15.12 19:00 case)", () =>
{
    // Real case: the website showed the screening as sold out while the API listed 1N and 2N in row 12 as free.
    var plan = CinemaCityParser.ParseSeatPlan(Fixture("seatplan-13-1.json"));
    var presentation = CinemaCityParser.ParsePresentation(Fixture("presentation-1765592.json"));
    var seats = new ScreeningSeats(presentation, plan, new HashSet<string> { "1_4_12", "1_40_12" });
    Assert.Equal(0, seats.FreeCount);
    Assert.Equal(383, seats.TotalCount);
    Assert.Equal(2, seats.FreeWheelchairCount);
});

TestRunner.Add("Matcher: free watched seats, minimum adjacent run respected", () =>
{
    var plan = CinemaCityParser.ParseSeatPlan(Fixture("seatplan-13-1.json"));
    var presentation = CinemaCityParser.ParsePresentation(Fixture("presentation-1765592.json"));
    var seats = new ScreeningSeats(presentation, plan, CinemaCityParser.ParseFreeSeats(Fixture("seats-status-1765592.json")));
    var row4 = plan.SeatsInRow("4").Select(s => s.Key).ToHashSet();

    // Row 4: seats 17 and 16 taken -> free runs of 14 (31..18) and 15 (15..1).
    Assert.Equal(29, SeatMatcher.FindAvailable(seats, row4, 1).Count);
    Assert.SequenceEqual(Enumerable.Range(1, 15).Reverse().Select(n => n.ToString()),
        SeatMatcher.FindAvailable(seats, row4, 15).Select(s => s.Label));
    Assert.Equal(0, SeatMatcher.FindAvailable(seats, row4, 16).Count);

    // Watching only seats around the taken ones: 19,18 | 17,16 taken | 15,14
    var around = plan.SeatsInRow("4").Where(s => s.Label is "19" or "18" or "17" or "16" or "15" or "14").Select(s => s.Key).ToHashSet();
    Assert.SequenceEqual(["19", "18", "15", "14"], SeatMatcher.FindAvailable(seats, around, 2).Select(s => s.Label));
    Assert.Equal(0, SeatMatcher.FindAvailable(seats, around, 3).Count);
});

TestRunner.Add("Selection text: whole rows, ranges and wheelchair spaces", () =>
{
    var plan = CinemaCityParser.ParseSeatPlan(Fixture("seatplan-13-1.json"));
    var keys = plan.SeatsInRow("8").Where(s => !s.IsWheelchair).Select(s => s.Key)
        .Concat(plan.SeatsInRow("9").Where(s => s.Label is "10" or "11" or "12" or "13" or "14" or "20").Select(s => s.Key))
        .Concat(plan.SeatsInRow("12").Where(s => s.Label is "1N").Select(s => s.Key))
        .ToHashSet();
    var rows = SeatSelectionText.Describe(plan, keys);
    Assert.SequenceEqual(["8", "9", "12"], rows.Select(r => r.Row));
    Assert.Equal("cały rząd", rows[0].Text);
    Assert.True(rows[0].IsWholeRow);
    Assert.Equal("10–14, 20", rows[1].Text);
    Assert.Equal("1N (dla osób na wózku)", rows[2].Text);
    Assert.Equal("1–3, 5, 7, 8, 1N", SeatSelectionText.CompressLabels(["8", "1", "3", "2", "5", "7", "1N"]));
});

TestRunner.Add("Client + service: correct requests, uuid header, seat plan cached", async () =>
{
    var handler = new FixtureHttpHandler();
    using var client = new CinemaCityClient(handler);
    var service = new SeatAvailabilityService(client);

    var rep = await client.GetRepertoireAsync("1060", day);
    Assert.Equal(25, rep.Screenings.Count);

    var first = await service.GetScreeningSeatsAsync("1765592");
    var second = await service.GetScreeningSeatsAsync("1765592");
    // 200 free keys in the API = 198 regular seats + both wheelchair spaces (1N, 2N).
    Assert.Equal(383, first.TotalCount);
    Assert.Equal(198, first.FreeCount);
    Assert.Equal(2, first.WheelchairCount);
    Assert.Equal(2, first.FreeWheelchairCount);
    Assert.Equal(198, second.FreeCount);

    Assert.Equal(1, handler.Requests.Count(r => r.Uri.AbsolutePath.EndsWith("/seatplanV2") && r.Method == HttpMethod.Post));
    Assert.True(handler.Requests.All(r => Guid.TryParse(r.Uuid, out _)));
    Assert.Equal(1, handler.Requests.Select(r => r.Uuid).Distinct().Count());
});

TestRunner.Add("Errors: HTTP 403 is reported as a possible Cloudflare block", async () =>
{
    using var client = new CinemaCityClient(new FixtureHttpHandler());
    try
    {
        await client.GetPresentationAsync("999");
        throw new InvalidOperationException("Expected an exception");
    }
    catch (CinemaCityException ex)
    {
        Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
        Assert.True(ErrorMessages.Describe(ex).Contains("404"));
    }
    var forbidden = new CinemaCityException(HttpStatusCode.Forbidden, "/api/x");
    Assert.True(ErrorMessages.Describe(forbidden).Contains("Cloudflare"));
});

// ---------------------------------------------------------------- stage 3: profiles, checks, notifications

WatchProfile SampleProfile(params string[] seatKeys) => new()
{
    Name = "Diuna IMAX test",
    CinemaId = "1060",
    CinemaName = "Cinema City Sadyba",
    FilmId = "8105s2r",
    FilmName = "Diuna: Część trzecia",
    DateFrom = day,
    DateTo = day,
    Screenings =
    [
        new WatchedScreening
        {
            Id = "1765592",
            StartsAt = new DateTime(2026, 12, 16, 16, 0, 0),
            Auditorium = "IMAX BNP Paribas",
            BookingLink = "https://tickets.cinema-city.pl/api/order/1765592?lang=pl&x=1",
            SeatKeys = seatKeys.ToList(),
        },
    ],
};

TestRunner.Add("Profile: JSON round trip keeps Polish text readable", () =>
{
    var path = Path.Combine(Path.GetTempPath(), $"cw-{Guid.NewGuid():N}{ProfileStore.FileExtension}");
    try
    {
        ProfileStore.Save(path, SampleProfile("1_18_4", "1_19_4"));
        var json = File.ReadAllText(path);
        Assert.True(json.Contains("Diuna: Część trzecia"));
        Assert.True(json.Contains("\"dateFrom\": \"2026-12-16\""));
        var loaded = ProfileStore.Load(path);
        Assert.Equal("8105s2r", loaded.FilmId);
        Assert.SequenceEqual(["1_18_4", "1_19_4"], loaded.Screenings[0].SeatKeys);
        Assert.Equal(new DateTime(2026, 12, 16, 16, 0, 0), loaded.Screenings[0].StartsAt);
        Assert.Equal(Path.GetFileName(path)[..^ProfileStore.FileExtension.Length], ProfileStore.NameFromPath(path));
    }
    finally
    {
        File.Delete(path);
    }
    Assert.Equal("Diuna Część trzecia 15.12–16.12", ProfileStore.ToFileName("Diuna: Część trzecia 15.12–16.12"));
});

TestRunner.Add("Checker: available seats per screening, past screenings skipped", async () =>
{
    using var client = new CinemaCityClient(new FixtureHttpHandler());
    var service = new SeatAvailabilityService(client);
    var plan = CinemaCityParser.ParseSeatPlan(Fixture("seatplan-13-1.json"));
    var row4 = plan.SeatsInRow("4").Select(s => s.Key).ToArray();

    var result = await ProfileChecker.CheckAsync(SampleProfile(row4), service, new DateTime(2026, 10, 21, 12, 0, 0));
    Assert.Equal(29, result.Screenings.Single().Available.Count);
    Assert.True(result.HasMatches);

    var past = await ProfileChecker.CheckAsync(SampleProfile(row4), service, new DateTime(2026, 12, 16, 16, 0, 0));
    Assert.True(past.Screenings.Single().IsPast);
    Assert.True(!past.HasMatches && !past.AllFailed);
});

TestRunner.Add("State: notify only about newly freed seats", () =>
{
    var plan = CinemaCityParser.ParseSeatPlan(Fixture("seatplan-13-1.json"));
    Seat S(string key) => plan.Seats.Single(s => s.Key == key);
    var profile = SampleProfile();
    var screening = profile.Screenings[0];
    ProfileCheckResult Result(params string[] keys) =>
        new(DateTime.Now, [new ScreeningCheck(screening, null, keys.Select(S).ToList(), null, false)]);

    var state = new NotificationState();
    Assert.Equal(2, state.Update(Result("1_18_4", "1_19_4")).Single().NewSeats.Count);
    Assert.Equal(0, state.Update(Result("1_18_4", "1_19_4")).Count);           // same seats -> nothing new
    Assert.Equal(0, state.Update(Result("1_19_4")).Count);                     // one taken -> nothing new
    Assert.Equal("1_18_4", state.Update(Result("1_18_4", "1_19_4")).Single().NewSeats.Single().Key); // freed again
    var failed = new ProfileCheckResult(DateTime.Now, [new ScreeningCheck(screening, null, [], "błąd", false)]);
    Assert.Equal(0, state.Update(failed).Count);
    Assert.Equal(2, state.Available[screening.Id].Count);                      // error keeps previous state
});

TestRunner.Add("Notification: text and toast XML (escaped URL, protocol activation)", () =>
{
    var plan = CinemaCityParser.ParseSeatPlan(Fixture("seatplan-13-1.json"));
    var profile = SampleProfile();
    var seats = plan.Seats.Where(s => s.Key is "1_18_4" or "1_19_4" or "1_20_9").ToList();
    var n = NotificationBuilder.Build(profile, [(profile.Screenings[0], seats)]);
    Assert.Equal("Wolne miejsca: Diuna: Część trzecia", n.Title);
    Assert.Equal("śr. 16.12, 16:00 · rząd 4: 18, 19 · rząd 9: 20", n.Lines.Single());

    var xml = ToastXml.Build(n);
    Assert.True(xml.Contains("activationType=\"protocol\""));
    Assert.True(xml.Contains("lang=pl&amp;x=1"));
    Assert.True(xml.Contains("<action content=\"Kup bilet\""));
    var parsed = System.Xml.Linq.XDocument.Parse(xml);
    Assert.Equal(2, parsed.Descendants("text").Count());
});

TestRunner.Add("Scheduler: schtasks arguments and task name", () =>
{
    var args = SchtasksScheduler.BuildCreateArguments(@"\CinemaWatch\Diuna IMAX", @"C:\Apps\CinemaWatch.exe", @"C:\Profiles\Diuna IMAX.cinemawatch.json", 10);
    Assert.SequenceEqual(["/Create", "/F", "/TN", @"\CinemaWatch\Diuna IMAX", "/SC", "MINUTE", "/MO", "10", "/TR",
        "\"C:\\Apps\\CinemaWatch.exe\" --profile \"C:\\Profiles\\Diuna IMAX.cinemawatch.json\""], args);
    Assert.Equal(@"\CinemaWatch\Diuna IMAX", SchtasksScheduler.TaskNameFor(@"C:\Profiles\Diuna IMAX.cinemawatch.json".Replace('\\', Path.DirectorySeparatorChar)));
    Assert.Equal("C:\\p.json", HeadlessRunner.GetProfileArgument(["--profile", "C:\\p.json"]));
    Assert.Equal("C:\\p.json", HeadlessRunner.GetProfileArgument(["--profile=\"C:\\p.json\""]));
    Assert.Equal(null, HeadlessRunner.GetProfileArgument([]));
});

TestRunner.Add("Headless: notifies once, then stays quiet; bad profile -> exit code 1", async () =>
{
    var path = Path.Combine(Path.GetTempPath(), $"cw-{Guid.NewGuid():N}{ProfileStore.FileExtension}");
    var log = new FileLog(Path.Combine(Path.GetTempPath(), $"cw-{Guid.NewGuid():N}.log"));
    var plan = CinemaCityParser.ParseSeatPlan(Fixture("seatplan-13-1.json"));
    ProfileStore.Save(path, SampleProfile(plan.SeatsInRow("4").Select(s => s.Key).ToArray()));
    var notifier = new RecordingNotifier();
    using var client = new CinemaCityClient(new FixtureHttpHandler());
    var runner = new HeadlessRunner(new SeatAvailabilityService(client), notifier, log, () => new DateTime(2026, 10, 21, 12, 0, 0));
    try
    {
        Assert.Equal(0, await runner.RunAsync(path));
        Assert.Equal(1, notifier.Shown.Count);
        Assert.Equal(0, await runner.RunAsync(path));
        Assert.Equal(1, notifier.Shown.Count);
        Assert.True(File.ReadAllText(log.Path).Contains("wolnych z zaznaczenia 29"));
        Assert.Equal(1, await runner.RunAsync(path + ".missing"));
    }
    finally
    {
        File.Delete(path);
        File.Delete(AppPaths.StateFileFor(path));
        File.Delete(log.Path);
    }
});

TestRunner.Add("Headless: after the last screening the scheduled task removes itself", async () =>
{
    var path = Path.Combine(Path.GetTempPath(), $"cw-{Guid.NewGuid():N}{ProfileStore.FileExtension}");
    var log = new FileLog(Path.Combine(Path.GetTempPath(), $"cw-{Guid.NewGuid():N}.log"));
    ProfileStore.Save(path, SampleProfile("1_18_4"));
    var scheduler = new FakeScheduler();
    var taskName = SchtasksScheduler.TaskNameFor(path);
    await scheduler.RegisterAsync(taskName, "CinemaWatch.exe", path, 10);
    using var client = new CinemaCityClient(new FixtureHttpHandler());
    var service = new SeatAvailabilityService(client);
    try
    {
        // Before the screening: task stays.
        await new HeadlessRunner(service, new RecordingNotifier(), log, () => new DateTime(2026, 12, 1), scheduler).RunAsync(path);
        Assert.True(scheduler.Tasks.ContainsKey(taskName));
        // After the screening: task removed, logged.
        Assert.Equal(0, await new HeadlessRunner(service, new RecordingNotifier(), log, () => new DateTime(2026, 12, 17), scheduler).RunAsync(path));
        Assert.True(!scheduler.Tasks.ContainsKey(taskName));
        Assert.True(File.ReadAllText(log.Path).Contains("nie jest już potrzebne"));
        Assert.True(!File.Exists(AppPaths.StateFileFor(path)));
    }
    finally
    {
        File.Delete(path);
        File.Delete(log.Path);
    }
});

TestRunner.Add("Seat plan cache: second process reuses the hall layout from disk", async () =>
{
    var cache = Path.Combine(Path.GetTempPath(), $"cw-cache-{Guid.NewGuid():N}");
    try
    {
        var first = new FixtureHttpHandler();
        using (var client = new CinemaCityClient(first))
            Assert.Equal(198, (await new SeatAvailabilityService(client, cache).GetScreeningSeatsAsync("1765592")).FreeCount);
        var second = new FixtureHttpHandler();
        using (var client = new CinemaCityClient(second))
            Assert.Equal(198, (await new SeatAvailabilityService(client, cache).GetScreeningSeatsAsync("1765592")).FreeCount);

        Assert.Equal(1, first.Requests.Count(r => r.Uri.AbsolutePath.EndsWith("/seatplanV2")));
        Assert.Equal(0, second.Requests.Count(r => r.Uri.AbsolutePath.EndsWith("/seatplanV2")));
        Assert.Equal(2, second.Requests.Count); // presentation + seat status only
    }
    finally
    {
        Directory.Delete(cache, recursive: true);
    }
});

TestRunner.Add("Localization: language detection, plurals, translated Core texts", () =>
{
    Assert.Equal(AppLanguage.Polish, Localizer.Detect(System.Globalization.CultureInfo.GetCultureInfo("pl-PL")));
    Assert.Equal(AppLanguage.English, Localizer.Detect(System.Globalization.CultureInfo.GetCultureInfo("en-US")));
    Assert.Equal(AppLanguage.English, Localizer.Detect(System.Globalization.CultureInfo.GetCultureInfo("de-DE")));
    Assert.Equal(AppLanguage.English, Localizer.Parse("EN"));
    Assert.Equal(null, Localizer.Parse("xx"));
    Assert.True(System.Text.RegularExpressions.Regex.IsMatch(AppInfo.Version, @"^\d+\.\d+\.\d+$"));

    var plan = CinemaCityParser.ParseSeatPlan(Fixture("seatplan-13-1.json"));
    var profile = SampleProfile();
    var seats = plan.Seats.Where(s => s.Key is "1_18_4" or "1_19_4").ToList();
    try
    {
        Localizer.SetLanguage(AppLanguage.Polish);
        Assert.Equal("5 seansów", Localizer.Count(5, "seans", "seanse", "seansów", "screening", "screenings"));
        Assert.Equal("22 seanse", Localizer.Count(22, "seans", "seanse", "seansów", "screening", "screenings"));

        Localizer.SetLanguage(AppLanguage.English);
        Assert.Equal("1 screening", Localizer.Count(1, "seans", "seanse", "seansów", "screening", "screenings"));
        Assert.Equal("5 screenings", Localizer.Count(5, "seans", "seanse", "seansów", "screening", "screenings"));
        var n = NotificationBuilder.Build(profile, [(profile.Screenings[0], seats)]);
        Assert.Equal("Seats available: Diuna: Część trzecia", n.Title);
        Assert.Equal("Wed 16 Dec, 16:00 · row 4: 18, 19", n.Lines.Single());
        Assert.True(ToastXml.Build(n).Contains("content=\"Buy ticket\""));
        Assert.Equal("whole row", SeatSelectionText.Describe(plan, plan.SeatsInRow("8").Where(s => !s.IsWheelchair).Select(s => s.Key).ToHashSet()).Single().Text);
        Assert.True(ErrorMessages.Describe(new CinemaCityException(HttpStatusCode.NotFound, "/x")).StartsWith("Cinema City could not find"));
    }
    finally
    {
        Localizer.SetLanguage(AppLanguage.Polish);
    }
});

ViewModelTests.Register();
ViewModelTests.RegisterLanguageTests();

return await TestRunner.RunAsync();

internal sealed class RecordingNotifier : INotifier
{
    public List<AvailabilityNotification> Shown { get; } = [];
    public void Show(AvailabilityNotification notification) => Shown.Add(notification);
}
