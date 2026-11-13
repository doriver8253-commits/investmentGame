using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BlackWhale.Flow;
using BlackWhale.Rules;
using BlackWhale.Rules.Information;
using BlackWhale.Shared;
using BlackWhale.UI.State;
using BlackWhale.UI.Presentation;

namespace BlackWhale.Tests
{
    public static class StockPatternValidation
    {
        private static void Check(bool value, string message) => RoomValidation.Check(value, "STOCK-v3: " + message);
        private sealed class Harness
        {
            public readonly TestClock Clock = new TestClock();
            public readonly MarketService Market = MarketService.CreateRandom(20261108);
            public readonly SharedInformationService Info;
            public readonly GameRoom Room;
            private int counter;
            public Harness(bool features = false, double seconds = 90)
            {
                Info = new SharedInformationService(Market);
                Room = new GameRoom("PATTERNS", Market, Info, Clock, fixedOrderForFixture: true,
                    settings: new GameSettings(infoSeconds: seconds, turns: 1, enableProgressionFeatures: features));
                for (int i = 1; i <= 4; i++)
                { Room.Join("c" + i, "Player " + i); Send(i, "SET_READY", new CommandPayload { ready = true }, 0); }
                Send(1, "START_GAME", turn: 0);
                if (features) Send(1, "COMPLETE_PHASE", turn: 0);
            }
            public CommandResult Send(int p, string type, CommandPayload payload = null, int turn = 1)
                => Room.ProcessCommand("c" + p, new Command { type = type, turn = turn, requestId = "test-" + (++counter), payload = payload });
            public void Public()
            {
                string[] companies = { "BANK", "AI", "SPACE", "FOOD" };
                for (int p = 1; p <= 4; p++) Check(Send(p, "REVEAL_INFORMATION_WINDOW", new CommandPayload { companyId = companies[p - 1], startDay = 1 }).success, "valid public pick");
            }
            public PlayerView View(int p) => Info.BuildPlayerView(new RequestContext("PATTERNS", 1, Protocol.Info, "P" + p), Market.GetOwnInvestmentState("P" + p));
            public CommandResult Buy(int p, string company, int start, int end = -1) => Send(p, "BUY_INFORMATION_WINDOW", new CommandPayload { companyId = company, startDay = start, endDay = end == -1 ? start + 4 : end });
            public void Feature(int p, string field, object value)
            {
                // Test setup for earned inventory/credits; no production-only testing endpoints.
                var all = (IDictionary)typeof(SharedInformationService).GetField("features", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(Info);
                object room = all["PATTERNS"];
                var players = (IDictionary)room.GetType().GetField("Players").GetValue(room);
                object player = players["P" + p];
                if (field == "Inventory") ((List<string>)player.GetType().GetField(field).GetValue(player)).Add((string)value);
                else player.GetType().GetField(field).SetValue(player, value);
            }
        }
        public static string[] Run()
        {
            var passed = new List<string>();
            Action<string, Action> run = (name, action) => { action(); passed.Add("STOCK-v3/" + name); };
            run("pattern-reproducibility-limits-and-legal-trade-survey", Patterns);
            run("four-public-picks-order-overlap-private-gates-and-replay", Public);
            run("timeout-reconnect-pause-and-short-info-budget", Timeouts);
            run("window-boundaries-union-cost-replay-and-private-copy", Purchases);
            run("free-credit-no-op-and-item-proof-ledger", Items);
            run("daily-price-flexible-length-free-days-and-grants", DailyEconomy);
            run("entry-open-exit-close-and-hidden-previous-day", OpenEntry);
            run("mouse-move-resize-and-invalid-edit-isolation", RangeEditing);
            run("pattern-lab-reuses-generator-and-visible-axis-fit", PatternLabSamples);
            run("unlimited-reservations-auto-order-reconnect-and-public-trade-returns", Reservations);
            return passed.ToArray();
        }
        private static void Reservations()
        {
            var h = new Harness(); h.Public(); h.Room.DrainMessages();
            var singles = new[] { 1, 9, 17 }.Select(d => new Interval { entryDay = d, exitDay = d + 7 }).ToArray();
            Check(OrderInputValidator.ValidateIntervals(singles, true) == "", "seven-day trades and more than two intervals are valid");
            Check(OrderInputValidator.ValidateIntervals(new[] { new Interval { entryDay = 5, exitDay = 5 } }, true) == ErrorCodes.InvalidHoldingDays, "single-day trades are no longer valid");
            Check(OrderInputValidator.ValidateIntervals(new[] { new Interval { entryDay = 0, exitDay = 30 } }, true) == "", "whole turn holding is valid");
            Check(OrderInputValidator.ValidateIntervals(new[] { new Interval { entryDay = 0, exitDay = 0 } }, true) == ErrorCodes.InvalidDay, "zero-day baseline is not a trade");
            var plan = new[] {
                new Investment { companyId = "AI", weightBps = 6000, intervals = singles },
                new Investment { companyId = "BANK", weightBps = 4000, intervals = new[] { new Interval { entryDay = 0, exitDay = 30 } } },
                new Investment { companyId = "BIO", intervals = new[] { new Interval { entryDay = 12, exitDay = 20 } } }
            };
            var command = new Command { type = "SAVE_TRADE_PLAN", turn = 1, requestId = "reservation", payload = new CommandPayload { investments = plan } };
            Check(h.Room.ProcessCommand("c1", command).success && h.Room.ProcessCommand("c1", command).success, "reservation save and replay");
            plan[0].intervals[0].exitDay = 29;
            Check(h.Market.GetReservations("P1")[0].intervals[0].exitDay == 8, "server reservation copy is isolated");
            Check(!h.Market.IsOrderLocked("P1") && h.Market.GetReservations("P2").Length == 0, "saving neither locks nor shares a plan");
            var invalid = h.Market.GetReservations("P1"); invalid[0].intervals[0].exitDay = 10;
            Check(h.Send(1, "SAVE_TRADE_PLAN", new CommandPayload { investments = invalid }).errorCode == ErrorCodes.OverlappingIntervals,
                "overlap is rejected atomically");
            h.Send(1, "GET_PLAYER_VIEW"); h.Send(2, "GET_PLAYER_VIEW");
            var privateViews = h.Room.DrainMessages();
            Check(privateViews.Single(d => d.PlayerId == "P1" && d.Message.type == "PLAYER_VIEW").Message.playerView.reservedInvestments.Length == 3 &&
                privateViews.Single(d => d.PlayerId == "P2" && d.Message.type == "PLAYER_VIEW").Message.playerView.reservedInvestments.Length == 0, "no cross-player reservation leak");
            string[] choices = { "AI", "BANK", "SPACE", "FOOD", "BIO", "AUTO", "FOOD", "BANK" };
            int[] pickers = { 1, 2, 3, 4, 4, 3, 2, 1 };
            var remote = new Harness(); remote.Public();
            string token = remote.Room.DrainMessages().First(d => d.PlayerId == "P2" && d.Message.type == "WELCOME").Message.session.reconnectToken;
            Check(remote.Send(2, "SAVE_TRADE_PLAN", new CommandPayload { investments = h.Market.GetReservations("P1") }).success, "remote reservation");
            remote.Room.DrainMessages(); remote.Room.Disconnect("c2");
            Check(remote.Room.Join("c2-return", "ignored", token) == "", "remote reconnect succeeds");
            Check(remote.Room.DrainMessages().Single(d => d.PlayerId == "P2" && d.Message.type == "PLAYER_VIEW").Message.playerView.reservedInvestments[0].intervals.Length == 3, "reconnect restores own reservations");
            // A fresh connected room exercises the complete timeout -> reveal -> settlement path.
            var live = new Harness(); live.Public();
            Check(live.Send(1, "SAVE_TRADE_PLAN", new CommandPayload { investments = h.Market.GetReservations("P1") }).success, "save live plan");
            for (int p = 1; p <= 4; p++) Check(live.Send(p, "COMPLETE_PHASE").success, "finish info");
            for (int n = 0; n < choices.Length; n++) Check(live.Send(pickers[n], "SELECT_COMPANY", new CommandPayload { companyId = choices[n] }).success, "company pick");
            bool guarded = false;
            try { live.Market.GetRevealedMarket(new RequestContext("PATTERNS", 1, Protocol.Info)); } catch (InvalidOperationException) { guarded = true; }
            Check(guarded, "other players' orders and market remain unavailable before lock");
            live.Room.DrainMessages();
            live.Clock.Seconds = live.Room.Snapshot().phaseEndsAtServerSeconds; live.Room.Tick();
            Check(live.Room.Snapshot().phase == Protocol.Reveal, "deadline reveals without clicking submit");
            var own = live.Market.GetOwnInvestmentState("P1").Investments;
            Check(own[0].companyId == "AI" && own[0].weightBps == 6000 && own[0].intervals.Length == 3 && own[1].intervals[0].exitDay == 30,
                "automatic order preserves chosen firms, ratio and every reserved interval");
            Check(own.All(i => i.companyId != "BIO"), "unselected company's reservation is not traded");
            var deliveries = live.Room.DrainMessages();
            Check(deliveries.Where(d => d.Message.type == "MARKET_REVEAL").Count() == 4, "all four receive public trades");
            var reveal = deliveries.First(d => d.Message.type == "MARKET_REVEAL").Message.revealedMarket;
            var ai = reveal.Single(c => c.companyId == "AI"); var p1 = ai.investments.Single(p => p.playerId == "P1");
            Check(p1.intervals.All(v => v.returnBps == ai.dailyChangesBps[v.exitDay] - ai.dailyChangesBps[Math.Max(0, v.entryDay - 1)]), "public interval return uses entry open and exit close");
            var copy = DtoCopy.Copy(ai); copy.investments[0].intervals[0].returnBps++;
            Check(copy.investments[0].intervals[0].returnBps != ai.investments[0].intervals[0].returnBps, "public trade DTO copy isolation");
            live.Clock.Seconds = live.Room.Snapshot().phaseEndsAtServerSeconds; live.Room.Tick();
            Check(live.Market.GetSettlementDetail(1).Single(p => p.playerId == "P1").companyProfits.Single(c => c.companyId == "AI").returnBps == p1.returnBps,
                "revealed company total exactly matches settlement");
            Check(live.Send(1, "SAVE_TRADE_PLAN", new CommandPayload { investments = own }).errorCode == ErrorCodes.WrongPhase, "cannot change reservations after reveal");
        }
        private static void DailyEconomy()
        {
            var h = new Harness(); h.Public();
            Check(h.Buy(1, "BIO", 26).success && h.View(1).coins == 10, "five new days cost five coins");
            Check(h.Buy(1, "BIO", 1).success && h.Buy(1, "BIO", 13).success && h.View(1).coins == 0, "three separated five-day windows use all fifteen coins");
            Check(h.Buy(1, "AUTO", 10).errorCode == "NOT_ENOUGH_COINS" && h.View(1).coins == 0, "unaffordable window is atomic");
            Check(h.Buy(1, "BIO", 7, 6).errorCode == "INVALID_WINDOW" && h.Buy(1, "BIO", 27).errorCode == "INVALID_WINDOW", "range boundaries");
            var grant = new Harness(); string[] players = { "P1", "P2", "P3", "P4" };
            for (int turn = 1; turn <= 10; turn++)
            {
                var c = new RequestContext("PATTERNS", turn, Protocol.Info);
                grant.Market.BeginTurn(c, players); grant.Info.GrantTurnCoins(c, players); grant.Info.GrantTurnCoins(c, players);
                Check(grant.Info.GetCoins("PATTERNS", "P1") == (turn <= 5 ? turn * 15 : 75 + (turn - 5) * 25), "15/25 idempotent daily grants");
            }
            var free = new Harness(features: true); free.Public(); free.Feature(1, "FreeCredits", 2);
            Check(free.Buy(1, "BIO", 1, 5).success && free.View(1).coins == 12, "free days cover only two of five new days");
        }
        private static void OpenEntry()
        {
            var h = new Harness(); h.Public(); Check(h.Buy(1, "BIO", 20).success, "buy one five-day window");
            var company = h.View(1).companies.Single(c => c.companyId == "BIO");
            var days = h.Market.GetWindow(1, "BIO", 19, 7);
            Check(!InformationWindowRules.KnownDays(company)[19].HasValue && InformationWindowRules.KnownOpens(company)[20] == days[0], "only bought day exposes its open");
            var data = PatternMarketGenerator.Generate(20261108, 1); var rules = new InvestmentRules { entryAtOpen = true };
            var investment = new Investment { companyId = "BIO", intervals = new[] { new Interval { entryDay = 20, exitDay = 25 } } };
            Check(Settlement.CompanyReturnBps(new MarketBook(data, rules), investment, rules) == days[6] - days[0], "exit close minus entry open");
            Check(h.View(1).entryAtOpen && h.View(2).companies.Single(c => c.companyId == "BIO").visibleWindows.Length == 0, "private opens do not leak to other players");
            Check(OrderInputValidator.ValidateIntervals(new[] { new Interval { entryDay = 3, exitDay = 10 } }, true) == "", "exit day minus entry day of seven is allowed");
            Check(OrderInputValidator.ValidateIntervals(new[] { new Interval { entryDay = 3, exitDay = 9 } }, true) == ErrorCodes.InvalidHoldingDays, "six days apart is too short");
            var touching = new[] { new Interval { entryDay = 1, exitDay = 8 }, new Interval { entryDay = 8, exitDay = 15 } };
            Check(OrderInputValidator.ValidateIntervals(touching, true) == ErrorCodes.OverlappingIntervals, "same-day exit close and re-entry open overlap");
        }
        private static void RangeEditing()
        {
            var book = new TradePlanBook(); book.BeginTurn(1); book.TryAddInterval("AI", 3, 9);
            var range = new ChartRangeSelection(); range.Begin(6, book.Get("AI").Intervals); range.Finish(8);
            Check(range.Start == 5 && range.End == 11 && book.TryReplaceInterval("AI", 0, range.Start, range.End) == "", "dragging body moves both ends");
            range.Begin(11, book.Get("AI").Intervals); range.Finish(13);
            Check(book.TryReplaceInterval("AI", 0, range.Start, range.End) == "" && book.Get("AI").Intervals[0].exitDay == 13, "dragging endpoint resizes");
            range.Begin(13, book.Get("AI").Intervals); range.Finish(30);
            Check(book.TryReplaceInterval("AI", 0, range.Start, range.End) != "" && book.Get("AI").Intervals[0].exitDay == 13, "invalid edit cannot mutate reservation");
        }
        private static void PatternLabSamples()
        {
            foreach (int seed in new[] { 0, 42, -9, 20261108 })
                foreach (int turn in new[] { 1, 5, 10 })
                {
                    var market = PatternMarketGenerator.Generate(seed, turn);
                    foreach (var company in market.companies)
                    {
                        var normal = PatternMarketGenerator.Inspect(seed, turn, company.companyId);
                        Check(normal.DailyChangesBps.SequenceEqual(company.dailyChangesBps), "lab normal mode matches the live generator exactly");
                        foreach (StockPatternKind pattern in Enum.GetValues(typeof(StockPatternKind)))
                            foreach (StockPatternOutcome outcome in Enum.GetValues(typeof(StockPatternOutcome)))
                            {
                                var sample = PatternMarketGenerator.Inspect(seed, turn, company.companyId, pattern, outcome);
                                Check(sample.Pattern == pattern && sample.Outcome == outcome && sample.StartDay >= 1 && sample.EndDay <= 29, "forced diagnostic metadata");
                                int limit = RandomMarketGenerator.DailyLimitBps(company.companyId, turn), cap = RandomMarketGenerator.AbsoluteCapBps[company.companyId];
                                Check(Enumerable.Range(1, 30).All(d => Math.Abs(sample.DailyChangesBps[d]) <= cap && Math.Abs(sample.DailyChangesBps[d] - sample.DailyChangesBps[d - 1]) <= limit), "lab constraints match normal play");
                            }
                    }
                }
            var flat = ChartVerticalRange.FromVisibleValues(new[] { 0, 0 });
            Check(flat.Span > 0 && flat.Fraction(0) == .5, "flat/unopened chart has a valid fallback");
            var fit = ChartVerticalRange.FromVisibleValues(new[] { 0, 30, -20 });
            Check(fit.MinBps < -20 && fit.MaxBps > 30 && fit.Span < 100, "small movements fill the chart with padding");
            var positive = ChartVerticalRange.FromVisibleValues(new[] { 0, 1000 });
            Check(positive.Fraction(0) < .2 && positive.Fraction(1000) > .8, "unused negative half no longer flattens a positive pattern");
        }
        private static void Patterns()
        {
            var generator = new PatternMarketGenerator(42);
            int profitable = 0, lossOnly = 0;
            for (int seed = 0; seed < 200; seed++)
                for (int turn = 1; turn <= 10; turn += 3)
                {
                    var data = PatternMarketGenerator.Generate(seed, turn);
                    new MarketBook(data, new InvestmentRules());
                    var repeat = PatternMarketGenerator.Generate(seed, turn);
                    foreach (var c in data.companies)
                    {
                        Check(c.publicBundleIndices.Length == 0 && c.dailyChangesBps[0] == 0, "no preselected public dates");
                        Check(c.dailyChangesBps.SequenceEqual(repeat.companies.Single(r => r.companyId == c.companyId).dailyChangesBps), "deterministic path");
                        int limit = RandomMarketGenerator.DailyLimitBps(c.companyId, turn), cap = RandomMarketGenerator.AbsoluteCapBps[c.companyId];
                        Check(Enumerable.Range(1, 30).All(d => Math.Abs(c.dailyChangesBps[d]) <= cap && Math.Abs(c.dailyChangesBps[d] - c.dailyChangesBps[d - 1]) <= limit), "daily and absolute constraints");
                        int best = int.MinValue;
                        for (int a = 0; a <= 25; a++) for (int b = a + 5; b <= Math.Min(a + 10, 30); b++)
                            best = Math.Max(best, c.dailyChangesBps[b] - c.dailyChangesBps[a]);
                        if (best > 0) profitable++; else lossOnly++;
                    }
                }
            Check(profitable > 0 && lossOnly > 0, "legal opportunities exist without guaranteeing profits for every company");
            var copy = generator.ForTurn(3); copy.companies[0].dailyChangesBps[10] = int.MaxValue;
            Check(generator.ForTurn(3).companies[0].dailyChangesBps[10] != int.MaxValue, "cache isolated");
            Check(!generator.ForTurn(3).companies[0].dailyChangesBps.SequenceEqual(generator.ForTurn(4).companies[0].dailyChangesBps), "fresh turn");
        }
        private static void Public()
        {
            var h = new Harness(); var first = h.Room.Snapshot();
            Check(first.publicSelectionActive && first.publicSelectionPlayerId == "P1" && first.phaseEndsAtServerSeconds == 90 && first.publicSelectionDeadlineAtServerSeconds == 10, "INFO includes ten-second pick");
            Check(h.View(1).companies.All(c => InformationWindowRules.KnownDays(c).Count(v => v.HasValue) == 1), "only day zero initially");
            Check(h.Send(1, "COMPLETE_PHASE").errorCode == "PUBLIC_SELECTION_PENDING" && !h.Buy(1, "AI", 1).success, "cannot skip public picks");
            Check(h.Send(2, "REVEAL_INFORMATION_WINDOW", new CommandPayload { companyId = "AI", startDay = 1 }).errorCode == "NOT_YOUR_TURN", "seat order");
            var request = new Command { type = "REVEAL_INFORMATION_WINDOW", requestId = "public-replay", turn = 1, payload = new CommandPayload { companyId = "AI", startDay = 3 } };
            Check(h.Room.ProcessCommand("c1", request).success && h.Room.ProcessCommand("c1", request).success && h.Room.Snapshot().publicSelectionIndex == 1, "replay publishes once");
            Check(h.Send(2, request.type, new CommandPayload { companyId = "AI", startDay = 7 }).errorCode == "PUBLIC_WINDOW_OVERLAP", "overlap rejected");
            Check(h.Send(2, request.type, new CommandPayload { companyId = "AI", startDay = 8 }).errorCode == "INFORMATION_WINDOW_TOO_CLOSE", "public windows of one company keep a four-day gap");
            Check(h.Send(2, request.type, new CommandPayload { companyId = "AI", startDay = 12 }).success, "same company allowed after the four-day gap");
            Check(h.Send(3, request.type, new CommandPayload { companyId = "AI", startDay = 21 }).success, "same company allowed after the four-day gap");
            Check(h.Send(4, request.type, new CommandPayload { companyId = "FOOD", startDay = 1 }).success, "other company allowed");
            Check(!h.Room.Snapshot().publicSelectionActive && h.Room.Snapshot().publicInformation.Length == 4, "four choices completed");
            for (int p = 1; p <= 4; p++) Check(h.View(p).companies.Sum(c => InformationWindowRules.KnownDays(c).Skip(1).Count(v => v.HasValue)) == 20 && h.Info.GetCoins("PATTERNS", "P" + p) == 15, "twenty identical free dates");
        }
        private static void Timeouts()
        {
            var h = new Harness(); var welcomes = h.Room.DrainMessages().Where(d => d.Message.type == "WELCOME").ToArray();
            h.Clock.Seconds = 9.999; h.Room.Tick();
            Check(h.Room.Snapshot().publicSelectionIndex == 0, "public turn stays available for ten seconds");
            h.Clock.Seconds = 10; h.Room.Tick();
            Check(h.Room.Snapshot().publicSelectionIndex == 1 && h.Room.Snapshot().publicInformation[0].automatic, "exact timeout auto pick");
            h.Room.Disconnect("c2"); h.Clock.Seconds = 30; h.Room.Tick();
            Check(h.Room.Snapshot().publicInformation.Length == 4 && h.Room.Snapshot().phaseEndsAtServerSeconds == 90, "timeout and disconnect preserve INFO budget");
            Check(h.Room.Join("return", "Player 2", welcomes.Single(d => d.PlayerId == "P2").Message.session.reconnectToken) == "", "reconnect allowed");
            Check(h.Room.DrainMessages().Any(d => d.PlayerId == "P2" && d.Message.playerView?.companies.Sum(c => InformationWindowRules.KnownDays(c).Skip(1).Count(v => v.HasValue)) == 20), "reconnect restores public mask");
            var full = new Harness(); full.Clock.Seconds = 39.999; full.Room.Tick();
            Check(full.Room.Snapshot().publicSelectionIndex == 3 && full.Room.Snapshot().publicSelectionActive, "four ten-second public choices fit inside INFO");
            full.Clock.Seconds = 40; full.Room.Tick();
            Check(!full.Room.Snapshot().publicSelectionActive && full.Room.Snapshot().phaseEndsAtServerSeconds == 90, "fifty seconds remain for personal exploration");
            var shortRoom = new Harness(seconds: 5); shortRoom.Clock.Seconds = 4; shortRoom.Room.Tick();
            Check(!shortRoom.Room.Snapshot().publicSelectionActive && shortRoom.Room.Snapshot().phase == Protocol.Info, "short configuration reserves personal time");
        }
        private static void Purchases()
        {
            var h = new Harness(); h.Public();
            Check(h.Buy(1, "BIO", 26).success && h.View(1).coins == 10, "26 through 30 costs five coins");
            Check(h.Buy(1, "BIO", 27).errorCode == "INVALID_WINDOW" && h.Buy(1, "BIO", 0).errorCode == "INVALID_WINDOW", "invalid endpoints");
            Check(h.Buy(1, "BIO", 24).errorCode == "INFORMATION_WINDOW_TOO_CLOSE" && h.View(1).coins == 10, "overlapping purchase is rejected without charging");
            Check(h.Buy(1, "BIO", 18).errorCode == "INFORMATION_WINDOW_TOO_CLOSE" && h.View(1).coins == 10, "a gap shorter than four days is rejected (18~22 then 26~30)");
            Check(h.Buy(1, "BIO", 1, 3).errorCode == "INFORMATION_WINDOW_NOT_FIVE_DAYS" && h.Buy(1, "BIO", 1, 6).errorCode == "INFORMATION_WINDOW_NOT_FIVE_DAYS", "only five-day windows can be bought");
            var request = new Command { type = "BUY_INFORMATION_WINDOW", requestId = "private-replay", turn = 1, payload = new CommandPayload { companyId = "BIO", startDay = 17, endDay = 21 } };
            Check(h.Room.ProcessCommand("c1", request).success && h.Room.ProcessCommand("c1", request).success && h.View(1).coins == 5, "exact four-day gap (17~21 then 26~30) is allowed and charged once");
            request.payload.startDay = 16; Check(h.Room.ProcessCommand("c1", request).errorCode == "REQUEST_ID_CONFLICT", "start date belongs to fingerprint");
            Check(h.Buy(1, "BIO", 26).errorCode == "ALREADY_VISIBLE" && h.View(1).coins == 5, "known window is rejected without charging");
            Check(InformationWindowRules.KnownDays(h.View(1).companies.Single(c => c.companyId == "BIO")).Skip(1).Count(d => d.HasValue) == 10, "daily mask union");
            Check(h.View(2).companies.Single(c => c.companyId == "BIO").visibleWindows.Length == 0, "personal purchases stay private");
            var privateCopy = DtoCopy.Copy(h.View(1)); privateCopy.companies.Single(c => c.companyId == "BIO").visibleWindows[0].dailyChangesBps[0] = int.MaxValue;
            Check(h.View(1).companies.Single(c => c.companyId == "BIO").visibleWindows[0].dailyChangesBps[0] != int.MaxValue, "deep copied windows");
            Check(h.Market.GetWindow(1, "BIO", 26).SequenceEqual(PatternMarketGenerator.Generate(20261108, 1).companies.Single(c => c.companyId == "BIO").dailyChangesBps.Skip(26)), "purchases never modify market");
            Check(h.Buy(2, "BIO", 22).success, "gap is checked per player: another player's private window does not block");
        }
        private static void Items()
        {
            var h = new Harness(features: true); h.Public();
            for (int p = 1; p <= 4; p++) h.Feature(p, "FreeCredits", 0);
            h.Feature(1, "FreeCredits", 5);
            Check(h.Buy(1, "AI", 1).errorCode == "ALREADY_VISIBLE", "fully public free purchase rejected");
            var view = new PlayerView(); h.Info.PopulateView(new RequestContext("PATTERNS", 1, Protocol.Info, "P1"), view);
            Check(view.ability.freeInformationCredits == 5, "no-op preserves credit");
            Check(h.Buy(1, "BIO", 26).success && h.View(1).coins == 15, "five free day credits consumed on new information only");
            Check(h.Buy(1, "AI", 3).errorCode == "INFORMATION_WINDOW_TOO_CLOSE", "private window cannot overlap the public window");
            Check(h.Buy(1, "AI", 10).success && h.Buy(1, "AI", 19).success, "two paid proofs with four-day gaps");
            h.Feature(2, "Inventory", "STEAL");
            Check(h.Send(2, "USE_INFORMATION_ITEM", new CommandPayload { itemId = "STEAL", targetPlayerId = "P1" }).success, "copy paid record");
            h.Feature(3, "Inventory", "DISTORT");
            Check(h.Send(3, "USE_INFORMATION_ITEM", new CommandPayload { itemId = "DISTORT", targetPlayerId = "P1" }).success, "hide record with exclusive dates");
            var known = InformationWindowRules.KnownDays(h.View(1).companies.Single(c => c.companyId == "AI"));
            Check(known[1].HasValue && known[5].HasValue && Enumerable.Range(10, 5).Concat(Enumerable.Range(19, 5)).Count(d => known[d].HasValue) == 5, "public window and the undistorted private proof stay visible");
            h.Clock.Seconds = 10; h.Room.Tick();
            Check(InformationWindowRules.KnownDays(h.View(1).companies.Single(c => c.companyId == "AI"))[10].HasValue, "distortion expires on server clock");
            h.Feature(4, "Inventory", "STEAL");
            Check(h.Send(4, "USE_INFORMATION_ITEM", new CommandPayload { itemId = "STEAL", targetPlayerId = "P2" }).errorCode == "NO_INFORMATION_TARGET", "stolen record cannot be stolen again");
            h.Feature(4, "Inventory", "BLACKBOX"); h.Feature(4, "Ability", "INFORMATION_BROKER");
            Check(h.Send(4, "USE_INFORMATION_ITEM", new CommandPayload { itemId = "BLACKBOX", companyId = "BIO", startDay = 1 }).success, "blackbox uses chosen window");
            Check(h.View(4).companies.Single(c => c.companyId == "BIO").visibleWindows.Count(w => w.source == "ITEM") == 2, "broker bonus uses remaining unseen dates");
        }
    }
}
