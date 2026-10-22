using CinemaWatch.Core;
using CinemaWatch.ViewModels;

namespace CinemaWatch.Tests;

/// <summary>End-to-end tests of the wizard view models (no window needed).</summary>
internal static class ViewModelTests
{
    private static readonly DateTime Now = new(2026, 10, 21, 18, 0, 0);
    private static readonly AppEnvironment Environment = new(
        @"C:\DEV\CinemaWatch\src\CinemaWatch.App\bin\Debug\net10.0-windows10.0.19041.0\CinemaWatch.exe",
        Path.GetTempPath());

    public static void Register()
    {
        TestRunner.Add("Wizard: validation, partial repertoire failure, screenings, occupancy", async () =>
        {
            // FixtureHttpHandler knows only 16.12 and screening 1765592 -> 15.12 and 19:15 return 404.
            using var client = new CinemaCityClient(new FixtureHttpHandler());
            var vm = new MainViewModel(client, new FakeUi(), new FakeScheduler(), Environment, () => Now);

            vm.DatesStep.DateFrom = new DateTime(2026, 10, 1);
            await vm.NextCommand.ExecuteAsync();
            Assert.Equal("Data nie może być z przeszłości.", vm.ErrorText);

            vm.DatesStep.DateFrom = new DateTime(2026, 12, 15);
            vm.DatesStep.IsRange = true;
            vm.DatesStep.DateTo = new DateTime(2027, 1, 30);
            await vm.NextCommand.ExecuteAsync();
            Assert.Equal("Zakres może obejmować maksymalnie 31 dni.", vm.ErrorText);

            vm.DatesStep.DateTo = new DateTime(2026, 12, 16);
            await vm.NextCommand.ExecuteAsync();
            Assert.Equal(1, vm.StepIndex);
            Assert.True(vm.StatusText!.Contains("15.12"));

            var screenings = vm.ScreeningsStep;
            Assert.Equal("Diuna: Część trzecia (4 seanse)", screenings.SelectedFilm!.DisplayName);
            Assert.SequenceEqual(["12:45", "16:00", "19:15", "22:30"], screenings.Days.Single().Screenings.Select(o => o.TimeText));
            Assert.Equal("Wyprzedany", screenings.Days[0].Screenings[2].AvailabilityText);

            await vm.NextCommand.ExecuteAsync();
            Assert.Equal("Zaznacz co najmniej jeden seans.", vm.ErrorText);

            screenings.Days[0].Screenings[1].IsChecked = true; // 16:00
            screenings.Days[0].Screenings[2].IsChecked = true; // 19:15 (no fixture -> error)
            await vm.NextCommand.ExecuteAsync();
            Assert.Equal(2, vm.StepIndex);
            Assert.Equal("Wolne: 198 z 383", vm.SeatsStep.Items[0].StatusText);
            Assert.True(vm.SeatsStep.Items[1].IsError);

            vm.BackCommand.Execute(null);
            Assert.Equal(2, screenings.SelectedCount);
        });

        TestRunner.Add("Hall map: geometry, row and seat toggles, wheelchair, copy to other screenings", async () =>
        {
            using var client = new CinemaCityClient(new AnyScreeningHandler());
            var vm = await OpenSeatsStepAsync(client, new FakeUi(), new FakeScheduler());
            var seats = vm.SeatsStep;
            var map = seats.Items[0].Map!;

            Assert.Equal(385, map.SeatViews.Count);
            Assert.Equal(24, map.RowLabels.Count);
            Assert.True(map.SeatViews.All(s => s.Left >= 0 && s.Top > map.ScreenTop && s.Left + s.Size <= map.Width && s.Top + s.Size <= map.Height));
            var row8 = map.SeatViews.Where(s => s.Seat.Row == "8").OrderBy(s => s.Left).ToList();
            Assert.Equal("37", row8.First().Seat.Label); // numbered right to left, like the website
            Assert.Equal("\u267F", map.SeatViews.Single(s => s.Key == "1_4_12").Text);

            var label8 = map.RowLabels.First(l => l.Label == "8");
            label8.ToggleCommand.Execute(null);
            Assert.Equal(37, map.WatchedCount);
            Assert.True(label8.IsFull);
            row8[3].IsWatched = false;
            Assert.True(label8.IsPartial);
            label8.ToggleCommand.Execute(null);
            Assert.True(label8.IsFull);

            map.RowLabels.First(l => l.Label == "12").ToggleCommand.Execute(null);
            Assert.Equal(37 + 35, map.WatchedCount); // wheelchair spaces are not part of "whole row"
            map.SeatViews.Single(s => s.Seat.Label == "1N").IsWatched = true;
            map.SeatViews.First(s => s.Seat.Row == "6" && !s.IsFree).IsWatched = true; // taken seats can be watched
            Assert.Equal(74, map.WatchedCount);

            seats.MinAdjacent = 3;
            Assert.True(seats.Items[0].SelectionText.Contains("min. 3 obok siebie"));

            seats.ApplyToAllCommand.Execute(null);
            Assert.Equal(74, seats.Items[1].WatchedCount);
            Assert.Equal("Skopiowano zaznaczenie do 1 seansu.", seats.ActionText);

            await vm.NextCommand.ExecuteAsync();
            var rows = vm.SummaryStep.Items[0].Rows;
            Assert.Equal("cały rząd", rows.Single(r => r.Row == "Rząd 8").Text);
            Assert.Equal("cały rząd + 1N (dla osób na wózku)", rows.Single(r => r.Row == "Rząd 12").Text);

            vm.BackCommand.Execute(null);
            vm.BackCommand.Execute(null);
            await vm.NextCommand.ExecuteAsync(); // reloads occupancy, keeps the selection
            Assert.Equal(74, vm.SeatsStep.Items[0].WatchedCount);
            Assert.Equal(74, vm.SeatsStep.Items[1].WatchedCount);

            foreach (var item in vm.SeatsStep.Items) item.Map!.Clear();
            await vm.NextCommand.ExecuteAsync();
            Assert.Equal("Zaznacz co najmniej jeden rząd albo jedno miejsce.", vm.ErrorText);
        });

        TestRunner.Add("Profiles: check now, save on copy, schedule with confirmation, reopen", async () =>
        {
            using var client = new CinemaCityClient(new AnyScreeningHandler());
            var ui = new FakeUi();
            var scheduler = new FakeScheduler();
            var vm = await OpenSeatsStepAsync(client, ui, scheduler);
            vm.SeatsStep.Items[0].Map!.RowLabels.First(l => l.Label == "4").ToggleCommand.Execute(null);
            vm.SeatsStep.Items[1].Map!.SeatViews.Single(s => s.Seat.Row == "9" && s.Seat.Label == "20").IsWatched = true;
            vm.SeatsStep.MinAdjacent = 2;
            await vm.NextCommand.ExecuteAsync();

            var summary = vm.SummaryStep;
            Assert.Equal("Diuna: Część trzecia 16.12", summary.ProfileName);
            Assert.True(!summary.UnscheduleCommand.CanExecute(null));

            await summary.CheckNowCommand.ExecuteAsync();
            Assert.True(summary.Items[0].IsMatch);
            Assert.Equal("Wolne teraz: rząd 4: 1–15, 18–31", summary.Items[0].ResultText);
            Assert.True(!summary.Items[1].IsMatch); // a single seat cannot satisfy "2 next to each other"
            summary.Items[0].BuyCommand.Execute(null);
            Assert.Equal("https://tickets.cinema-city.pl/api/order/1765592?lang=pl", ui.Opened);

            var directory = Path.Combine(Path.GetTempPath(), $"cw-vm-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            try
            {
                var path = Path.Combine(directory, "Diuna IMAX" + ProfileStore.FileExtension);
                ui.SavePath = path;
                summary.ProfileName = "Diuna IMAX";
                await summary.CopyCommandLineCommand.ExecuteAsync();
                Assert.Equal("Diuna IMAX.cinemawatch.json", ui.LastSaveSuggestion);
                Assert.Equal($"\"{Environment.ExePath}\" --profile \"{path}\"", ui.Copied);

                var saved = ProfileStore.Load(path);
                Assert.Equal(2, saved.MinAdjacent);
                Assert.Equal(31, saved.Screenings[0].SeatKeys.Count);
                Assert.Equal("1_20_9", saved.Screenings[1].SeatKeys.Single());

                summary.IntervalMinutes = 15;
                await summary.ScheduleCommand.ExecuteAsync();
                Assert.True(ui.Confirms.Single().Contains("co 15 min") && ui.Confirms[0].Contains("folderu kompilacji"));
                Assert.Equal(15, scheduler.Tasks[SchtasksScheduler.TaskNameFor(path)]);
                Assert.True(summary.IsScheduled);
                Assert.Equal(15, ProfileStore.Load(path).IntervalMinutes);

                ui.ConfirmAnswer = false;
                await summary.UnscheduleCommand.ExecuteAsync();
                Assert.True(summary.IsScheduled);
                ui.ConfirmAnswer = true;
                await summary.UnscheduleCommand.ExecuteAsync();
                Assert.True(!summary.IsScheduled);

                vm.NewProfileCommand.Execute(null);
                Assert.Equal(0, vm.StepIndex);
                Assert.Equal(null, summary.ProfilePath);

                ui.OpenPath = path;
                await vm.OpenProfileCommand.ExecuteAsync();
                Assert.Equal(3, vm.StepIndex);
                Assert.Equal("Otwarto profil: Diuna IMAX", vm.StatusText);
                Assert.Equal(path, summary.ProfilePath);
                Assert.Equal(15, summary.IntervalMinutes);
                Assert.Equal(2, vm.SeatsStep.MinAdjacent);
                Assert.Equal(31, vm.SeatsStep.Items[0].WatchedCount);
                Assert.Equal(1, vm.SeatsStep.Items[1].WatchedCount);

                var later = new MainViewModel(client, ui, scheduler, Environment, () => new DateTime(2026, 12, 17, 10, 0, 0));
                await later.OpenProfileCommand.ExecuteAsync();
                Assert.Equal("Wszystkie seanse z tego profilu już się odbyły.", later.ErrorText);

                // Expired profile that is still scheduled: the app offers to remove the task.
                await scheduler.RegisterAsync(SchtasksScheduler.TaskNameFor(path), "CinemaWatch.exe", path, 10);
                ui.Confirms.Clear();
                await later.OpenProfileCommand.ExecuteAsync();
                Assert.True(ui.Confirms.Single().StartsWith("Profil nieaktualny"));
                Assert.True(!scheduler.Tasks.ContainsKey(SchtasksScheduler.TaskNameFor(path)));
                Assert.True(later.StatusText!.Contains("Profil był nieaktualny"));

                var bad = Path.Combine(directory, "bad" + ProfileStore.FileExtension);
                File.WriteAllText(bad, "{ nope");
                ui.OpenPath = bad;
                await vm.OpenProfileCommand.ExecuteAsync();
                Assert.True(vm.ErrorText!.Contains("nie jest poprawnym profilem"));
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        });
    }

    public static void RegisterNavigationTests()
    {
        TestRunner.Add("Step navigation: clickable steps, instant revisits, reload after changes, stop at missing input", async () =>
        {
            var http = new AnyScreeningHandler();
            using var client = new CinemaCityClient(http);
            var vm = new MainViewModel(client, new FakeUi(), new FakeScheduler(), Environment, () => Now);
            var steps = vm.Steps;

            // Fresh wizard: only the next step can be opened.
            Assert.SequenceEqual([true, true, false, false], steps.Select(s => s.IsReachable));
            Assert.True(!steps[3].NavigateCommand.CanExecute(null));
            Assert.Equal("Najpierw przejdź poprzednie kroki", steps[3].ToolTip);

            vm.DatesStep.DateFrom = new DateTime(2026, 12, 16);
            await steps[1].NavigateCommand.ExecuteAsync();
            vm.ScreeningsStep.Days[0].Screenings.Single(o => o.TimeText == "16:00").IsChecked = true;
            vm.ScreeningsStep.Days[0].Screenings.Single(o => o.TimeText == "22:30").IsChecked = true;
            await steps[2].NavigateCommand.ExecuteAsync();
            vm.SeatsStep.Items[0].Map!.RowLabels.First(l => l.Label == "8").ToggleCommand.Execute(null);
            await steps[3].NavigateCommand.ExecuteAsync();
            Assert.Equal(3, vm.StepIndex);
            Assert.SequenceEqual([true, true, true, false], steps.Select(s => s.IsDone));

            // Back to step 1 and straight to the summary again: nothing is reloaded.
            var requests = http.RequestCount;
            await steps[0].NavigateCommand.ExecuteAsync();
            Assert.Equal(0, vm.StepIndex);
            Assert.SequenceEqual([true, true, true, true], steps.Select(s => s.IsReachable));
            await steps[3].NavigateCommand.ExecuteAsync();
            Assert.Equal(3, vm.StepIndex);
            Assert.Equal(requests, http.RequestCount);
            Assert.Equal(37, vm.SeatsStep.Items[0].WatchedCount);

            // Different screenings: only the seat data is reloaded (2 requests: screening details + free seats).
            await steps[1].NavigateCommand.ExecuteAsync();
            vm.ScreeningsStep.Days[0].Screenings.Single(o => o.TimeText == "22:30").IsChecked = false;
            await steps[3].NavigateCommand.ExecuteAsync();
            Assert.Equal(3, vm.StepIndex);
            Assert.Equal(requests + 2, http.RequestCount);
            Assert.Equal(1, vm.SeatsStep.Items.Count);
            Assert.Equal(37, vm.SeatsStep.Items[0].WatchedCount); // seat selection kept

            // Wider date range: the repertoire reloads, ticked screenings stay ticked, the chain runs to the summary.
            await steps[0].NavigateCommand.ExecuteAsync();
            vm.DatesStep.DateFrom = new DateTime(2026, 12, 15);
            vm.DatesStep.IsRange = true;
            vm.DatesStep.DateTo = new DateTime(2026, 12, 16);
            await steps[3].NavigateCommand.ExecuteAsync();
            Assert.Equal(3, vm.StepIndex);
            Assert.Equal(1, vm.ScreeningsStep.SelectedCount);
            Assert.Equal(37, vm.SeatsStep.Items[0].WatchedCount);

            // Missing input stops the chain at that step.
            await steps[1].NavigateCommand.ExecuteAsync();
            vm.ScreeningsStep.ClearAllCommand.Execute(null);
            await steps[3].NavigateCommand.ExecuteAsync();
            Assert.Equal(1, vm.StepIndex);
            Assert.Equal("Zaznacz co najmniej jeden seans.", vm.ErrorText);

            // A new profile resets what can be clicked.
            vm.NewProfileCommand.Execute(null);
            Assert.SequenceEqual([true, true, false, false], steps.Select(s => s.IsReachable));
            vm.Dispose();
        });
    }

    public static void RegisterLanguageTests()
    {
        TestRunner.Add("Language switch: every visible text follows without a restart; cards start collapsed", async () =>
        {
            using var client = new CinemaCityClient(new AnyScreeningHandler());
            var refreshes = 0;
            var uiChanges = 0;
            var vm = await OpenSeatsStepAsync(client, new FakeUi(), new FakeScheduler(), () => refreshes++);
            try
            {
                Assert.Equal($"CinemaWatch {AppInfo.Version}", vm.WindowTitle);
                vm.SeatsStep.Items[0].Map!.RowLabels.First(l => l.Label == "8").ToggleCommand.Execute(null);
                await vm.NextCommand.ExecuteAsync();
                var card = vm.SummaryStep.Items[0];
                Assert.True(!card.IsExpanded);
                card.ToggleExpandedCommand.Execute(null);
                Assert.True(card.IsExpanded);
                Assert.Equal("Rząd 8", card.Rows.Single().Row);

                UiText.Instance.PropertyChanged += OnUiChanged;
                vm.SelectedLanguage = vm.Languages.Single(l => l.Language == AppLanguage.English);
                UiText.Instance.PropertyChanged -= OnUiChanged;

                Assert.Equal(1, refreshes);
                Assert.True(uiChanges >= 1);
                Assert.Equal("Next", UiText.Instance.Next);
                Assert.Equal("English", vm.SelectedLanguage.Name);
                Assert.Equal("Cinema and dates", vm.Steps[0].Title);
                Assert.Equal("Diuna: Część trzecia (4 screenings)", vm.ScreeningsStep.SelectedFilm!.DisplayName);
                Assert.Equal("Wednesday, 16 December 2026", vm.ScreeningsStep.Days[0].Header);
                Assert.Equal("EN · PL subtitles", vm.ScreeningsStep.Days[0].Screenings[0].DetailText);
                Assert.Equal("Free: 198 of 383", vm.SeatsStep.Items[0].StatusText);
                Assert.True(vm.SeatsStep.Items[0].SelectionText.StartsWith("Watching 37 seats · free now:"));
                Assert.Equal("Row 8", card.Rows.Single().Row);
                Assert.Equal("whole row", card.Rows.Single().Text);
                Assert.Equal("Wednesday, 16 December 2026, 16:00", card.Title);
                Assert.Equal("Notification: when any of the selected seats becomes free.", vm.SummaryStep.ConditionText);
                Assert.Equal("Task Scheduler: save the profile first.", vm.SummaryStep.ScheduleStatusText);

                // Messages already shown re-render in the new language.
                vm.BackCommand.Execute(null);
                vm.SeatsStep.Items[0].Map!.Clear();
                await vm.NextCommand.ExecuteAsync();
                Assert.Equal("Select at least one row or seat.", vm.ErrorText);
                vm.SelectedLanguage = vm.Languages.Single(l => l.Language == AppLanguage.Polish);
                Assert.Equal("Zaznacz co najmniej jeden rząd albo jedno miejsce.", vm.ErrorText);
                Assert.Equal(2, refreshes);
            }
            finally
            {
                Localizer.SetLanguage(AppLanguage.Polish);
                vm.Dispose();
            }

            void OnUiChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => uiChanges++;
        });
    }

    /// <summary>Wizard on 16.12 with the 16:00 and 22:30 screenings selected, ending on the hall maps.</summary>    /// <summary>Wizard on 16.12 with the 16:00 and 22:30 screenings selected, ending on the hall maps.</summary>
    private static async Task<MainViewModel> OpenSeatsStepAsync(CinemaCityClient client, FakeUi ui, FakeScheduler scheduler, Action? languageChanged = null)
    {
        var vm = new MainViewModel(client, ui, scheduler, Environment, () => Now, languageChanged);
        vm.DatesStep.DateFrom = new DateTime(2026, 12, 16);
        await vm.NextCommand.ExecuteAsync();
        vm.ScreeningsStep.Days[0].Screenings.Single(o => o.TimeText == "16:00").IsChecked = true;
        vm.ScreeningsStep.Days[0].Screenings.Single(o => o.TimeText == "22:30").IsChecked = true;
        await vm.NextCommand.ExecuteAsync();
        Assert.Equal(2, vm.StepIndex);
        return vm;
    }
}
