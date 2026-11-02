using System.Text;
using CinemaWatch.Core;
using CinemaWatch.Probe;
using CinemaWatch.Tests;

// Tests for the SaaS probe (stage 2, risk test R1). Fixtures: real responses captured on 2026-10-21 for
// "Diuna: Część trzecia", Cinema City Sadyba, IMAX, screening 2026-12-16 16:00 (id 1765592).

Localizer.SetLanguage(AppLanguage.English);
const string UserAgent = "Mozilla/5.0 (X11; Linux x86_64) CinemaWatch-Probe/0.1.0 (+https://github.com/damiankowalski/CinemaWatch)";
var morning = new DateTimeOffset(2026, 11, 2, 9, 30, 0, TimeSpan.Zero);

ProbeRunner Runner(TempFolder t, FakeNetwork network, TimeProvider clock, TextWriter output, int repertoireMinutes = 0) =>
    new(new ProbeOptions(t.Profiles, t.Data, TimeSpan.FromMinutes(repertoireMinutes)), network, clock, output, UserAgent);

// ---------------------------------------------------------------- command line and identity

TestRunner.Add("Options: both folders required, '--name value' and '--name=value', repertoire interval", () =>
{
    var options = ProbeOptions.Parse(["--profiles", "/etc/cinemawatch-probe", "--data=/var/lib/cinemawatch-probe"], out var error)!;
    Assert.Equal("/etc/cinemawatch-probe", options.ProfilesDirectory);
    Assert.Equal("/var/lib/cinemawatch-probe", options.DataDirectory);
    Assert.Equal(TimeSpan.FromMinutes(60), options.RepertoireInterval);
    Assert.True(error is null);

    Assert.Equal(TimeSpan.Zero, ProbeOptions.Parse(["--profiles", "p", "--data", "d", "--repertoire-every", "0"], out _)!.RepertoireInterval);
    Assert.True(ProbeOptions.Parse(["--profiles", "p"], out error) is null && error!.Contains("--data"));
    Assert.True(ProbeOptions.Parse(["--profiles", "p", "--data", "d", "--repertoire-every", "-5"], out error) is null && error!.Contains("minutes"));
    Assert.True(ProbeOptions.Parse(["--interval", "5", "--profiles", "p", "--data", "d"], out error) is null && error!.Contains("--interval"));
    Assert.True(ProbeOptions.IsHelp(["--help"]) && !ProbeOptions.IsHelp(["--profiles", "p"]));
});

TestRunner.Add("User-Agent: real platform, probe name and version, project link; accepted as an HTTP header", () =>
{
    Assert.Equal(UserAgent, ProbeInfo.BuildUserAgent("X11; Linux x86_64", "0.1.0"));
    Assert.Equal("0.1.0", ProbeInfo.Version);
    Assert.True(ProbeInfo.UserAgent.Contains(OperatingSystem.IsWindows() ? "(Windows NT 10.0" : "(X11; Linux"));
    using var http = new HttpClient();
    http.DefaultRequestHeaders.UserAgent.ParseAdd(ProbeInfo.UserAgent);
    Assert.Equal(ProbeInfo.UserAgent, http.DefaultRequestHeaders.UserAgent.ToString());
});

// ---------------------------------------------------------------- request log and stop on block

TestRunner.Add("Recorder: one CSV row per request with endpoint, status, size and Cloudflare headers; body still readable", async () =>
{
    using var t = new TempFolder();
    var network = new FakeNetwork { Override = _ => FakeNetwork.Respond(200, "{}", "application/json", ("cf-ray", "9a1b2c3d4e5f-WAW")) };
    var log = new RequestLog(t.Data);
    using var recorder = new RequestRecorder(network, disposeNetwork: false, log, new FakeClock(morning), "run1");
    using var http = new HttpClient(recorder, disposeHandler: false);

    var response = await http.GetAsync("https://tickets.cinema-city.pl/api/presentations/1765592?referralMiniSiteId=0");
    Assert.Equal("{}", await response.Content.ReadAsStringAsync());

    Assert.Equal("requests-2026-11.csv", Path.GetFileName(log.PathFor(morning)));
    var lines = File.ReadAllLines(log.PathFor(morning));
    Assert.Equal(RequestLog.Header, lines[0]);
    Assert.Equal(2, lines.Length);
    Assert.SequenceEqual(
        ["2026-11-02T09:30:00.000Z", "run1", "GET", "tickets.cinema-city.pl", "presentation", "/api/presentations/1765592?referralMiniSiteId=0",
         "200", lines[1].Split(',')[7], "2", "application/json", "", "9a1b2c3d4e5f-WAW", "", ""],
        lines[1].Split(','));
    Assert.True(recorder.BlockedBy is null && recorder.Skipped == 0);
});

TestRunner.Add("Recorder: HTTP 403 with a Cloudflare challenge stops the run - the next request is not sent", async () =>
{
    using var t = new TempFolder();
    const string page = "<html><title>Just a moment...</title></html>";
    var network = new FakeNetwork { Override = _ => FakeNetwork.Respond(403, page, "text/html", ("cf-mitigated", "challenge")) };
    using var recorder = new RequestRecorder(network, disposeNetwork: false, new RequestLog(t.Data), new FakeClock(morning), "run1");
    using var http = new HttpClient(recorder, disposeHandler: false);

    var first = await http.GetAsync("https://tickets.cinema-city.pl/api/presentations/1765592");
    Assert.Equal(403, (int)first.StatusCode);
    try
    {
        await http.GetAsync("https://tickets.cinema-city.pl/api/presentations/1765593");
        throw new InvalidOperationException("Expected the second request to be refused");
    }
    catch (ProbeStoppedException ex)
    {
        Assert.True(ex.Message.Contains("HTTP 403 (cf-mitigated: challenge)"));
    }

    Assert.Equal(1, network.Requests.Count);
    Assert.Equal(1, recorder.Skipped);
    Assert.Equal("challenge", recorder.BlockedBy!.CfMitigated);
    Assert.Equal(page, Encoding.UTF8.GetString(recorder.BlockedBody!));
    Assert.Equal(1, t.CsvRows().Length);
});

TestRunner.Add("Recorder: HTTP 429 blocks too; a network error is logged but does not block", async () =>
{
    using var t = new TempFolder();
    var tooMany = new FakeNetwork { Override = _ => FakeNetwork.Respond(429, "", "text/plain", ("Retry-After", "120")) };
    using (var recorder = new RequestRecorder(tooMany, false, new RequestLog(t.Data), new FakeClock(morning), "run1"))
    using (var http = new HttpClient(recorder, false))
    {
        await http.GetAsync("https://tickets.cinema-city.pl/api/seats/seats-statusV2?presentationId=1");
        Assert.Equal(429, recorder.BlockedBy!.Status);
        Assert.Equal("120", recorder.BlockedBy.RetryAfter);
        Assert.Equal("seats-status", recorder.BlockedBy.Endpoint);
    }

    var refused = new FakeNetwork { Override = _ => throw new HttpRequestException("Connection refused") };
    using (var recorder = new RequestRecorder(refused, false, new RequestLog(t.Data), new FakeClock(morning), "run2"))
    using (var http = new HttpClient(recorder, false))
    {
        for (var i = 0; i < 2; i++)
        {
            try
            {
                await http.GetAsync("https://tickets.cinema-city.pl/api/presentations/1");
            }
            catch (HttpRequestException)
            {
            }
        }
        Assert.True(recorder.BlockedBy is null);
        Assert.Equal(2, refused.Requests.Count);
        Assert.Equal("HttpRequestException: Connection refused", recorder.Records[0].Error);
    }
    Assert.Equal(3, t.CsvRows().Length);
});

TestRunner.Add("CSV: values with commas and quotes are quoted, line breaks flattened, empty values left empty", () =>
{
    Assert.Equal("a,\"b,c\",\"say \"\"hi\"\"\",,line break", Csv.Line("a", "b,c", "say \"hi\"", null, "line\nbreak"));
});

// ---------------------------------------------------------------- whole runs

TestRunner.Add("Run: checks a profile with 3 requests and would notify once; the next run uses the cached layout and stays quiet", async () =>
{
    using var t = new TempFolder();
    t.WriteProfile("Diuna Warszawa", Fixtures.Profile("1765592", Fixtures.Row4Keys()));
    var network = new FakeNetwork();
    var clock = new FakeClock(morning);

    var first = new StringWriter();
    Assert.Equal(ProbeExitCodes.Checked, await Runner(t, network, clock, first).RunAsync());
    Assert.SequenceEqual(["presentation", "seatplan", "seats-status"], t.CsvRows().Select(r => r.Split(',')[4]));
    Assert.True(network.Requests.All(r => r.UserAgent == UserAgent));
    var text = first.ToString();
    Assert.True(text.Contains("1 profile(s), 1 screening(s) to check, Polish time 2026-11-02 10:30"));
    Assert.True(text.Contains("[Diuna Warszawa] Warszawa Wed 16.12 16:00 (1765592): 29 free watched seat(s) of 31 (hall 198/383 free)"));
    Assert.True(text.Contains("WOULD NOTIFY [Diuna Warszawa] Seats available: Diuna: Część trzecia | Warsaw · Wed 16 Dec, 16:00 · row 4: "));
    Assert.True(text.Contains("| https://tickets.cinema-city.pl/api/order/1765592?lang=pl"));
    Assert.True(text.Contains("3 request(s) (200 x3), 0 screening error(s), 1 would-notify, exit 0"));
    Assert.Equal(1, File.ReadAllLines(Path.Combine(t.Data, ProbeRunner.WouldNotifyFile)).Length);

    clock.Now = morning.AddMinutes(10);
    var second = new StringWriter();
    Assert.Equal(ProbeExitCodes.Checked, await Runner(t, network, clock, second).RunAsync());
    Assert.Equal(5, network.Requests.Count);
    Assert.True(second.ToString().Contains("2 request(s) (200 x2), 0 screening error(s), 0 would-notify, exit 0"));
    Assert.Equal(1, File.ReadAllLines(Path.Combine(t.Data, ProbeRunner.WouldNotifyFile)).Length);
});

TestRunner.Add("Run: two profiles watching the same screening fetch it once; each profile keeps its own state", async () =>
{
    using var t = new TempFolder();
    t.WriteProfile("A", Fixtures.Profile("1765592", Fixtures.Row4Keys()));
    t.WriteProfile("B", Fixtures.Profile("1765592", Fixtures.Row4Keys().Take(3).ToArray()));
    var network = new FakeNetwork();
    var output = new StringWriter();

    Assert.Equal(ProbeExitCodes.Checked, await Runner(t, network, new FakeClock(morning), output).RunAsync());
    Assert.Equal(3, network.Requests.Count);
    Assert.True(output.ToString().Contains("2 profile(s), 1 screening(s) to check"));
    Assert.True(output.ToString().Contains("2 would-notify"));
    Assert.True(File.Exists(Path.Combine(t.Data, "state", "A.json")) && File.Exists(Path.Combine(t.Data, "state", "B.json")));
});

TestRunner.Add("Run: blocked -> exit 3, nothing more sent (no repertoire either), the challenge page is kept", async () =>
{
    using var t = new TempFolder();
    t.WriteProfile("A", Fixtures.Profile("1765592", Fixtures.Row4Keys()));
    t.WriteProfile("B", Fixtures.Profile("1765600", Fixtures.Row4Keys()));
    var network = new FakeNetwork { Override = _ => FakeNetwork.Respond(403, "<html>challenge</html>", "text/html", ("cf-mitigated", "challenge")) };
    var output = new StringWriter();

    Assert.Equal(ProbeExitCodes.Blocked, await Runner(t, network, new FakeClock(morning), output, repertoireMinutes: 60).RunAsync());
    Assert.Equal(1, network.Requests.Count);
    var text = output.ToString();
    Assert.True(text.Contains("BLOCKED: HTTP 403 (cf-mitigated: challenge) on GET tickets.cinema-city.pl/api/presentations/1765592"));
    Assert.True(text.Contains("1 request(s) (403 x1), 1 skipped after the block, 2 screening error(s), 0 would-notify, exit 3"));
    Assert.Equal("<html>challenge</html>", File.ReadAllText(Directory.GetFiles(Path.Combine(t.Data, "blocked"), "*.html").Single()));
    Assert.True(!File.Exists(Path.Combine(t.Data, ProbeRunner.RepertoireStampFile)));
});

TestRunner.Add("Run: Polish time decides what is over, not the server's UTC clock", async () =>
{
    // Screening 16 Dec 16:00 in Warsaw = 15:00 UTC.
    using var t = new TempFolder();
    t.WriteProfile("A", Fixtures.Profile("1765592", Fixtures.Row4Keys()));
    var network = new FakeNetwork();

    var after = new StringWriter();
    Assert.Equal(ProbeExitCodes.NothingToCheck,
        await Runner(t, network, new FakeClock(new DateTimeOffset(2026, 12, 16, 15, 30, 0, TimeSpan.Zero)), after).RunAsync());
    Assert.Equal(0, network.Requests.Count);
    Assert.True(after.ToString().Contains("over - skipped") && after.ToString().Contains("NOTHING TO CHECK"));

    Assert.Equal(ProbeExitCodes.Checked,
        await Runner(t, network, new FakeClock(new DateTimeOffset(2026, 12, 16, 14, 30, 0, TimeSpan.Zero)), new StringWriter()).RunAsync());
    Assert.Equal(3, network.Requests.Count);
});

TestRunner.Add("Run: repertoire of the watched cinema once per hour (today's Polish date), never with 0", async () =>
{
    using var t = new TempFolder();
    t.WriteProfile("A", Fixtures.Profile("1765592", Fixtures.Row4Keys()));
    var network = new FakeNetwork();
    var start = new DateTimeOffset(2026, 12, 16, 8, 0, 0, TimeSpan.Zero); // 09:00 in Warsaw
    var clock = new FakeClock(start);

    var first = new StringWriter();
    await Runner(t, network, clock, first, repertoireMinutes: 60).RunAsync();
    Assert.Equal(1, network.Count("/film-events/in-cinema/1060/at-date/2026-12-16"));
    Assert.True(first.ToString().Contains("Repertoire 1060 2026-12-16: "));

    clock.Now = start.AddMinutes(11);
    await Runner(t, network, clock, new StringWriter(), repertoireMinutes: 60).RunAsync();
    Assert.Equal(1, network.Count("/film-events/"));

    clock.Now = start.AddMinutes(56); // within the 5-minute tolerance of the hour
    await Runner(t, network, clock, new StringWriter(), repertoireMinutes: 60).RunAsync();
    Assert.Equal(2, network.Count("/film-events/"));

    clock.Now = start.AddMinutes(200);
    await Runner(t, network, clock, new StringWriter(), repertoireMinutes: 0).RunAsync();
    Assert.Equal(2, network.Count("/film-events/"));
});

TestRunner.Add("Run: unreadable profiles are reported and skipped; none readable or no data folder -> exit 1", async () =>
{
    using var t = new TempFolder();
    File.WriteAllText(Path.Combine(t.Profiles, "broken" + ProfileStore.FileExtension), "{ not json");
    var newer = Fixtures.Profile("1765592", Fixtures.Row4Keys());
    newer.Version = WatchProfile.CurrentVersion + 1;
    t.WriteProfile("newer", newer);
    var network = new FakeNetwork();

    var none = new StringWriter();
    Assert.Equal(ProbeExitCodes.Configuration, await Runner(t, network, new FakeClock(morning), none).RunAsync());
    Assert.True(none.ToString().Contains("cannot read profile broken.cinemawatch.json"));
    Assert.True(none.ToString().Contains("cannot read profile newer.cinemawatch.json: The profile was saved by a newer version"));
    Assert.True(none.ToString().Contains("no readable"));

    t.WriteProfile("ok", Fixtures.Profile("1765592", Fixtures.Row4Keys()));
    Assert.Equal(ProbeExitCodes.Checked, await Runner(t, network, new FakeClock(morning), new StringWriter()).RunAsync());

    // A data "folder" that is a file cannot be written to.
    var file = Path.Combine(t.Root, "not-a-folder");
    File.WriteAllText(file, "");
    var noData = new StringWriter();
    var runner = new ProbeRunner(new ProbeOptions(t.Profiles, file, TimeSpan.Zero), network, new FakeClock(morning), noData, UserAgent);
    Assert.Equal(ProbeExitCodes.Configuration, await runner.RunAsync());
    Assert.True(noData.ToString().Contains("cannot write to the data folder"));
});

TestRunner.Add("Run: HTTP 500 everywhere -> exit 2 (every screening failed), not treated as a block", async () =>
{
    using var t = new TempFolder();
    t.WriteProfile("A", Fixtures.Profile("1765592", Fixtures.Row4Keys()));
    var network = new FakeNetwork { Override = _ => FakeNetwork.Respond(500, "oops", "text/plain") };
    var output = new StringWriter();

    Assert.Equal(ProbeExitCodes.AllFailed, await Runner(t, network, new FakeClock(morning), output, repertoireMinutes: 60).RunAsync());
    var text = output.ToString();
    Assert.True(text.Contains("ERROR Cinema City returned HTTP error 500.") && !text.Contains("BLOCKED"));
    Assert.Equal(1, network.Count("/film-events/")); // not a block, so the repertoire is still tried
});

return await TestRunner.RunAsync();
