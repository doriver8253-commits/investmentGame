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
            public Harness(TimerMode mode = TimerMode.Realtime)
            {
                Info = new SharedInformationService(Market);
                Room = new GameRoom("STOCK", Market, Info, Clock, mode: mode, fixedOrderForFixture: true,
                    settings: new GameSettings(5, 6, 2, enableProgressionFeatures: false));
                for (int i = 1; i <= 4; i++) { Room.Join("c" + i, "Player " + i); Send(i, "SET_READY", new CommandPayload { ready = true }); }
                Send(1, "START_GAME");
            }
            public CommandResult Send(int player, string type, CommandPayload payload = null)
                => Room.ProcessCommand("c" + player, new Command { type = type, turn = Room.Snapshot().turn, requestId = "s" + (++sequence), payload = payload });
            public void FinishInfo()
            {
                FinishPublic();
                for (int player = 1; player <= 4; player++)
                {
                    Check(Room.Snapshot().phase == Protocol.Info, "information stays open until everyone finishes");
                    Check(Send(player, "COMPLETE_PHASE").success, "finish information");
                }
                Check(Room.Snapshot().phase == Protocol.Selection, "four completions start trading immediately");
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
            public CommandResult Trade(int player, string company, int shares) => Send(player, StockRules.Trade,
                new CommandPayload { shareOrders = new[] { new ShareOrder { companyId = company, targetShares = shares } } });
            public void Deadline() { Clock.Seconds = Room.Snapshot().phase == Protocol.Selection ? Room.Snapshot().selectionDeadlineAtServerSeconds : Room.Snapshot().phaseEndsAtServerSeconds; Room.Tick(); }
        }
        public static string[] Run()
        {
            var names = new List<string>();
            Action<string, Action> run = (name, test) => { test(); names.Add("STOCK-SYSTEM/" + name); };
            run("twenty-shares-thirty-slots-public-ownership-and-atomic-draft", Draft);
            run("eight-seven-second-turns-then-private-intervals-and-market-reveal", Timeline);
            run("fifteen-shares-per-pick-multicompany-atomicity-replay-and-reset", PickBudget);
            run("information-reservations-do-not-claim-shares-and-survive-automatic-trading", InformationReservations);
            run("new-turn-snapshots-clear-old-private-data-and-periodic-state-keeps-deadlines", TurnSnapshots);
            run("all-unused-slots-automatically-fill-one-hundred-twenty-bank-shares", Bank);
            run("slot-weighted-multicompany-cash-and-settlement-replay", Settlement);
            run("developer-mode-waits-for-human-and-bots-complete-without-timeouts", DeveloperMode);
            run("bank-reservations-limit-interest-and-idle-slots-return-to-cash", BankIntervals);
            run("augment-candidates-discounts-focus-flexibility-and-priority", Augments);
            run("six-turns-augmentation-schedule-reset-timeouts-and-final-ranking", SixTurns);
            run("turn-start-restores-all-stock-supply-and-bank-preserving-assets-and-history", ResetHoldings);
            run("private-analysis-public-portfolio-copy-and-json-integers", Copies);
            run("all-augment-effect-boundaries-information-ledger-and-long-hold-bonuses", Effects);
            run("planned-early-exit-keeps-streak-bonuses-and-diversify-uses-daily-holdings", HoldingAugmentsUseDailyHoldings);
            return names.ToArray();
        }
        private static void Draft()
        {
            var h = new Harness(); h.Choose(); h.FinishInfo();
            double start = h.Clock.Seconds;
            Check(h.Room.Snapshot().draftOrder.SequenceEqual(Players.Concat(Players)), "A to D repeats twice without reversing");
            Check(h.Trade(2, "AI", 10).errorCode == "NOT_YOUR_TURN", "authenticated turn gate");
            Check(h.Trade(1, "AI", h.Market.GetPortfolio("P1").LimitFor("AI") + 1).errorCode == "SHARE_LIMIT", "individual limit checked with or without concentration");
            Check(h.Trade(1, "AI", 10).success, "first order");
            Check(h.Trade(1, "SPACE", 5).success && h.Trade(1, "FOOD", 1).errorCode == "DRAFT_SHARE_LIMIT", "separate purchases share the fifteen-share budget");
            Check(h.Room.Snapshot().selectionDeadlineAtServerSeconds == start + 7, "purchases never extend the deadline");
            Check(h.Send(1, StockRules.Pass).success, "first player finishes early");
            Check(h.Clock.Seconds == start && h.Trade(2, "AI", 10).success, "second player can immediately trade");
            Check(h.Send(2, StockRules.Pass).success, "second player finishes early");
            Check(h.Trade(3, "AI", 1).errorCode == "INSUFFICIENT_SHARES", "sold out");
            h.Deadline(); Check(h.Send(4, StockRules.Pass).success, "early pass");
            Check(h.Room.Snapshot().currentPlayerId == "P1" && h.Market.GetPortfolio("P1").draftSharesRemaining == 15, "second round starts with a fresh fifteen-share budget");
            Check(h.Trade(1, "FOOD", 10).success && h.Trade(1, "AUTO", 5).success, "second pick invests the remaining fifteen slots");
            for (int p = 1; p <= 4; p++) Check(h.Send(p, StockRules.Pass).success, "second round completion");
            Check(h.Room.Snapshot().phase == Protocol.Investment && !h.Trade(4, "AI", 1).success, "eighth completion starts intervals and locks share trading");
            Check(h.Market.ProcessedDay == 0, "prices are not simulated between players");
            var plan = new[] { new Investment { companyId = "AI", intervals = new[] { new Interval { entryDay = 3, exitDay = 5 } } } };
            Check(h.Send(3, "SAVE_TRADE_PLAN", new CommandPayload { investments = plan }).errorCode == "INVALID_COMPANY", "cannot reserve shares that were not purchased");
            Check(h.Send(1, "SAVE_TRADE_PLAN", new CommandPayload { investments = plan }).success, "owned company intervals saved during the separate investment phase");
            Check(h.Send(1, "SUBMIT_INVESTMENT", new CommandPayload { investments = plan }).success, "share quantities determine allocations without legacy two-company weights");
            Check(h.Market.ProcessedDay == 0, "first confirmation keeps the market private");
            h.Deadline();
            Check(h.Room.Snapshot().phase == Protocol.Reveal && h.Market.ProcessedDay == 30, "remaining players time out into the market reveal");
            Check(h.Market.GetPortfolio("P1").holdings.Single(v => v.companyId == "AI").shares == 0 &&
                h.Market.GetPortfolio("P1").holdings.Single(v => v.companyId == "FOOD").shares == 10, "confirmed interval sells at exit while unspecified holdings stay invested");
        }
        private static void Timeline()
        {
            var h = new Harness(); h.Choose(); h.FinishInfo(); h.Room.DrainMessages();
            double start = h.Clock.Seconds;
            for (int i = 0; i < 8; i++)
            {
                var s = h.Room.Snapshot();
                Check(s.phase == Protocol.Selection && s.currentPlayerId == Players[i % 4], "each player gets two turns in seat order");
                Check(s.selectionDeadlineAtServerSeconds == start + (i + 1) * 7, "each timeout allows seven seconds");
                Check(h.Send(i % 4 + 1, "SAVE_TRADE_PLAN", new CommandPayload { investments = Array.Empty<Investment>() }).success, "own purchase turn can edit reservation drafts");
                Check(h.Send((i + 1) % 4 + 1, "SAVE_TRADE_PLAN", new CommandPayload { investments = Array.Empty<Investment>() }).errorCode == "WRONG_PHASE", "other players cannot edit plans during someone else's purchase turn");
                h.Deadline();
            }
            Check(h.Clock.Seconds == start + 56 && h.Room.Snapshot().phase == Protocol.Investment, "eight timeouts take fifty-six seconds with no intermediate simulation");
            Check(h.Market.ProcessedDay == 0 && !h.Room.DrainMessages().Any(m => m.Message.type == "MARKET_REVEAL"), "market remains hidden until plans are final");
            for (int i = 1; i <= 4; i++)
            {
                Check(h.Room.Snapshot().phase == Protocol.Investment, "wait for all four confirmations");
                Check(h.Send(i, "SUBMIT_INVESTMENT", new CommandPayload { investments = Array.Empty<Investment>() }).success, "empty bank-only portfolio can confirm");
                if (i == 1) Check(h.Send(i, "SAVE_TRADE_PLAN", new CommandPayload { investments = Array.Empty<Investment>() }).errorCode == "ORDER_LOCKED", "confirmed plans cannot change");
            }
            Check(h.Room.Snapshot().phase == Protocol.Reveal && h.Market.ProcessedDay == 30, "all confirmations reveal the full market");
            Check(h.Room.DrainMessages().Count(m => m.Message.type == "MARKET_REVEAL") == 4, "one market reveal per player");
        }
        private static void PickBudget()
        {
            var h = new Harness(); h.Choose(); h.FinishInfo();
            var order = new Command { type = StockRules.Trade, requestId = "split-fifteen", turn = 1, payload = new CommandPayload {
                shareOrders = new[] { new ShareOrder { companyId = "AI", targetShares = 10 }, new ShareOrder { companyId = "SPACE", targetShares = 6 } } } };
            Check(h.Room.ProcessCommand("c1", order).errorCode == "DRAFT_SHARE_LIMIT" && h.Market.GetPortfolio("P1").usedSlots == 0 &&
                h.Market.GetPortfolio("P1").draftSharesRemaining == 15, "oversized multi-firm order changes neither shares nor budget");
            order.requestId = "split-valid"; order.payload.shareOrders[1].targetShares = 5;
            Check(h.Room.ProcessCommand("c1", order).success && h.Room.ProcessCommand("c1", order).success, "ordinary investor can split AI ten and SPACE five and safely replay");
            Check(h.Market.GetPortfolio("P1").usedSlots == 15 && h.Market.GetPortfolio("P1").draftSharesRemaining == 0, "replayed order debits the budget only once");
            Check(h.Trade(1, "AI", 5).success && h.Trade(1, "FOOD", 1).errorCode == "DRAFT_SHARE_LIMIT", "selling cannot refill this pick's budget");
            Check(h.Trade(1, "AI", 10).errorCode == "DRAFT_SHARE_LIMIT", "sell-and-rebuy cannot bypass the cap");
            Check(h.Market.GetShareMarket().Single(c => c.companyId == "BANK").owners[0].shares == 20, "uninvested shares automatically return to BANK");
            h.Send(1, StockRules.Pass);
            Check(h.Trade(2, "FOOD", 1).success, "another player leaves fourteen shares unused in the first pick");
            for (int p = 2; p <= 4; p++) h.Send(p, StockRules.Pass);
            Check(h.Market.GetPortfolio("P1").draftSharesRemaining == 15, "second pick resets the cap");
            Check(h.Send(1, StockRules.Trade, new CommandPayload { shareOrders = new[] {
                new ShareOrder { companyId = "FOOD", targetShares = 5 }, new ShareOrder { companyId = "AUTO", targetShares = 5 },
                new ShareOrder { companyId = "BIO", targetShares = 5 } } }).success, "the per-pick fifteen shares can be spread over three firms");
            Check(h.Trade(1, "AI", 6).errorCode == "DRAFT_SHARE_LIMIT", "unused first-pick capacity does not increase the second-pick cap");
            h.Send(1, StockRules.Pass);
            Check(h.Market.GetPortfolio("P2").draftSharesRemaining == 15 && h.Send(2, StockRules.Trade, new CommandPayload { shareOrders = new[] {
                new ShareOrder { companyId = "AI", targetShares = 10 }, new ShareOrder { companyId = "SPACE", targetShares = 6 } } }).errorCode == "DRAFT_SHARE_LIMIT", "fourteen unused first-pick shares are not carried into the second-pick budget");
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
        private static void InformationReservations()
        {
            var h = new Harness(); h.Choose(); h.FinishPublic();
            var plan = new[] { new Investment { companyId = "AI", intervals = new[] { new Interval { entryDay = 3, exitDay = 5 } } } };
            Check(h.Send(1, "SAVE_TRADE_PLAN", new CommandPayload { investments = plan }).success, "information exploration can reserve an unowned firm");
            Check(h.Market.GetPortfolio("P1").usedSlots == 0 && h.Market.GetShareMarket().Single(c => c.companyId == "AI").owners.Sum(o => o.shares + o.reserved) == 0, "reservation drafts never preempt stock supply");
            Check(h.Market.GetReservations("P1").Single().intervals.Single().entryDay == 3, "private reservation persists on the server for reconnect");
            h.FinishInfo(); Check(h.Trade(1, "AI", 10).success, "automatic quantity input uses the normal share command");
            h.Deadline();
            Check(h.Market.GetPortfolio("P1").usedSlots == 10 && h.Room.Snapshot().currentPlayerId == "P2", "purchases survive the deadline without an apply or finish button");
            while (h.Room.Snapshot().phase == Protocol.Selection) h.Deadline();
            h.Deadline();
            Check(h.Room.Snapshot().phase == Protocol.Reveal && h.Market.GetPortfolio("P1").usedSlots == 0, "saved exploration interval automatically exits at day five");
        }
        private static void Bank()
        {
            var m = MarketService.CreateStockSystem(19); m.BeginTurn(new RequestContext("BANK", 1, Protocol.Info), Players);
            var bank = m.GetShareMarket().Single(c => c.companyId == "BANK");
            Check(bank.totalShares == 120 && bank.owners.Sum(o => o.shares) == 120 && bank.owners.All(o => o.shares == 30), "all four players automatically deposit thirty shares");
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
            decimal mixed=price("BANK",2)/10000m*price("AI",5)/price("AI",2)*price("BANK",30)/price("BANK",5);
            decimal expected=10000m*(mixed/3m+price("BANK",30)/10000m*2m/3m);
            Check(Math.Abs(board.rows.Single(v=>v.playerId=="P1").totalAssets-expected)<=1, "actual entry/exit and BANK valuation");
            Check(m.GetWindow(1,"BANK",1,30).All(v=>Math.Abs(v)<=200),"bounded BANK series");
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
            var h = new Harness(TimerMode.Developer);
            var clients = Enumerable.Range(1, 4).Select(p => new LocalClient(h, p)).ToArray();
            var human = new GameClientController(clients[0]);
            var bots = Enumerable.Range(1, 3).Select(i => new BlackWhale.UI.Dev.DevBot(clients[i], 0, i)).ToArray();
            Action pump = () => {
                foreach (var delivery in h.Room.DrainMessages())
                    clients[int.Parse(delivery.PlayerId.Substring(1)) - 1].Receive(delivery.Message);
            };
            Action wait = () => { h.Clock.Seconds += 1000; h.Room.Tick(); pump(); };
            try
            {
                pump(); wait();
                Check(h.Room.Snapshot().phase == StockRules.AugmentPhase, "developer augment does not time out");
                Check(human.Store.Public.developerMode && !BlackWhale.UI.Presentation.PhaseClock.HasDeadline(human.Store.Public), "developer state survives client copy without deadline");
                human.ChooseAugment(h.Market.GetPortfolio("P1").candidates[0]); pump();
                for (int n = 0; n < 300 && h.Room.Snapshot().phase != Protocol.Result; n++)
                {
                    foreach (var bot in bots) { bot.Update(n); pump(); }
                    var state = h.Room.Snapshot();
                    wait();
                    Check(h.Room.Snapshot().phase == state.phase && h.Room.Snapshot().selectionIndex == state.selectionIndex,
                        "elapsed time alone cannot advance developer phase");
                    if (state.publicSelectionActive && state.publicSelectionPlayerId == "P1")
                    {
                        Check(h.Send(1, "REVEAL_INFORMATION_WINDOW", new CommandPayload { companyId = "SPACE", startDay = Enumerable.Range(1, 26).First(start => !state.publicInformation.Any(v => v.companyId == "SPACE" && start < v.startDay + 5 && v.startDay < start + 5)) }).success, "human chooses public window after elapsed deadline");
                        pump();
                    }
                    else if (state.phase == Protocol.Info && !state.publicSelectionActive ||
                        state.phase == Protocol.Selection && state.currentPlayerId == "P1" || state.phase == Protocol.Investment || state.phase == Protocol.Reveal)
                    {
                        if (human.CanCompleteStep()) { Check(human.CompleteStep() == CommandStatus.Sent, "human side completion"); pump(); }
                    }
                }
                Check(h.Room.Snapshot().phase == Protocol.Result, "three bots finish public picks, information, purchases, investments and reveal");
                for (int n = 0; n < 5; n++) { foreach (var bot in bots) { bot.Update(400 + n); pump(); } }
                wait(); Check(h.Room.Snapshot().phase == Protocol.Result, "result waits for human completion");
                Check(human.CompleteStep() == CommandStatus.Sent, "human completes result"); pump();
                Check(h.Room.Snapshot().turn == 2, "explicit human completion starts next turn");
            }
            finally { human.Dispose(); foreach (var bot in bots) bot.Dispose(); }
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
            decimal expected = 10000m * price(5) / price(2) * price(12) / price(9);
            Check(Math.Abs(board.rows.Single(v => v.playerId == "P1").totalAssets - expected) <= 1, "BANK interest only accrues within reserved intervals");
            Check(board.rows.Single(v => v.playerId == "P2").totalAssets == 10000 + m.GetWindow(1, "BANK", 30, 1)[0], "unconfigured BANK preserves full-period return");
            long nextAssets = board.rows.Single(v => v.playerId == "P1").totalAssets;
            decimal stockFactor = (10000m + m.GetWindow(1, "AI", 5, 1)[0]) / (10000m + m.GetWindow(1, "AI", 2, 1)[0]);
            decimal mixed = 10000m * (stockFactor / 3 + price(5) / price(2) * 2 / 3) * price(12) / price(9);
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
            OwnedAugments(h.Market, "P1").Add("FLEXIBLE"); OwnedAugments(h.Market, "P4").Add("PRIORITY");
            Check(h.Send(1, StockRules.Trade, new CommandPayload { shareOrders = new[] { new ShareOrder { companyId = "AI", targetShares = 10 }, new ShareOrder { companyId = "FOOD", targetShares = 5 } } }).success,
                "flexible investor atomically adjusts two firms");
            Check(h.Send(1, StockRules.Pass).success, "finish flexible purchase");
            Check(h.Send(4, StockRules.Priority).success, "future turn moves ahead without replacing active P2");
            Check(h.Room.Snapshot().currentPlayerId == "P2" && h.Room.Snapshot().draftOrder[2] == "P4", "priority preserves already completed and active picks");
            Check(h.Send(4, StockRules.Priority).errorCode == "NO_PRIORITY_SLOT", "priority cannot replace the active pick");
            var order = new Command { type = StockRules.Trade, requestId = "share-replay", turn = 1, payload = new CommandPayload { shareOrders = new[] { new ShareOrder { companyId = "FOOD", targetShares = 5 } } } };
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
            Check(augmentationTurns.SequenceEqual(new[] { 1, 3, 5 }), "three timed augmentation rounds");
            Check(h.Market.GetPortfolio("P1").usedSlots == 0, "first-turn holdings never carry into later turns through timeouts");
            Check(h.Market.GetPortfolio("P1").augments.Length == 3, "one new augment each round, timeout selects a valid first candidate");
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
            Check(market.Single(c => c.companyId == "BANK").owners.Sum(o => o.shares) == 120, "all four players automatically deposit into BANK");
            Check(m.GetPortfolio("P1").augments.Contains("ANALYST") && m.InformationDiscount("P1") == 2, "earned augments still apply in the new turn");
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
            Check(StockRules.Augments.Length == 12 && StockRules.Augments.All(id => StockRules.Description(id).Length > 0), "all twelve augments defined");
        }
        private static void Effects()
        {
            var m = MarketService.CreateStockSystem(19); m.BeginTurn(new RequestContext("E", 1, Protocol.Info), Players);
            m.BeginAugmentSelection();
            var all = (IDictionary)typeof(MarketService).GetField("stockPlayers", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(m);
            var p1 = all["P1"]; p1.GetType().GetField("Candidates").SetValue(p1, new[] { "CONCENTRATION", "ANALYST", "TREND" });
            Check(m.ChooseAugment(new RequestContext("E", 1, StockRules.AugmentPhase, "P1"), "CONCENTRATION", "BIO").Success, "concentration chooses a real target firm");
            Func<string, string, int, RuleResult> trade = (p, c, q) => m.TradeShares(new RequestContext("E", 1, Protocol.Selection, p), new[] { new ShareOrder { companyId = c, targetShares = q } });
            Check(trade("P1", "BIO", 12).Success && trade("P1", "AI", 11).ErrorCode == "SHARE_LIMIT", "only focused firm has twelve-share limit");
            m.BeginDraftPick("P1");
            Check(trade("P1", "AI", 10).Success && trade("P1", "SPACE", 9).ErrorCode == "PORTFOLIO_FULL" && m.GetPortfolio("P1").usedSlots == 22, "thirty-slot cap remains atomic with concentration across two picks");
            Check(trade("P2", "BIO", 8).Success, "twelve plus eight still respects total supply");
            OwnedAugments(m, "P3").Add("OPPORTUNITY"); OwnedAugments(m, "P3").Add("ANALYST"); OwnedAugments(m, "P3").Add("BIO_RESEARCH");
            Check(trade("P3", "BIO", 1).ErrorCode == "INSUFFICIENT_SHARES" && trade("P3", "BIO", 2).ErrorCode == "INSUFFICIENT_SHARES" && m.InformationDiscount("P3") == 2,
                "opportunity awards two days only once, never fills unavailable stock");
            Check(m.GetPortfolio("P3").insights.Single().probabilityBps == MarketService.BioSuccessChance(19, 1) && m.GetPortfolio("P2").insights.Length == 0, "BIO chance is private and tied to its actual event generator");
            OwnedAugments(m, "P1").Add("TREND"); OwnedAugments(m, "P1").Add("CONTRARIAN");
            m.LockPortfolios(); m.SettleTurn(new RequestContext("E", 1, Protocol.Reveal));
            m.BeginTurn(new RequestContext("E", 2, Protocol.Info), Players);
            Check(m.InformationDiscount("P3") == 4, "analyst and one opportunity coupon stack to four days next info phase");
            var info = new SharedInformationService(m); info.GrantTurnCoins(new RequestContext("E", 2, Protocol.Info), Players);
            for (int i = 0; i < 4; i++) Check(info.RevealWindow(new RequestContext("E", 2, Protocol.Info, Players[i], "p" + i), InvestmentRules.CompanyIds[i], 10, false).Success, "public analysis setup");
            var own = info.BuildPlayerView(new RequestContext("E", 2, Protocol.Info, "P1"), m.GetOwnInvestmentState("P1"));
            var other = info.BuildPlayerView(new RequestContext("E", 2, Protocol.Info, "P2"), m.GetOwnInvestmentState("P2"));
            var analysis = m.AnalysisWindows("P1");
            Check(analysis.All(w => w.dailyChangesBps.Length == 3) && other.companies.All(c => c.visibleWindows.All(w => w.source != "AUGMENT")), "trend and contrarian reveal only three days to their owner");
            var buyContext = new RequestContext("E", 2, Protocol.Info, "P3", "discounted-buy");
            var bought = info.BuyWindow(buyContext, "BIO", 20, 24);
            Check(bought.Success && bought.Payload.paidCoins == 1 && m.InformationDiscount("P3") == 0, "information discount charges only one of five days");
            Check(info.BuyWindow(buyContext, "BIO", 20, 24).Payload.paidCoins == 1 && m.InformationDiscount("P3") == 0, "discount replay does not consume twice");
            if (analysis.Length > 0)
            {
                var w = analysis[0]; var purchased = info.BuyWindow(new RequestContext("E", 2, Protocol.Info, "P1", "analysis-mask"), w.companyId, 1, 5);
                Check(purchased.Success && purchased.Payload.paidCoins == 2, "analysis proof participates in the paid-information union");
            }
            var cash = MarketService.CreateStockSystem(1); cash.BeginTurn(new RequestContext("C", 1, Protocol.Info), Players); OwnedAugments(cash, "P1").Add("CASH_INTEREST"); cash.LockPortfolios();
            Check(cash.SettleTurn(new RequestContext("C", 1, Protocol.Reveal)).rows.Single(v => v.playerId == "P1").totalAssets == 10100 + cash.GetWindow(1, "BANK", 30, 1)[0], "cash bonus is capped at one percent of total assets");
            var holding = MarketService.CreateStockSystem(41);
            for (int t = 1; t <= 3; t++)
            {
                holding.BeginTurn(new RequestContext("H", t, Protocol.Info), Players);
                Check(holding.GetPortfolio("P1").usedSlots == 0, "long-term holdings must be repurchased after each turn reset");
                if (t == 1)
                {
                    OwnedAugments(holding, "P1").AddRange(new[] { "SPACE_PIONEER", "LONG_TERM" });
                }
                Check(holding.TradeShares(new RequestContext("H", t, Protocol.Selection, "P1"), new[] { new ShareOrder { companyId = "SPACE", targetShares = 10 } }).Success, "repurchase the same company each turn");
                int expectedRate = holding.GetWindow(t, "SPACE", 30, 1)[0] + (t >= 2 ? 200 : 0) + (t >= 3 ? 200 : 0);
                holding.LockPortfolios(); holding.SettleTurn(new RequestContext("H", t, Protocol.Reveal));
                Check(holding.GetSettlementDetail(t).Single(v => v.playerId == "P1").companyProfits.Single(v => v.companyId == "SPACE").returnBps == expectedRate, "two-turn SPACE and three-turn long-term bonuses apply at exact boundaries");
            }
            holding.BeginTurn(new RequestContext("H", 4, Protocol.Info), Players);
            holding.LockPortfolios(); holding.SettleTurn(new RequestContext("H", 4, Protocol.Reveal));
            holding.BeginTurn(new RequestContext("H", 5, Protocol.Info), Players);
            Check(holding.TradeShares(new RequestContext("H", 5, Protocol.Selection, "P1"), new[] { new ShareOrder { companyId = "SPACE", targetShares = 10 } }).Success, "repurchase after skipping one turn");
            holding.LockPortfolios(); holding.SettleTurn(new RequestContext("H", 5, Protocol.Reveal));
            Check(holding.GetSettlementDetail(5).Single(p => p.playerId == "P1").companyProfits.Single(c => c.companyId == "SPACE").returnBps == holding.GetWindow(5, "SPACE", 30, 1)[0],
                "skipping a turn breaks the consecutive-investment bonuses");
            var diversified = MarketService.CreateStockSystem(42); diversified.BeginTurn(new RequestContext("D", 1, Protocol.Info), Players); OwnedAugments(diversified, "P1").Add("DIVERSIFY");
            var firms = InvestmentRules.CompanyIds.Where(id => id != "BANK").OrderBy(id => diversified.GetWindow(1, id, 30, 1)[0]).Take(3).ToArray();
            var dc = new RequestContext("D", 1, Protocol.Selection, "P1");
            Check(diversified.TradeShares(dc, new[] { new ShareOrder { companyId = firms[0], targetShares = 10 }, new ShareOrder { companyId = firms[1], targetShares = 5 } }).Success, "diversified first fifteen shares");
            diversified.BeginDraftPick("P1");
            Check(diversified.TradeShares(dc, new[] { new ShareOrder { companyId = firms[1], targetShares = 10 }, new ShareOrder { companyId = firms[2], targetShares = 10 } }).Success, "diversified second fifteen shares");
            diversified.LockPortfolios(); diversified.SettleTurn(new RequestContext("D", 1, Protocol.Reveal));
            foreach (var v in diversified.GetSettlementDetail(1).Single(p => p.playerId == "P1").companyProfits)
            {
                int raw = diversified.GetWindow(1, v.companyId, 30, 1)[0]; Check(v.returnBps == (raw < 0 ? raw * 80 / 100 : raw), "diversification reduces losses and leaves gains unchanged");
            }
        }

        // B: streak bonuses and DIVERSIFY judge what was actually held during the turn, not what is left on day 30.
        private static int CompanyRate(MarketService m, int turn, string company)
            => m.GetSettlementDetail(turn).Single(p => p.playerId == "P1").companyProfits.Single(c => c.companyId == company).returnBps;
        private static void PlayHeldTurn(MarketService m, int turn, Investment[] plan)
        {
            Check(m.TradeShares(new RequestContext("HOLD", turn, Protocol.Selection, "P1"),
                plan.Select(i => new ShareOrder { companyId = i.companyId, targetShares = i.weightBps }).ToArray()).Success, "buy the planned companies");
            Check(m.SaveReservations(new RequestContext("HOLD", turn, Protocol.Investment, "P1"),
                plan.Select(i => new Investment { companyId = i.companyId, intervals = i.intervals }).ToArray()).Success, "save the planned exits");
            m.LockPortfolios(); m.SettleTurn(new RequestContext("HOLD", turn, Protocol.Reveal));
        }
        private static void HoldingAugmentsUseDailyHoldings()
        {
            // weightBps is reused here as "shares to buy" to keep the helper short.
            Func<string, int, int, int, Investment> hold = (id, shares, entry, exit) =>
                new Investment { companyId = id, weightBps = shares, intervals = new[] { new Interval { entryDay = entry, exitDay = exit } } };

            var bonus = MarketService.CreateStockSystem(77); var plain = MarketService.CreateStockSystem(77);
            for (int t = 1; t <= 3; t++)
            {
                var spaceUntilDayFive = new[] { hold("SPACE", 10, 1, 5) };
                bonus.BeginTurn(new RequestContext("HOLD", t, Protocol.Info), Players);
                plain.BeginTurn(new RequestContext("HOLD", t, Protocol.Info), Players);
                if (t == 1) OwnedAugments(bonus, "P1").AddRange(new[] { "SPACE_PIONEER", "LONG_TERM" });
                PlayHeldTurn(bonus, t, spaceUntilDayFive);
                PlayHeldTurn(plain, t, spaceUntilDayFive);
                Check(bonus.GetPortfolio("P1").holdings.Single(h => h.companyId == "SPACE").shares == 0, "planned exit sells every SPACE share before day 30");
                Check(bonus.GetPortfolio("P1").holdings.Single(h => h.companyId == "SPACE").consecutiveTurns == t, "early planned exit still counts as an invested turn");
                int extra = (t >= 2 ? 200 : 0) + (t >= 3 ? 200 : 0);
                Check(CompanyRate(bonus, t, "SPACE") == CompanyRate(plain, t, "SPACE") + extra, "SPACE +2%p from turn two and long-term +2%p from turn three survive early exits");
            }

            int seed = Enumerable.Range(1, 200).First(s => {
                var probe = MarketService.CreateStockSystem(s); probe.BeginTurn(new RequestContext("HOLD", 1, Protocol.Info), Players);
                return new[] { "AI", "FOOD", "AUTO" }.Count(id => probe.GetWindow(1, id, 5, 1)[0] < 0) >= 2; });
            Func<bool, Investment[], MarketService> diversifyGame = (owned, plan) => {
                var m = MarketService.CreateStockSystem(seed); m.BeginTurn(new RequestContext("HOLD", 1, Protocol.Info), Players);
                if (owned) OwnedAugments(m, "P1").Add("DIVERSIFY");
                PlayHeldTurn(m, 1, plan); return m; };

            var together = new[] { hold("AI", 3, 1, 5), hold("FOOD", 3, 1, 5), hold("AUTO", 3, 1, 5) };
            var withAugment = diversifyGame(true, together); var without = diversifyGame(false, together);
            Check(withAugment.GetPortfolio("P1").usedSlots == 0, "all three companies were sold by planned exits before settlement");
            foreach (var id in new[] { "AI", "FOOD", "AUTO" })
            {
                int raw = CompanyRate(without, 1, id);
                Check(CompanyRate(withAugment, 1, id) == (raw < 0 ? raw * 80 / 100 : raw), "three companies held together still reduce losses after early exits");
            }
            var oneAtATime = new[] { hold("AI", 3, 1, 5), hold("FOOD", 3, 6, 10), hold("AUTO", 3, 11, 15) };
            withAugment = diversifyGame(true, oneAtATime); without = diversifyGame(false, oneAtATime);
            foreach (var id in new[] { "AI", "FOOD", "AUTO" })
                Check(CompanyRate(withAugment, 1, id) == CompanyRate(without, 1, id), "holding companies one after another is not diversification");
        }
    }
}
