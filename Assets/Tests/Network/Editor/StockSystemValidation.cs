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

namespace BlackWhale.Tests
{
    public static class StockSystemValidation
    {
        private static void Check(bool ok, string message) => RoomValidation.Check(ok, "STOCK-SYSTEM: " + message);
        private static readonly string[] Players = { "P1", "P2", "P3", "P4" };
        private sealed class Harness
        {
            public readonly TestClock Clock = new TestClock();
            public readonly MarketService Market = MarketService.CreateStockSystem(7741);
            public readonly SharedInformationService Info;
            public readonly GameRoom Room;
            private int sequence;
            public Harness(TimerMode mode = TimerMode.Realtime, LobbySettings lobbyOptions = null)
            {
                Info = new SharedInformationService(Market);
                Room = new GameRoom("STOCK", Market, Info, Clock, mode: mode, fixedOrderForFixture: true,
                    settings: new GameSettings(5, 6, 2, enableProgressionFeatures: false));
                for (int i = 1; i <= 4; i++) Room.Join("c" + i, "Player " + i);
                if (lobbyOptions != null) Check(Send(1, "SET_LOBBY_SETTINGS", new CommandPayload { lobbySettings = lobbyOptions }).success, "host configures lobby");
                for (int i = 1; i <= 4; i++) Send(i, "SET_READY", new CommandPayload { ready = true });
                Send(1, "START_GAME");
            }
            public CommandResult Send(int player, string type, CommandPayload payload = null)
                => Room.ProcessCommand("c" + player, new Command { type = type, turn = Room.Snapshot().turn, requestId = "s" + (++sequence), developerRevision = Room.Snapshot().developerRevision, payload = payload });
            public void FinishInfo()
            {
                FinishPublic();
                for (int player = 1; player <= 4; player++)
                {
                    Check(Room.Snapshot().phase == Protocol.Info, "information stays open until everyone finishes");
                    Check(Send(player, "COMPLETE_PHASE").success, "finish information");
                }
                Check(Room.Snapshot().phase == Protocol.Investment, "four completions start trading immediately");
            }
            public void FinishPublic()
            {
                while (Room.Snapshot().publicSelectionActive)
                {
                    var s = Room.Snapshot(); int p = int.Parse(s.publicSelectionPlayerId.Substring(1));
                    Check(Send(p, "REVEAL_INFORMATION_WINDOW", new CommandPayload { companyId = UiCompany(s.publicSelectionIndex), startDay = 1 }).success, "public pick");
                }
            }
            private string UiCompany(int index) => new[] { "AI", "BANK", "FOOD", "AUTO" }[index];
            public void Choose()
            {
                foreach (var id in Players)
                {
                    var p = Market.GetPortfolio(id);
                    Check(Send(int.Parse(id.Substring(1)), StockRules.Choose, new CommandPayload { augmentId = p.candidates[0], companyId = "AI" }).success, "choose own candidate");
                }
            }
            public CommandResult Trade(int player, string company, int shares) => Send(player, StockRules.Position,
                new CommandPayload { shareOrders = new[] { new ShareOrder { companyId = company, targetShares = shares } } });
            public void Deadline() { Clock.Seconds = Room.Snapshot().phase == Protocol.Selection ? Room.Snapshot().selectionDeadlineAtServerSeconds : Room.Snapshot().phaseEndsAtServerSeconds; Room.Tick(); }
        }
        public static string[] Run()
        {
            var names = new List<string>();
            Action<string, Action> run = (name, test) => { test(); names.Add("STOCK-SYSTEM/" + name); };
            run("intraday-position-changes-and-settlement", Draft);
            run("hidden-loss-control-day-boundaries-live-and-reserved-settlement", HiddenLossControl);
            run("percentage-point-settlement-at-zero-chart-price", PercentagePointSettlement);
            run("information-then-thirty-live-closes-and-market-reveal", Timeline);
            run("private-reservations-live-supply-limits-public-fills", PickBudget);
            run("all-companies-random-public-private-purchase-replay-and-exhaustion", CompanyOnlyPublicPicks);
            run("information-depth-pricing-and-anonymous-interest-signal", DepthPricingAndInterest);
            run("configured-information-range-and-bank-visibility", ConfigurableLobby);
            run("configured-day-duration-boundaries-pause-and-catch-up", ConfiguredDayTiming);
            run("new-turn-snapshots-clear-old-private-data-and-periodic-state-keeps-deadlines", TurnSnapshots);
            run("all-unused-slots-automatically-fill-one-hundred-twenty-bank-shares", Bank);
            run("slot-weighted-multicompany-cash-and-settlement-replay", Settlement);
            run("developer-manual-days-timer-checkpoints-and-settlement-rewind", DeveloperMode);
            run("bank-reservations-limit-interest-and-idle-slots-return-to-cash", BankIntervals);
            run("eight-augment-candidates-and-choice-request-replay", Augments);
            run("six-turns-augmentation-schedule-reset-timeouts-and-final-ranking", SixTurns);
            run("turn-start-restores-all-stock-supply-and-bank-preserving-assets-and-history", ResetHoldings);
            run("private-analysis-public-portfolio-copy-and-json-integers", Copies);
            run("eight-augment-effects-information-ledger-and-cumulative-holding-boundaries", Effects);
            return names.ToArray();
        }

        private static void ConfigurableLobby()
        {
            var h = new Harness(lobbyOptions: new LobbySettings { earlyTurnCoins = 7, lateTurnCoins = 11,
                publicRevealDays = 8, informationStartDay = 5, informationEndDay = 12 });
            Check(h.Info.GetCoins("STOCK", "P1") == 7, "configured grant");
            var state = h.Room.Snapshot();
            Check(!state.publicSelectionActive && state.publicInformation.Length == 6, "every company is prepared automatically");
            foreach (var choice in state.publicInformation.Where(c => c.companyId != "BANK"))
                Check(choice.days.SequenceEqual(Enumerable.Range(5, 8)), "public information obeys the configured inclusive range");
            Check(state.publicInformation.Single(c => c.companyId == "BANK").days.SequenceEqual(Enumerable.Range(0, 31)), "BANK ignores the hidden-information boundary");
            foreach (var invalid in new[] { new LobbySettings { informationEndDay = 26 }, new LobbySettings { informationStartDay = -1 },
                new LobbySettings { informationStartDay = 20, informationEndDay = 10 }, new LobbySettings { publicRevealDays = 26 },
                new LobbySettings { daySeconds = 0 }, new LobbySettings { earlyTurnCoins = -1 } }) Check(!invalid.IsValid, "invalid options rejected");
            var copy = DtoCopy.Copy(state); copy.lobbySettings.informationEndDay = 0; copy.publicInformation[0].days[0] = 99;
            Check(h.Room.Snapshot().lobbySettings.informationEndDay == 12 && h.Room.Snapshot().publicInformation.All(c => c.days.All(d => d <= 30)), "settings and dates copied");
            Check(h.Send(1, "SET_LOBBY_SETTINGS", new CommandPayload { lobbySettings = new LobbySettings() }).errorCode == "WRONG_PHASE", "rules lock after start");
            var zero = new Harness(lobbyOptions: new LobbySettings { publicRevealDays = 0 });
            Check(zero.Room.Snapshot().publicInformation.Length == 1 && zero.Room.Snapshot().publicInformation[0].companyId == "BANK", "zero public count preserves BANK visibility");
        }
        private static void Draft()
        {
            var h = new Harness(); h.Choose(); h.FinishInfo();
            Func<int,string,int,CommandResult> position = (player, company, target) => h.Send(player, StockRules.Position,
                new CommandPayload { shareOrders = new[] { new ShareOrder { companyId = company, targetShares = target } } });
            Check(position(3, "AI", 1).success && !position(1, "AI", 11).success, "position requires enough drafted shares");
            Check(position(1, "AI", 10).success, "full entry before day one");
            h.Clock.Seconds += 2;
            Check(position(1, "AI", 4).success && h.Market.ProcessedDay == 1, "day-one mark precedes same-boundary partial exit");
            h.Clock.Seconds += 2; h.Room.Tick();
            Check(position(1, "AI", 0).success && position(1, "AI", 6).success, "exit all and re-enter");
            h.Clock.Seconds += 2; h.Room.Tick(); Check(position(1, "AI", 0).success, "exit at third close");
            h.Deadline();
            var investment = h.Market.GetRevealedMarket(new RequestContext("STOCK", 1, Protocol.Reveal))
                .Single(c => c.companyId == "AI").investments.Single(i => i.playerId == "P1");
            Check(investment.intervals.Single().entryDay == 1 && investment.intervals.Single().exitDay == 3, "history reflects only days invested");
            var prices = h.Market.GetWindow(1, "AI", 1, 3).Select(v => (10000m + v) / 10000m).ToArray();
            decimal gain = 10 * (prices[0] - 1) + 4 * (prices[1] - prices[0]) + 6 * (prices[2] - prices[1]);
            Check(investment.returnBps == (int)Math.Round(10000m * gain / 10), "realized profits include partial exit and re-entry at the observed close");
        }
        private static void PercentagePointSettlement()
        {
            var library = new StockPatternLibrary { registeredVersion = 2 };
            var flag = CustomStockPattern.Preset("SPACE", 2); flag.isBasePattern = true; flag.id = "zero-crossing";
            library.patterns.Add(flag); library.volatility.spaceBps = 10000; library.baseSettings.surgeMultiplier = 1;
            int seed = 0, zeroDay = -1;
            int[] values = null;
            for (; seed < 500; seed++)
            {
                values = PatternMarketGenerator.Inspect(seed, 1, "SPACE", library: library).DailyChangesBps;
                zeroDay = Array.FindIndex(values, v => v == -10000);
                if (zeroDay >= 1 && zeroDay < 30) break;
            }
            Check(zeroDay >= 1 && zeroDay < 30, "fixture reaches zero chart price before a held day");
            int heldDay = zeroDay + 1;
            var market = MarketService.CreateStockSystem(seed, library);
            market.BeginTurn(new RequestContext("PP", 1, Protocol.Info), Players);
            Check(market.SaveReservations(new RequestContext("PP", 1, Protocol.Info, "P1"), new[] {
                new Investment { companyId = "SPACE", intervals = new[] { new Interval { entryDay = heldDay, exitDay = heldDay } } }
            }).Success, "one-day plan starts at zero chart price");
            Check(market.TradeShares(new RequestContext("PP", 1, Protocol.Selection, "P1"), new[] {
                new ShareOrder { companyId = "SPACE", targetShares = 10 }
            }).Success, "ten slots allocated to percentage-point trade");
            market.LockPortfolios();
            var board = market.SettleTurn(new RequestContext("PP", 1, Protocol.Reveal));
            int delta = values[heldDay] - values[zeroDay];
            var profit = market.GetSettlementDetail(1).Single(p => p.playerId == "P1").companyProfits.Single(p => p.companyId == "SPACE");
            Check(profit.returnBps == Math.Max(-10000, delta), "single-day return equals chart delta even when entry chart price is zero");
            Check(Math.Abs(profit.profit - (decimal)10000 * 10 / 30 * profit.returnBps / 10000) <= 1, "turn capital and allocated slots determine profit without price division");
            Check(board.rows.All(p => p.totalAssets >= 0), "final assets retain existing zero floor");
        }
        private static void Timeline()
        {
            var h = new Harness(); h.Choose(); h.FinishInfo(); h.Room.DrainMessages();
            double start = h.Clock.Seconds;
            Check(h.Market.ProcessedDay == 0 && !h.Room.DrainMessages().Any(m => m.Message.type == "MARKET_REVEAL"), "market remains hidden until plans are final");
            Check(h.Room.Snapshot().phaseEndsAtServerSeconds == start + 60, "sixty seconds of live trading");
            for (int day = 1; day <= 29; day++) {
                h.Clock.Seconds = start + day * 2 - .001; h.Room.Tick();
                Check(h.Market.ProcessedDay == day - 1, "no early close");
                h.Clock.Seconds += .001; h.Room.Tick();
                var view = h.Room.DrainMessages().Last(d => d.PlayerId == "P1" && d.Message.type == "PLAYER_VIEW").Message.playerView;
                Check(view.portfolio.processedDay == day && view.companies.All(c =>
                    c.visibleWindows.Single(w => w.source == "MARKET").dailyChangesBps.Length == day), "all companies reveal one more day");
            }
            h.Deadline();
            Check(h.Room.Snapshot().phase == Protocol.Reveal && h.Market.ProcessedDay == 30, "thirtieth close ends trading");
            Check(h.Room.DrainMessages().Count(m => m.Message.type == "MARKET_REVEAL") == 4, "one market reveal per player");
        }
        private static void ConfiguredDayTiming()
        {
            foreach (int seconds in new[] { 1, 5, 60 }) {
                var h = new Harness(lobbyOptions: new LobbySettings { daySeconds = seconds });
                h.Choose(); h.FinishInfo();
                double start = h.Clock.Seconds;
                Check(h.Room.Snapshot().lobbySettings.daySeconds == seconds, "configured day duration is public");
                Check(h.Room.Snapshot().phaseEndsAtServerSeconds == start + 30 * seconds, "investment deadline uses thirty configured days");
                Check(StockRules.CurrentDay(h.Room.Snapshot(), h.Market.GetPortfolio("P1")) == 1, "first trading day is active immediately");
                h.Clock.Seconds = start + seconds - .001; h.Room.Tick();
                Check(h.Market.ProcessedDay == 0, "no premature close");
                h.Clock.Seconds = start + seconds; h.Room.Tick();
                Check(h.Market.ProcessedDay == 1 && StockRules.CurrentDay(h.Room.Snapshot(), h.Market.GetPortfolio("P1")) == 2, "one configured interval closes day one and activates day two");
                h.Clock.Seconds = start + 3 * seconds + .25; h.Room.Tick();
                Check(h.Market.ProcessedDay == 3, "late frames process every elapsed configured interval");
                Check(h.Room.SetPaused(true), "pause configured timeline");
                h.Clock.Seconds += 100; h.Room.Tick();
                Check(h.Market.ProcessedDay == 3 && StockRules.CurrentDay(h.Room.Snapshot(), h.Market.GetPortfolio("P1")) == 4, "pause keeps the current day fixed");
                Check(h.Room.SetPaused(false), "resume configured timeline");
                h.Clock.Seconds += seconds; h.Room.Tick();
                Check(h.Market.ProcessedDay == 4, "resume continues with the configured interval");
                h.Clock.Seconds = start + 100 + 30 * seconds - .001; h.Room.Tick();
                Check(h.Market.ProcessedDay == 29 && StockRules.CurrentDay(h.Room.Snapshot(), h.Market.GetPortfolio("P1")) == 30, "last day remains visible until its close");
                h.Clock.Seconds += .001; h.Room.Tick();
                Check(h.Market.ProcessedDay == 30 && h.Room.Snapshot().phase == Protocol.Reveal, "last configured close starts market reveal");
                Check(!StockRules.CurrentDay(h.Room.Snapshot(), h.Market.GetPortfolio("P1")).HasValue, "current-day highlight ends with live trading");
            }
        }
        private static void PickBudget()
        {
            var h = new Harness(); h.Choose(); h.FinishPublic();
            var plan = new Investment { companyId = "AI", intervals = new[] { new Interval { entryDay = 3, exitDay = 5 } } };
            Check(h.Send(2, "SAVE_TRADE_PLAN", new CommandPayload { investments = new[] { plan } }).success, "private range saved during info");
            h.FinishInfo();
            Check(h.Market.GetReservations("P2").Single().intervals.Single().exitDay == 5 && h.Market.GetReservations("P1").Length == 0, "plan survives transition privately");
            Check(h.Trade(4, "AI", 10).success && h.Trade(2, "AI", 10).success && !h.Trade(1, "AI", 1).success, "simultaneous shared supply");
            h.Clock.Seconds += 2;
            Check(h.Trade(4, "AI", 6).success && h.Trade(1, "AI", 4).success, "sale immediately frees shares for another player");
            Check(h.Trade(2, "BIO", 10).success && h.Trade(2, "BANK", 10).success && !h.Trade(2, "FOOD", 1).success, "total capacity applies across companies");
            var fills = DtoCopy.Copy(h.Room.Snapshot()).shareMarket.Single(c => c.companyId == "AI").fills;
            Check(fills.Length == 4 && fills[2].playerId == "P4" && fills[2].day == 1 && fills[2].quantity == -4 && fills[3].quantity == 4, "public fills record partial sale and same-day buy");
        }
        private static void TurnSnapshots()
        {
            var store = new ClientStateStore();
            var previous = new PlayerView { turn = 1, phase = Protocol.Result, recipientPlayerId = "P1",
                ownInvestments = new[] { new Investment { companyId = "AI", intervals = new[] { new Interval { entryDay = 1, exitDay = 5 } } } } };
            store.SetPublicState(new PublicState { turn = 1, phase = Protocol.Result, serverElapsedSeconds = 100, phaseEndsAtServerSeconds = 105 });
            store.SetPlayerView(previous);
            store.MarkPhaseCompleted("old-done", 1, Protocol.Result);
            Check(store.OrderLocked, "previous turn starts locked");
            var current = new PublicState { turn = 2, phase = Protocol.Info, serverElapsedSeconds = 105, phaseEndsAtServerSeconds = 165 };
            store.SetPublicState(current);
            Check(store.View == null && !store.OrderLocked && !store.HasPending("SUBMIT_INVESTMENT"), "new public turn cannot retain previous holdings or locked order controls");
            Check(store.SetPlayerView(previous) == ApplyOutcome.Stale && store.View == null, "late previous-turn private view cannot restore stale buttons");
            store.SetPlayerView(new PlayerView { turn = 3, phase = StockRules.AugmentPhase, recipientPlayerId = "P1" });
            Check(store.Public == null && store.CurrentTurn == 3, "private snapshot arriving first cannot keep a previous-turn timer");
            store.SetPublicState(new PublicState { turn = 3, phase = StockRules.AugmentPhase, serverElapsedSeconds = 170, phaseEndsAtServerSeconds = 190 });
            Check(store.View.turn == store.Public.turn && !store.OrderLocked, "matching snapshots resume an unlocked current turn");

            var h = new Harness(); h.Choose(); h.FinishInfo(); h.Room.DrainMessages();
            var before = h.Room.Snapshot();
            h.Clock.Seconds += .5; h.Room.Tick();
            var deliveries = h.Room.DrainMessages();
            Check(deliveries.Count(d => d.Message.type == "PUBLIC_STATE") == 4, "server refreshes every client's authoritative timer twice per second");
            Check(h.Room.Snapshot().selectionDeadlineAtServerSeconds == before.selectionDeadlineAtServerSeconds && h.Room.Snapshot().selectionIndex == before.selectionIndex,
                "periodic synchronization never restarts or extends a purchase window");
            Check(h.Send(1, "GET_PLAYER_VIEW").success, "explicit resynchronization is supported");
            deliveries = h.Room.DrainMessages();
            Check(deliveries.Any(d => d.PlayerId == "P1" && d.Message.type == "PUBLIC_STATE") && deliveries.Any(d => d.PlayerId == "P1" && d.Message.type == "PLAYER_VIEW"),
                "resynchronization returns matching deadlines and private player data");
            Check(deliveries.All(d => d.PlayerId == "P1"), "private resynchronization never leaks to other players");
        }
        private static void CompanyOnlyPublicPicks()
        {
            var h = new Harness(lobbyOptions: new LobbySettings { earlyTurnCoins = 60 }); h.Choose();
            var state = h.Room.Snapshot();
            Check(!state.publicSelectionActive, "no player public-pick round remains");
            foreach (var company in InvestmentRules.CompanyIds)
            {
                var days = state.publicInformation.Single(c => c.companyId == company).days;
                Check(days.Length == (company == "BANK" ? 31 : 5) && days.Distinct().Count() == days.Length, "unique public dates for every company");
                Check(company == "BANK" || days.All(d => d >= 1 && d <= 25), "hidden dates cannot be public information");
                foreach (string player in Players)
                {
                    var view = h.Info.BuildPlayerView(new RequestContext("STOCK", 1, Protocol.Info, player), h.Market.GetOwnInvestmentState(player));
                    var known = InformationWindowRules.KnownDays(view.companies.Single(c => c.companyId == company));
                    Check(Enumerable.Range(1, 30).All(d => known[d].HasValue == days.Contains(d)), "all players see exactly the same initial dates");
                }
            }
            int initial = h.Info.GetCoins("STOCK", "P1");
            var request = new Command { type = "BUY_INFORMATION_WINDOW", requestId = "random-replay", turn = 1,
                payload = new CommandPayload { companyId = "AI", startDay = 30, endDay = 30, informationDays = 3 } };
            Check(h.Room.ProcessCommand("c1", request).success && h.Room.ProcessCommand("c1", request).success, "random purchase is replay safe and ignores chosen dates");
            Check(h.Info.GetCoins("STOCK", "P1") == initial - 3, "only three coins charged once");
            request.payload.informationDays = 2;
            Check(h.Room.ProcessCommand("c1", request).errorCode == "REQUEST_ID_CONFLICT", "purchase count is fingerprinted");
            Check(h.Send(1, "BUY_INFORMATION_WINDOW", new CommandPayload { companyId = "BANK" }).errorCode == "ALREADY_VISIBLE", "BANK cannot be charged");
            // Depth pricing: the fourth to sixth AI dates cost 2 coins, later dates 3 coins (3 + 6 + 14 * 3 = 51 for all 20).
            for (int i = 0; i < 17; i++) Check(h.Send(1, "BUY_INFORMATION_WINDOW", new CommandPayload { companyId = "AI" }).success, "every purchase exposes a new date");
            Check(h.Info.GetCoins("STOCK", "P1") == initial - 51, "deeper purchases in one company cost 1/2/3 coins");
            Check(h.Send(1, "BUY_INFORMATION_WINDOW", new CommandPayload { companyId = "AI" }).errorCode == "ALREADY_VISIBLE", "stop at exhaustion without charging");
            var own = h.Info.BuildPlayerView(new RequestContext("STOCK", 1, Protocol.Info, "P1"), h.Market.GetOwnInvestmentState("P1"));
            var other = h.Info.BuildPlayerView(new RequestContext("STOCK", 1, Protocol.Info, "P2"), h.Market.GetOwnInvestmentState("P2"));
            Check(InformationWindowRules.KnownDays(own.companies.Single(c => c.companyId == "AI")).Skip(1).Take(25).All(v => v.HasValue), "all allowed AI dates eventually visible");
            Check(own.companies.Single(c => c.companyId == "AI").visibleWindows.Where(w => w.source == "PURCHASED").All(w => w.startDay <= 25), "purchases never reveal hidden days");
            Check(other.companies.All(c => c.visibleWindows.All(w => w.source != "PURCHASED")), "paid information stays private");
            foreach (var company in own.companies)
                company.visibleWindows = new[] { new VisibleInformationWindow { startDay = 1, dailyChangesBps = new int[25] } };
            var strategy = new BlackWhale.Bots.InformationBotStrategy(2, new BlackWhale.Bots.BotPolicy(BlackWhale.Bots.BotDifficulty.Hard));
            Check(strategy.NextRandomPurchase(own, new LobbySettings()) == null, "BOT does not buy hidden dates after exhausting the allowed range");
            own.companies.Single(c => c.companyId == "BIO").visibleWindows = Array.Empty<VisibleInformationWindow>();
            Check(strategy.NextRandomPurchase(own, new LobbySettings()).CompanyId == "BIO", "BOT buys only a company with eligible unknown dates");
        }
        private static void DepthPricingAndInterest()
        {
            Check(Enumerable.Range(0, 8).Select(InformationWindowRules.RandomDayCost).SequenceEqual(new[] { 1, 1, 1, 2, 2, 2, 3, 3 }), "1-3rd date 1 coin, 4-6th 2 coins, then 3 coins");
            Check(InformationWindowRules.RandomDaysCost(2, 3) == 1 + 2 + 2 && InformationWindowRules.RandomDaysCost(0, 0) == 0, "multi-day cost sums each step");
            var h = new Harness(); h.Choose();
            Check(h.Room.Snapshot().informationInterest.All(i => i.purchasedDays == 0) && h.Room.Snapshot().informationInterest.All(i => i.companyId != "BANK")
                && h.Room.Snapshot().informationInterest.Length == InvestmentRules.CompanyIds.Length - 1, "interest starts at zero for every non-BANK company");
            int start = h.Info.GetCoins("STOCK", "P1"), start2 = h.Info.GetCoins("STOCK", "P2");
            for (int i = 0; i < 4; i++) Check(h.Send(1, "BUY_INFORMATION_WINDOW", new CommandPayload { companyId = "AI" }).success, "P1 digs into AI");
            Check(h.Info.GetCoins("STOCK", "P1") == start - 5, "fourth AI date costs 2 coins");
            var replay = new Command { type = "BUY_INFORMATION_WINDOW", requestId = "depth-replay", turn = 1, payload = new CommandPayload { companyId = "AI" } };
            var first = h.Room.ProcessCommand("c1", replay); var again = h.Room.ProcessCommand("c1", replay);
            Check(first.success && again.success && h.Info.GetCoins("STOCK", "P1") == start - 7, "replayed request is charged once at the depth price");
            Check(h.Send(1, "BUY_INFORMATION_WINDOW", new CommandPayload { companyId = "FOOD" }).success && h.Info.GetCoins("STOCK", "P1") == start - 8, "another company starts again at 1 coin");
            Check(h.Send(2, "BUY_INFORMATION_WINDOW", new CommandPayload { companyId = "AI" }).success && h.Info.GetCoins("STOCK", "P2") == start2 - 1, "each player's depth price is separate");
            var state = h.Room.Snapshot();
            Check(InformationWindowRules.Interest(state, "AI") == 6 && InformationWindowRules.Interest(state, "FOOD") == 1 && InformationWindowRules.Interest(state, "BIO") == 0, "interest is the anonymous total of bought dates");
            Check(typeof(CompanyInterest).GetFields().Select(f => f.Name).OrderBy(n => n).SequenceEqual(new[] { "companyId", "purchasedDays" }), "interest carries no buyer identity");
            var view = h.Info.BuildPlayerView(new RequestContext("STOCK", 1, Protocol.Info, "P1"), h.Market.GetOwnInvestmentState("P1"));
            var ai = view.companies.Single(c => c.companyId == "AI");
            Check(InformationWindowRules.PurchasedDays(ai) == 5 && InformationWindowRules.NextRandomDayCost(ai) == 2, "client shows the next AI price from its own purchases");
            Check(ai.visibleWindows.Where(w => w.source == "PURCHASED").Sum(w => w.paidCoins) == 7, "each record keeps the coins it really cost");
            // Spend down to 1 coin: the bot must not pick a company whose next date costs more than it has.
            var poor = h.Info.BuildPlayerView(new RequestContext("STOCK", 1, Protocol.Info, "P1"), h.Market.GetOwnInvestmentState("P1"));
            poor.coins = 1;
            foreach (var c in poor.companies.Where(c => c.companyId != "AI" && c.companyId != "BANK"))
                c.visibleWindows = new[] { new VisibleInformationWindow { startDay = 1, dailyChangesBps = new int[25] } };
            var strategy = new BlackWhale.Bots.InformationBotStrategy(3, new BlackWhale.Bots.BotPolicy(BlackWhale.Bots.BotDifficulty.Hard));
            Check(strategy.NextRandomPurchase(poor, new LobbySettings()) == null, "BOT skips a company it cannot afford at depth price");
            Check(h.Send(1, "BUY_INFORMATION_WINDOW", new CommandPayload { companyId = "AI" }).success && h.Info.GetCoins("STOCK", "P1") == start - 10, "a player with coins can still go deeper");
        }
        private static void Bank()
        {
            var m = MarketService.CreateStockSystem(19); m.BeginTurn(new RequestContext("BANK", 1, Protocol.Info), Players);
            var bank = m.GetShareMarket().Single(c => c.companyId == "BANK");
            Check(bank.totalShares == 120 && bank.owners.All(o => o.shares == 0), "all four players automatically deposit thirty shares");
            Check(m.TradeShares(new RequestContext("BANK", 1, Protocol.Selection, "P1"), new[] { new ShareOrder { companyId = "BANK", targetShares = 1 } }).ErrorCode == "AUTOMATIC_BANK", "BANK needs no manual order");
            m.LockPortfolios(); var board = m.SettleTurn(new RequestContext("BANK", 1, Protocol.Reveal));
            Check(board.rows.All(r => r.totalAssets == 10000 + m.GetWindow(1, "BANK", 30, 1)[0]), "every unused slot earns BANK returns");
        }
        private static void Settlement()
        {
            var m = MarketService.CreateStockSystem(19);
            m.BeginTurn(new RequestContext("S", 1, Protocol.Info), Players);
            var ctx = new RequestContext("S", 1, Protocol.Investment, "P1");
            var range = new Interval { entryDay = 3, exitDay = 5 };
            Check(m.TradeShares(new RequestContext("S",1,Protocol.Selection,"P1"), new[] { new ShareOrder { companyId="AI",targetShares=10 } }).Success, "reserve supply");
            Check(m.SaveReservations(ctx, new[] { new Investment { companyId = "AI", intervals = new[] { range } } }).Success, "range saved after shares purchased");
            Func<int> bank = () => m.GetShareMarket().Single(c => c.companyId=="BANK").owners[0].shares;
            Check(bank() == 30, "waiting slots earn BANK");
            m.SimulateDay(); m.SimulateDay(); m.SimulateDay();
            Check(bank() == 20, "ten slots enter on day three");
            m.SimulateDay(); m.SimulateDay();
            Check(bank() == 30 && m.GetPortfolio("P1").usedSlots==0, "sale returns supply and deposits");
            m.LockPortfolios();
            var board=m.SettleTurn(new RequestContext("S",1,Protocol.Reveal));
            Func<string,int,decimal> price=(id,d)=>10000m+m.GetWindow(1,id,d,1)[0];
            decimal mixed = price("BANK",2) - 10000m + price("AI",5) - price("AI",2) + price("BANK",30) - price("BANK",5);
            decimal expected = 10000m + mixed / 3m + (price("BANK",30) - 10000m) * 2m / 3m;
            Check(Math.Abs(board.rows.Single(v=>v.playerId=="P1").totalAssets-expected)<=1, "actual entry/exit and BANK valuation");
            var bankPrices = new[] { 0 }.Concat(m.GetWindow(1, "BANK", 1, 30)).ToArray();
            Check(Enumerable.Range(1, 30).All(d => Math.Abs(bankPrices[d] - bankPrices[d - 1]) <= 600), "BANK base patterns respect configured surge limit");
        }
        private sealed class LocalClient : IGameClient
        {
            private readonly Harness harness;
            private readonly int player;
            public event Action<ServerMessage> MessageReceived;
            public LocalClient(Harness harness, int player) { this.harness = harness; this.player = player; }
            public void SendCommand(Command command)
            { Check(harness.Room.ProcessCommand("c" + player, command).success, "developer client command " + command.type); }
            public void Receive(ServerMessage message) => MessageReceived?.Invoke(message);
            public void Pump() { }
            public void Dispose() { }
        }
        private static void DeveloperMode()
        {
            var h = new Harness(TimerMode.Developer); h.Choose();
            h.Clock.Seconds += 1000; h.Room.Tick();
            Check(h.Room.Snapshot().phase == Protocol.Info && h.Market.ProcessedDay == 0, "developer information has no clock");
            Check(h.Send(2, "DEV_NEXT").errorCode == "HOST_ONLY", "only host may control stages");
            int coins = h.Info.GetCoins("STOCK", "P1");
            var publicDays = h.Room.Snapshot().publicInformation.SelectMany(c => c.days.Select(d => c.companyId + ":" + d)).ToArray();
            Check(h.Send(1, "BUY_INFORMATION_WINDOW", new CommandPayload { companyId = "BIO" }).success, "buy before reset");
            Check(h.Send(1, "DEV_RESET").success && h.Info.GetCoins("STOCK", "P1") == coins, "information reset restores coins and purchases");
            Check(h.Room.Snapshot().publicInformation.SelectMany(c => c.days.Select(d => c.companyId + ":" + d)).SequenceEqual(publicDays), "reset keeps the same public random draw");
            Check(h.Room.ProcessCommand("c1", new Command { type = "DEV_NEXT", turn = 1, requestId = "stale-control" }).errorCode == "STALE_DEVELOPER_STATE", "old revision cannot act after reset");
            Check(h.Send(1, "BUY_INFORMATION_WINDOW", new CommandPayload { companyId = "BIO" }).success, "buy after reset");
            Check(h.Send(1, "DEV_NEXT").success && h.Room.Snapshot().phase == Protocol.Investment, "host confirmation starts investing");
            Check(h.Trade(1, "AI", 5).success && h.Send(1, "DEV_NEXT").success && h.Send(1, "DEV_NEXT").success && h.Market.ProcessedDay == 2, "confirm exactly one day at a time");
            h.Clock.Seconds += 1000; h.Room.Tick(); Check(h.Market.ProcessedDay == 2, "manual investing never auto advances");
            Check(h.Send(1, "DEV_TIMER").success && h.Room.Snapshot().developerTimerRunning, "timer resumes from current close");
            h.Clock.Seconds += 1.999; h.Room.Tick(); Check(h.Market.ProcessedDay == 2, "resumed timer does not skip days");
            h.Clock.Seconds += .001; h.Room.Tick(); Check(h.Market.ProcessedDay == 3, "one resumed interval closes the next day");
            Check(h.Send(1, "DEV_TIMER").success, "timer can be stopped"); h.Clock.Seconds += 1000; h.Room.Tick();
            Check(h.Market.ProcessedDay == 3, "stopped timer remains manual");
            Check(h.Send(1, "DEV_RESET").success && h.Market.ProcessedDay == 0 && h.Market.GetPortfolio("P1").usedSlots == 0, "investment reset restores positions and day");
            Check(h.Info.GetCoins("STOCK", "P1") == coins - 1, "investment reset keeps completed information purchases");
            Check(h.Send(1, "DEV_PREVIOUS").success && h.Room.Snapshot().phase == Protocol.Info && h.Info.GetCoins("STOCK", "P1") == coins, "previous stage restores information-stage entry");
            Check(h.Send(1, "DEV_NEXT").success, "re-enter investment");
            for (int day = 0; day < 30; day++) Check(h.Send(1, "DEV_NEXT").success, "manual close");
            Check(h.Room.Snapshot().phase == Protocol.Reveal && h.Market.ProcessedDay == 30, "last confirmation reveals market");
            Check(h.Send(1, "DEV_NEXT").success && h.Room.Snapshot().phase == Protocol.Result, "confirm settlement");
            long assets = h.Market.GetAssets("P1");
            Check(h.Send(1, "DEV_PREVIOUS").success && h.Market.GetAssets("P1") == 10000, "back from results rolls back settlement exactly");
            Check(h.Send(1, "DEV_NEXT").success && h.Market.GetAssets("P1") == assets, "settling again never duplicates gains");
            var store = new ClientStateStore();
            store.SetPublicState(new PublicState { turn = 1, phase = Protocol.Result, developerMode = true });
            store.SetPlayerView(new PlayerView { turn = 1, ownInvestments = new[] { new Investment { companyId = "AI" } } });
            store.MarkPhaseCompleted("done", 1, Protocol.Info);
            store.SetPublicState(new PublicState { turn = 1, phase = Protocol.Info, developerMode = true, developerRevision = 1 });
            Check(!store.OrderLocked && store.View == null && !store.IsPhaseCompleted(1, Protocol.Info), "client reset clears stale locks and completions");
        }
        private static void BankIntervals()
        {
            var m = MarketService.CreateStockSystem(19);
            m.BeginTurn(new RequestContext("B", 1, Protocol.Info), Players);
            var ctx = new RequestContext("B", 1, Protocol.Investment, "P1");
            var bank = new Investment { companyId = "BANK", intervals = new[] {
                new Interval { entryDay = 3, exitDay = 5 }, new Interval { entryDay = 10, exitDay = 12 } } };
            Check(m.SubmitInvestment(ctx, new[] { bank }).Success, "BANK may be reserved without purchasing shares");
            Check(m.GetReservations("P1").Single().intervals.Length == 2, "multiple BANK ranges retained");
            m.GetReservations("P1")[0].intervals[0].entryDay = 1;
            Check(m.GetReservations("P1")[0].intervals[0].entryDay == 3, "BANK reservation copy isolated");
            Check(m.TradeShares(new RequestContext("B", 1, Protocol.Selection, "P3"), new[] { new ShareOrder { companyId = "AI", targetShares = 10 } }).Success, "stock slots reserved alongside BANK");
            Check(m.SubmitInvestment(new RequestContext("B", 1, Protocol.Investment, "P3"), new[] { bank,
                new Investment { companyId = "AI", intervals = new[] { new Interval { entryDay = 3, exitDay = 5 } } } }).Success, "stock and BANK intervals may overlap for different slots");
            var p4 = new RequestContext("B", 1, Protocol.Investment, "P4");
            Check(!m.SaveReservations(p4, new[] { new Investment { companyId = "BANK", intervals = new[] { new Interval { entryDay = 8, exitDay = 3 } } } }).Success, "invalid BANK interval rejected");
            Check(m.SaveReservations(p4, new[] { bank }).Success && m.SaveReservations(p4, Array.Empty<Investment>()).Success, "removing BANK reservation restores default");
            m.LockPortfolios(); var board = m.SettleTurn(new RequestContext("B", 1, Protocol.Reveal));
            Func<int, decimal> price = d => 10000m + m.GetWindow(1, "BANK", d, 1)[0];
            decimal expected = 10000m + price(5) - price(2) + price(12) - price(9);
            Check(Math.Abs(board.rows.Single(v => v.playerId == "P1").totalAssets - expected) <= 1, "BANK interest only accrues within reserved intervals");
            Check(board.rows.Single(v => v.playerId == "P2").totalAssets == 10000 + m.GetWindow(1, "BANK", 30, 1)[0], "unconfigured BANK preserves full-period return");
            long nextAssets = board.rows.Single(v => v.playerId == "P1").totalAssets;
            decimal stockGain = m.GetWindow(1, "AI", 5, 1)[0] - m.GetWindow(1, "AI", 2, 1)[0];
            decimal mixed = 10000m + stockGain / 3 + (price(5) - price(2)) * 2 / 3 + price(12) - price(9);
            Check(Math.Abs(board.rows.Single(v => v.playerId == "P3").totalAssets - mixed) <= 2, "BANK accrues only on idle slots, without double counting stock gains");
            Check(board.rows.Single(v => v.playerId == "P4").totalAssets == board.rows.Single(v => v.playerId == "P2").totalAssets, "deleted BANK reservation restores full-period return");
            m.BeginTurn(new RequestContext("B", 2, Protocol.Info), Players);
            Check(m.GetReservations("P1").Length == 0, "new turn clears BANK reservation");
            m.LockPortfolios(); board = m.SettleTurn(new RequestContext("B", 2, Protocol.Reveal));
            Check(Math.Abs(board.rows.Single(v => v.playerId == "P1").totalAssets - nextAssets * (1 + m.GetWindow(2, "BANK", 30, 1)[0] / 10000m)) <= 1, "next turn restores full-period BANK");
        }
        private static List<string> OwnedAugments(MarketService market, string id)
        {
            var dictionary = (IDictionary)typeof(MarketService).GetField("stockPlayers", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(market);
            var player = dictionary[id]; return (List<string>)player.GetType().GetField("Augments").GetValue(player);
        }
        private static void Augments()
        {
            var h = new Harness();
            foreach (var id in Players) Check(h.Market.GetPortfolio(id).candidates.Distinct().Count() == 3 && !h.Market.GetPortfolio(id).candidates.Contains("FLEXIBLE"), "three distinct candidates exclude the effect now provided by default");
            Check(h.Send(1, StockRules.Choose, new CommandPayload { augmentId = "not-a-candidate" }).errorCode == "INVALID_AUGMENT", "cannot invent an augment"); h.Choose(); h.FinishInfo();
            var order = new Command { type = StockRules.Position, requestId = "share-replay", turn = 1, payload = new CommandPayload { shareOrders = new[] { new ShareOrder { companyId = "FOOD", targetShares = 5 } } } };
            Check(h.Room.ProcessCommand("c2", order).success && h.Room.ProcessCommand("c2", order).success, "share request replay does not advance twice");
            order.payload.shareOrders[0].targetShares = 4; Check(h.Room.ProcessCommand("c2", order).errorCode == "REQUEST_ID_CONFLICT", "share quantities included in fingerprint");
        }
        private static void SixTurns()
        {
            var h = new Harness(); var augmentationTurns = new List<int>();
            while (h.Room.Snapshot().phase != Protocol.Ended)
            {
                var s = h.Room.Snapshot();
                if (s.phase == Protocol.Info) {
                    Check(Players.All(id => h.Market.GetPortfolio(id).usedSlots == 0), "every information phase starts without previous holdings");
                    h.FinishInfo();
                }
                else if (s.phase == StockRules.AugmentPhase) { augmentationTurns.Add(s.turn); h.Deadline(); }
                else if (s.phase == Protocol.Selection)
                {
                    if (s.turn == 1 && s.selectionIndex == 0 && s.currentPlayerId == "P1") { Check(h.Trade(1, "FOOD", 7).success, "first turn holding"); h.Send(1, StockRules.Pass); }
                    else h.Deadline();
                }
                else h.Deadline();
                Check(h.Room.Snapshot().turn <= 6, "never enters turn seven");
            }
            Check(augmentationTurns.SequenceEqual(new[] { 1, 4 }), "two timed augmentation rounds");
            Check(h.Market.GetPortfolio("P1").usedSlots == 0, "first-turn holdings never carry into later turns through timeouts");
            Check(h.Market.GetPortfolio("P1").augments.Length == 2, "one new augment each round, timeout selects a valid first candidate");
            var final = h.Room.DrainMessages().Last(d => d.Message.type == "SCOREBOARD").Message.scoreboard;
            Check(final.turn == 6 && final.gameComplete && !string.IsNullOrEmpty(final.winnerPlayerId), "server declares winner at turn six");
        }
        private static void ResetHoldings()
        {
            var m = MarketService.CreateStockSystem(19); m.BeginTurn(new RequestContext("RESET", 1, Protocol.Info), Players);
            OwnedAugments(m, "P1").Add("ANALYST");
            foreach (string player in Players.Take(2)) Check(m.TradeShares(new RequestContext("RESET", 1, Protocol.Selection, player),
                new[] { new ShareOrder { companyId = "AI", targetShares = 10 } }).Success, "previous turn can sell out a company");
            Check(m.SaveReservations(new RequestContext("RESET", 1, Protocol.Investment, "P1"), new[] {
                new Investment { companyId = "AI", intervals = Array.Empty<Interval>() } }).Success, "full-period holding has a saved plan");
            m.LockPortfolios();
            var board = m.SettleTurn(new RequestContext("RESET", 1, Protocol.Reveal));
            Check(m.GetPortfolio("P1").usedSlots == 10, "holdings remain available in the completed turn's result");
            m.BeginTurn(new RequestContext("RESET", 2, Protocol.Info), Players);
            foreach (string player in Players)
            {
                var p = m.GetPortfolio(player);
                Check(p.usedSlots == 0 && p.CashSlots == 30 && p.holdings.All(h => h.shares == 0), "each player starts with zero stocks and thirty BANK slots");
                Check(m.GetReservations(player).Length == 0 && p.draftSharesRemaining == 15, "old plans reset and purchase allowance is restored");
                Check(m.GetOwnInvestmentState(player).TotalAssets == board.rows.Single(r => r.playerId == player).totalAssets, "settled assets carry forward without discarding profit");
            }
            var market = m.GetShareMarket();
            Check(market.Where(c => c.companyId != "BANK").All(c => c.totalShares == 20 && c.owners.All(o => o.shares == 0 && o.reserved == 0)), "all company supply is available again");
            Check(market.Single(c => c.companyId == "BANK").owners.Sum(o => o.shares) == 0, "BANK supply starts unclaimed");
            Check(m.GetPortfolio("P1").augments.Contains("ANALYST") && m.InformationDiscount("P1") == 0, "earned augments still apply in the new turn");
            Check(m.GetRevealedMarket(new RequestContext("RESET", 1, Protocol.Reveal)).Single(c => c.companyId == "AI").investments.Single(i => i.playerId == "P1").shares == 10,
                "reset never rewrites the previous turn's public trade history");
            Check(m.TradeShares(new RequestContext("RESET", 2, Protocol.Selection, "P3"), new[] { new ShareOrder { companyId = "AI", targetShares = 10 } }).Success,
                "a different player can purchase the previously sold-out stock");
        }
        private static void Copies()
        {
            var h = new Harness(); h.Room.DrainMessages();
            var s = h.Room.Snapshot(); var copy = DtoCopy.Copy(s); copy.shareMarket[0].owners[0].shares = 999;
            Check(h.Room.Snapshot().shareMarket[0].owners[0].shares == 0, "public share snapshots cannot mutate the ledger");
            var p = h.Market.GetPortfolio("P1"); p.holdings[0].shares = 123;
            Check(h.Market.GetPortfolio("P1").usedSlots == 0, "private portfolio copies isolated");
            bool rejected = false;
            try { BlackWhale.Network.RequestJsonValidator.Validate("{\"command\":{\"payload\":{\"shareOrders\":[{\"companyId\":\"AI\",\"targetShares\":1.5}]}}}"); } catch (FormatException) { rejected = true; }
            Check(rejected, "share count cannot be fractional or coerced by JSON decoding");
            Check(StockRules.Augments.Length == 8 && StockRules.Augments.All(id => StockRules.Description(id).Length > 0), "all eight augments defined");
        }
        private static void HiddenLossControl()
        {
            var library = StockPatternLibrary.CreateDefault(); library.patterns.Clear();
            foreach (string company in InvestmentRules.CompanyIds) library.volatility.SetDailyLimitBps(company, 1001);
            var line = CustomStockPattern.Preset("AI", 0); line.isBasePattern = true; line.baseShape = BasePatternShape.Authored;
            line.startDay = 22; line.vertices = new List<double> { 0, 1, 0, 1, 0, 1, 0, 0, 1 };
            line.segmentDays = Enumerable.Repeat(1, 8).ToList(); library.patterns.Add(line);
            // Exactly +/-1000 bps per moving day, with day 29 flat. The shared authored base also
            // makes BANK move during these dates, so exemption is checked against a non-flat BANK.
            var cases = new[] {
                new[] { 22, 25, 1000, 1000 }, new[] { 26, 26, -200, -1000 },
                new[] { 27, 27, 500, 1000 }, new[] { 28, 28, -200, -1000 },
                new[] { 29, 29, 0, 0 }, new[] { 30, 30, 500, 1000 },
                new[] { 26, 30, 600, 0 }, new[] { 22, 30, 1600, 1000 }
            };
            foreach (bool live in new[] { false, true })
            foreach (var sample in cases)
            {
                var market = MarketService.CreateStockSystem(41, library);
                market.BeginTurn(new RequestContext("H", 1, Protocol.Info), Players);
                OwnedAugments(market, "P1").Add("HIDDEN_LOSS_CONTROL");
                foreach (string player in Players.Take(2)) OwnedAugments(market, player).Add("CASH_INTEREST");
                if (live)
                {
                    market.BeginLiveTrading();
                    for (int day = 1; day <= 30; day++)
                    {
                        if (day == sample[0] || day == sample[1] + 1)
                            foreach (string player in Players.Take(2))
                                Check(market.SetPosition(new RequestContext("H", 1, Protocol.Investment, player), new ShareOrder {
                                    companyId = "AI", targetShares = day == sample[0] ? 10 : 0 }).Success, "hidden live position setup");
                        market.SimulateDay();
                    }
                }
                else foreach (string player in Players.Take(2))
                {
                    Check(market.SaveReservations(new RequestContext("H", 1, Protocol.Info, player), new[] { new Investment {
                        companyId = "AI", intervals = new[] { new Interval { entryDay = sample[0], exitDay = sample[1] } } } }).Success, "hidden reservation setup");
                    Check(market.TradeShares(new RequestContext("H", 1, Protocol.Selection, player), new[] {
                        new ShareOrder { companyId = "AI", targetShares = 10 } }).Success, "hidden share setup");
                }
                market.LockPortfolios(); market.SettleTurn(new RequestContext("H", 1, Protocol.Reveal));
                var detail = market.GetSettlementDetail(1);
                foreach (string player in Players.Take(2))
                {
                    int expected = sample[player == "P1" ? 2 : 3];
                    var profit = detail.Single(p => p.playerId == player).companyProfits.Single(p => p.companyId == "AI");
                    Check(profit.returnBps == expected, "only held hidden days change: losses to 20%, gains to 50%, prelude unchanged");
                    Check(profit.profit == (long)Math.Round(10000m * 10 / 30 * expected / 10000, MidpointRounding.AwayFromZero), "hidden return settles against the ten held slots");
                    Check(market.GetRevealedMarket(new RequestContext("H", 1, Protocol.Reveal)).Single(c => c.companyId == "AI")
                        .investments.Single(p => p.playerId == player).returnBps == expected, "revealed returns match hidden-adjusted settlement");
                }
                var protectedBank = detail.Single(p => p.playerId == "P1").companyProfits.Single(p => p.companyId == "BANK");
                var ordinaryBank = detail.Single(p => p.playerId == "P2").companyProfits.Single(p => p.companyId == "BANK");
                Check(protectedBank.returnBps == ordinaryBank.returnBps && protectedBank.profit == ordinaryBank.profit, "BANK chart return and cash interest are exempt from hidden loss control");
                Check(market.GetWindow(1, "AI", 25, 6).SequenceEqual(new[] { 1000, 0, 1000, 0, 0, 1000 }), "augment never modifies the shared hidden chart");
            }
        }
        private static void Effects()
        {
            var m = MarketService.CreateStockSystem(19); m.BeginTurn(new RequestContext("E", 1, Protocol.Info), Players); m.BeginLiveTrading();
            OwnedAugments(m, "P1").Add("CONCENTRATION"); OwnedAugments(m, "P2").AddRange(new[] { "BIO_RESEARCH", "SPACE_PIONEER" });
            Func<string, string, int, RuleResult> trade = (player, company, count) => m.SetPosition(new RequestContext("E", 1, Protocol.Investment, player), new ShareOrder { companyId = company, targetShares = count });
            Check(trade("P1", "AI", 12).Success && trade("P1", "FOOD", 12).Success && trade("P1", "AUTO", 6).Success, "concentration applies to every company within thirty slots");
            Check(trade("P1", "AI", 13).ErrorCode == "SHARE_LIMIT", "concentration capped at twelve");
            Check(trade("P2", "BIO", 15).Success && trade("P2", "SPACE", 15).Success, "research and pioneer both permit fifteen shares");
            Check(trade("P2", "BIO", 16).ErrorCode == "SHARE_LIMIT" && trade("P3", "BIO", 6).ErrorCode == "INSUFFICIENT_SHARES", "personal and total supply bounds remain atomic");
            var portfolio = m.GetPortfolio("P2");
            Check(portfolio.LimitFor("BIO") == 15 && portfolio.LimitFor("SPACE") == 15 && StockRules.ShareLimit(new[] { "CONCENTRATION", "BIO_RESEARCH" }, "BIO") == 15, "client uses the same strongest limits");
            var info = new SharedInformationService(m); info.GrantTurnCoins(new RequestContext("E", 1, Protocol.Info), Players); info.PrepareRandomInformation(new RequestContext("E", 1, Protocol.Info));
            int before = info.GetCoins("E", "P1"); OwnedAugments(m, "P1").Add("ANALYST");
            info.GrantAnalystCoins(new RequestContext("E", 1, Protocol.Info), "P1"); info.GrantAnalystCoins(new RequestContext("E", 1, Protocol.Info), "P1");
            Check(info.GetCoins("E", "P1") == before + 2 && m.InformationDiscount("P1") == 0, "analyst grants actual coins once per turn");
            m.LockPortfolios(); m.SettleTurn(new RequestContext("E", 1, Protocol.Reveal)); m.BeginTurn(new RequestContext("E", 2, Protocol.Info), Players);
            info.GrantTurnCoins(new RequestContext("E", 2, Protocol.Info), Players); info.GrantAnalystCoins(new RequestContext("E", 2, Protocol.Info), "P1");
            Check(info.GetCoins("E", "P1") == before + 19, "analyst is granted again on the next turn");
            OwnedAugments(m, "P3").Add("HIDDEN_LOSS_CONTROL");
            Check(StockRules.Augments.Contains("HIDDEN_LOSS_CONTROL") && !StockRules.Augments.Contains("CONTRARIAN"), "hidden loss control replaces contrarian in the eight candidates");
            Check(m.AnalysisWindows("P3").Length == 0, "hidden loss control grants no free company information");
            foreach (int days in new[] { 1, 7, 30 })
            {
                var bank = MarketService.CreateStockSystem(1); bank.BeginTurn(new RequestContext("B", 1, Protocol.Info), Players); OwnedAugments(bank, "P1").Add("CASH_INTEREST");
                foreach (string player in Players.Take(2)) Check(bank.SaveReservations(new RequestContext("B", 1, Protocol.Info, player), new[] { new Investment { companyId = "BANK", intervals = new[] { new Interval { entryDay = 1, exitDay = days } } } }).Success, "bank duration setup");
                bank.LockPortfolios(); var board = bank.SettleTurn(new RequestContext("B", 1, Protocol.Reveal));
                Check(board.rows.Single(p => p.playerId == "P1").totalAssets - board.rows.Single(p => p.playerId == "P2").totalAssets == days * 50, "BANK adds exactly half a percent per deposited day up to fifteen percent");
            }
            foreach (bool loss in new[] { false, true })
            foreach (int days in new[] { 9, 10, 15 })
            {
                var library = StockPatternLibrary.CreateDefault(); library.patterns.Clear();
                var line = CustomStockPattern.Preset("AI", 0); line.isBasePattern = true; line.baseShape = BasePatternShape.Authored;
                line.vertices = new List<double> { 0, loss ? -1 : 1 }; line.segmentDays = new List<int> { 30 }; line.amplitudeBps = 3000; line.startDay = 0; library.patterns.Add(line);
                var holding = MarketService.CreateStockSystem(41, library); holding.BeginTurn(new RequestContext("L", 1, Protocol.Info), Players); OwnedAugments(holding, "P1").Add("LONG_TERM");
                foreach (string player in Players.Take(2)) {
                    Check(holding.SaveReservations(new RequestContext("L", 1, Protocol.Info, player), new[] { new Investment { companyId = "AI", intervals = new[] { new Interval { entryDay = 1, exitDay = 5 }, new Interval { entryDay = 11, exitDay = 10 + days - 5 } } } }).Success, "non-contiguous cumulative holding setup");
                    Check(holding.TradeShares(new RequestContext("L", 1, Protocol.Selection, player), new[] { new ShareOrder { companyId = "AI", targetShares = 10 } }).Success, "holding setup");
                }
                holding.LockPortfolios(); holding.SettleTurn(new RequestContext("L", 1, Protocol.Reveal));
                var detail = holding.GetSettlementDetail(1);
                int boosted = detail.Single(p => p.playerId == "P1").companyProfits.Single(p => p.companyId == "AI").returnBps;
                int normal = detail.Single(p => p.playerId == "P2").companyProfits.Single(p => p.companyId == "AI").returnBps;
                Check(boosted == (days >= 10 && !loss ? normal * 3 : normal), "cumulative ten-day threshold triples gains without amplifying losses");
                Check(holding.GetRevealedMarket(new RequestContext("L", 1, Protocol.Reveal)).Single(c => c.companyId == "AI")
                    .investments.Single(p => p.playerId == "P1").returnBps == boosted, "revealed company summary includes the same augment return as settlement");
            }
            foreach (bool qualifies in new[] { false, true }) {
                var diversified = MarketService.CreateStockSystem(42); diversified.BeginTurn(new RequestContext("D", 1, Protocol.Info), Players); OwnedAugments(diversified, "P1").Add("DIVERSIFY");
                var firms = InvestmentRules.CompanyIds.Where(id => id != "BANK").OrderBy(id => diversified.GetWindow(1, id, 30, 1)[0]).Take(3).ToArray();
                Check(diversified.TradeShares(new RequestContext("D", 1, Protocol.Selection, "P1"), firms.Select((id, i) => new ShareOrder { companyId = id, targetShares = qualifies || i == 0 ? 3 : 2 }).ToArray()).Success, "diversification setup");
                diversified.LockPortfolios(); diversified.SettleTurn(new RequestContext("D", 1, Protocol.Reveal));
                foreach (var profit in diversified.GetSettlementDetail(1).Single(p => p.playerId == "P1").companyProfits) {
                    int raw = Math.Max(-10000, diversified.GetWindow(1, profit.companyId, 30, 1)[0]);
                    Check(profit.returnBps == (qualifies && raw < 0 ? raw * 80 / 100 : raw), "diversification requires three sufficiently large holdings and only reduces losses");
                }
            }
        }
    }
}
