using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using BlackWhale.Bots;
using BlackWhale.Flow;
using BlackWhale.Network;
using BlackWhale.Rules;
using BlackWhale.Rules.Information;
using BlackWhale.Shared;
using BlackWhale.UI.Dev;
using BlackWhale.UI.State;

namespace BlackWhale.Tests
{
    public static class NextAValidation
    {
        // Server-side validation evidence only; never included in game client messages/assets.
        public static readonly List<string> BotCommandEvidence = new List<string>();
        private static void Check(bool value, string message) => RoomValidation.Check(value, "NEXT-A: " + message);
        private sealed class Harness
        {
            public readonly TestClock Clock = new TestClock();
            public readonly MarketService Market;
            public readonly SharedInformationService Info;
            public readonly GameRoom Room;
            public readonly Dictionary<string, string> Tokens;
            private int counter;
            public Harness(GameSettings settings = null, bool manual = false)
            {
                Market = MarketService.CreateRandom(321); Info = new SharedInformationService(Market);
                Room = new GameRoom("NEXT", Market, Info, Clock, manual ? TimerMode.ManualForFixture : TimerMode.Realtime, true, settings);
                for (int i = 1; i <= 4; i++)
                { Room.Join("c" + i, "Player " + i); Send(i, "SET_READY", new CommandPayload { ready = true }); }
                Send(1, "START_GAME");
                Tokens = Room.DrainMessages().Where(d => d.Message.type == "WELCOME").ToDictionary(d => d.PlayerId, d => d.Message.session.reconnectToken);
            }
            public CommandResult Send(int player, string type, CommandPayload payload = null)
                => Room.ProcessCommand("c" + player, new Command { type = type, requestId = "test-" + ++counter,
                    turn = Room.Snapshot().turn, payload = payload });
            public void Start() { if (Room.Snapshot().phase == Protocol.AbilitySelection) Send(1, "COMPLETE_PHASE"); }
            public void FinishPublic()
            {
                while (Room.Snapshot().publicSelectionActive)
                {
                    var state = Room.Snapshot();
                    string company = Market.GetCompanyIds().First(id => Enumerable.Range(1, 26).Any(d =>
                        !InformationWindowRules.TooCloseToPublic(state, id, d)));
                    int start = Enumerable.Range(1, 26).First(d => !InformationWindowRules.TooCloseToPublic(state, company, d));
                    Check(Send(int.Parse(state.publicSelectionPlayerId.Substring(1)), "REVEAL_INFORMATION_WINDOW", new CommandPayload { companyId = company, startDay = start }).success,
                        "finish public selections");
                }
            }
        }
        public static async Task<string[]> Run(IWireCodec codec)
        {
            BotCommandEvidence.Clear();
            var passed = new List<string>();
            Action<string, Action> run = (name, test) => { test(); passed.Add(name); };
            run("T01-T03/90-second-receipt-boundaries-and-replay", Timing);
            run("T03/early-completion-and-full-160-second-timeline", Timeline);
            run("pause/frozen-deadlines-command-guard-resume-and-repeat", Pause);
            run("B02-B03-B05-B07/private-known-endpoints-seeded-order-validation", Strategies);
            run("B04/lobby-difficulty-frozen-after-start", Difficulty);
            run("B01-B06/pending-failure-replay-deadline-and-order-races", BotFailures);
            run("B02/hidden-server-values-never-affect-identical-private-input", HiddenValues);
            run("N01-N03/ten-turn-automatic-timeline-rotation-and-ledgers", TenTurns);
            run("N04-N06/auction-cost-replay-reward-and-final-seats", Auctions);
            run("N04/exact-auction-receipt-deadline-and-late-replay", AuctionDeadline);
            run("N07/three-candidates-choice-default-and-reconnect", Abilities);
            run("N03/final-ranking-assets-coins-best-return-initial-order", FinalRanking);
            run("N02-R02/market-turn-isolation-and-return-copy", MarketGeneration);
            run("N11/private-history-query-keeps-live-state-and-works-after-end", History);
            run("D/live-screens-private-dto-mapping-and-server-final-result", LiveScreens);
            run("D/private-trade-plans-ranking-validation-order-copy-and-turn-reset", TradePlans);
            run("N04-N07/wire-integers-and-deep-copies", () => Wire(codec));
            foreach (int botCount in new[] { 3, 2, 0 })
            {
                await TcpTenTurns(codec, botCount);
                passed.Add("N01-R03/TCP-10-turns-scripted-humans-" + (4 - botCount) + "-bots-" + botCount);
            }
            return passed.ToArray();
        }
        private static void LiveScreens()
        {
            var state = new PublicState { turn = 0, phase = Protocol.AbilitySelection };
            var view = new PlayerView { turn = 0, ability = new AbilityState {
                candidates = new[] { "SYNDICATE", "INFORMATION_BROKER", "CORPORATE_RAIDER" }, selectedId = "" },
                inventory = new[] { "INFORMATION_STEAL", "FREE_PASS" } };
            var ability = BlackWhale.UI.Presentation.LiveScreenModels.Ability(state, view, "INFOBROKER", true, 0);
            Check(ability.Candidates.SequenceEqual(new[] { "OPERATOR", "INFOBROKER", "RAIDER" }) && ability.Pending && !ability.Confirmed && !ability.TimeLimited,
                "candidate mapping preserves pending and unlimited deadline");
            Check(BlackWhale.UI.Presentation.LiveScreenModels.ServerAbilityId(ability.Selected) == "INFORMATION_BROKER" && view.ability.selectedId == "",
                "draft sends server id without optimistic server mutation");
            view.ability.selectedId = "INFORMATION_BROKER"; view.ability.freeInformationCredits = 1;
            Check(BlackWhale.UI.Presentation.LiveScreenModels.Ability(state, view, "RAIDER", false, 0).Selected == "INFOBROKER", "confirmed server selection dominates draft");
            state.turn = 9; state.phase = Protocol.Auction; view.turn = 9; view.assets = 10000;
            view.auction = new AuctionState { maximumBid = 987, confirmed = true, bidAmount = 321, useFreePass = true, seatPreferences = new[] { "D", "C", "B", "A" } };
            var auction = BlackWhale.UI.Presentation.LiveScreenModels.Auction(state, view, null, false, 12);
            Check(auction.MaxBid == 987 && auction.Submitted && auction.Bid == 321 && auction.HasFreePass && auction.UseFreePass && auction.SubmittedCount == -1,
                "server maximum and private bid only, no fabricated other submission count");
            auction.SeatPreference[0] = "tampered";
            Check(view.auction.seatPreferences[0] == "D", "screen cannot mutate private server DTO");
            var inventory = BlackWhale.UI.Presentation.LiveScreenModels.Inventory(view, state.phase, 1);
            Check(inventory.Items.SequenceEqual(new[] { "STEAL", "FREEPASS" }) && !inventory.UsageEnabled && inventory.SelectedSlot == 1,
                "server inventory maps to actual item cards with unsupported actions disabled");
            Check(!BlackWhale.UI.Presentation.LiveScreenModels.Shop(view, state.phase).Enabled, "unsupported shop cannot pretend to purchase");
            state.turn = 10;
            state.events = new[] { new PublicEvent { turn = 9, kind = "AUCTION_SEAT", playerId = "P2", detail = "B" },
                new PublicEvent { turn = 9, kind = "AUCTION_SEAT", playerId = "P1", detail = "A" } };
            var recovered = BlackWhale.UI.Presentation.LiveScreenModels.LastAuction(state, new PlayerView { turn = 10 });
            Check(recovered.Turn == 9 && !recovered.OwnBidKnown && !recovered.Result.AmountsPublic && !recovered.ShowBidLimit
                && recovered.Result.Seats.Select(s => s.Seat).SequenceEqual(new[] { "A", "B" }), "reconnected result uses public seats without inventing private amounts");
            var board = new Scoreboard { turn = 10, gameComplete = true, winnerPlayerId = "P2", rows = new[] {
                new ScoreRow { playerId = "P1", rank = 1, totalAssets = 10000 }, new ScoreRow { playerId = "P2", rank = 1, totalAssets = 10000 } } };
            state.phase = Protocol.Result;
            var final = BlackWhale.UI.Presentation.LiveScreenModels.Board(board, state, "P1");
            Check(final.Final && final.WinnerId == "P2", "server final result preserved even before ENDED, without inferring tied winner");
        }

        private static void Pause()
        {
            var h = new Harness(); h.Start();
            h.Clock.Seconds = 12; h.Room.Tick();
            var before = h.Room.Snapshot();
            Check(h.Room.SetPaused(true) && !h.Room.SetPaused(true), "pause is idempotent");
            h.Clock.Seconds = 612; h.Room.Tick();
            var frozen = h.Room.Snapshot();
            Check(frozen.paused && frozen.phase == before.phase && frozen.serverElapsedSeconds == 12
                && frozen.phaseEndsAtServerSeconds == before.phaseEndsAtServerSeconds, "wall time cannot advance a paused room");
            var command = RoomValidation.Command("COMPLETE_PHASE", "paused-complete", 1);
            Check(h.Room.ProcessCommand("c1", command).errorCode == ErrorCodes.GamePaused, "server refuses state mutations while paused");
            var uiClock = new BlackWhale.UI.Presentation.PhaseClock(); uiClock.Sync(frozen.serverElapsedSeconds, 1);
            Check(uiClock.RemainingSeconds(frozen, 10000) == (int)Math.Ceiling(BlackWhale.UI.Presentation.PhaseClock.DeadlineOf(frozen) - frozen.serverElapsedSeconds), "client countdown stays frozen without repeated snapshots");
            var wireCopy = DtoCopy.Copy(frozen);
            Check(wireCopy.paused, "pause survives client snapshot copy");
            using (var client = new BotClient())
            using (var controller = new GameClientController(client))
            {
                var bot = new DevBot(client, 0);
                client.Receive(new ServerMessage { type = "WELCOME", session = new Session { playerId = "P1" } });
                client.Receive(new ServerMessage { type = "PUBLIC_STATE", state = frozen });
                client.Receive(new ServerMessage { type = "PLAYER_VIEW", playerView = new PlayerView { turn = 1, recipientPlayerId = "P1", coins = 3 } });
                Check(controller.BuyInformation("AI", 0) == CommandStatus.BlockedLocally && controller.LastLocalBlockCode == ErrorCodes.GamePaused
                    && !controller.CanFinishPhase(), "client blocks purchases and completion during pause");
                int count = client.Sent.Count;
                bot.Update(10000);
                Check(client.Sent.Count == count, "paused bot sends no command");
                bot.Dispose();
            }
            Check(h.Room.SetPaused(false) && h.Room.Snapshot().serverElapsedSeconds == 12, "resume excludes paused wall time");
            h.FinishPublic();
            Check(h.Room.ProcessCommand("c1", command).success, "paused request can be retried after resume");
            h.Clock.Seconds = 689.999; h.Room.Tick();
            Check(h.Room.Snapshot().phase == Protocol.Info, "deadline is preserved after resume");
            h.Clock.Seconds = 690; h.Room.Tick();
            Check(h.Room.Snapshot().phase == Protocol.Selection, "exact deadline advances after resumed remaining time");
            h.Clock.Seconds = 691; h.Room.SetPaused(true);
            h.Clock.Seconds = 1691; h.Room.Tick();
            Check(h.Room.Snapshot().selectionIndex == 0 && h.Room.Snapshot().serverElapsedSeconds == 91, "selection clock also freezes");
            h.Room.SetPaused(false); h.Clock.Seconds = 1694; h.Room.Tick();
            Check(h.Room.Snapshot().selectionIndex == 1, "selection resumes at its original four-second boundary");
            h.Room.SetPaused(true); h.Room.Shutdown();
            Check(!h.Room.Snapshot().paused && h.Room.Snapshot().phase == Protocol.Ended, "host can end a paused room");
        }
        private static void TradePlans()
        {
            var client = new BotClient(); using var controller = new GameClientController(client);
            client.Receive(new ServerMessage { type = "WELCOME", session = new Session { playerId = "P1" } });
            client.Receive(new ServerMessage { type = "PUBLIC_STATE", state = new PublicState { turn = 1, phase = Protocol.Info } });
            var plans = controller.Store.TradePlans;
            Check(plans.SetRank("AI", 1) && plans.SetRank("SPACE", 2) && plans.SetRank("SPACE", 1), "ranks can be assigned and swapped");
            Check(plans.Get("AI").Rank == 2 && plans.Get("SPACE").Rank == 1 && plans.Get("SPACE").Favorite, "unique personal priorities");
            Check(plans.TryAddInterval("AI", 0, 7) == "" && plans.TryAddInterval("AI", 8, 15) == "", "seven-day planned intervals require a non-overlapping gap");
            Check(plans.TryAddInterval("AI", 16, 23) == ErrorCodes.TooManyIntervals && plans.Get("AI").Intervals.Length == 2, "failed extra interval leaves plans unchanged");
            Check(plans.TryAddInterval("SPACE", 26, 31) != "" && plans.TryAddInterval("SPACE", 10, 17) == "", "day and holding rules enforced before saving");
            Check(plans.TryAddInterval("SPACE", 14, 21) == ErrorCodes.OverlappingIntervals && plans.Get("SPACE").Intervals.Length == 1, "overlap cannot alter saved plan");
            var copy = plans.Get("AI"); copy.Intervals[0].entryDay = 20;
            Check(plans.Get("AI").Intervals[0].entryDay == 0, "returned plans are independent copies");
            var draft = BlackWhale.UI.Presentation.InvestmentDraft.FromSelections(new[] { "AI", "SPACE" }, plans);
            Check(draft.Validate() == "" && draft.FirstIntervals.Count == 2 && draft.SecondIntervals[0].entryDay == 10, "only selected companies are imported into the order draft");
            draft.FirstIntervals[0].entryDay = 1;
            Check(plans.Get("AI").Intervals[0].entryDay == 0 && client.Sent.Count == 0, "editing notes or draft never sends commands or mutates saved plan");
            var view = new PlayerView { turn = 1, ownInvestments = new[] { new Investment { companyId = "AI", intervals = new[] { new Interval { entryDay = 12, exitDay = 17 } } } } };
            Check(plans.ChartIntervals(view, 1, "AI", true)[0].entryDay == 12 && plans.ChartIntervals(view, 1, "SPACE", true).Length == 0,
                "locked charts show actual orders, not tentative or unselected-company plans");
            var other = new ClientStateStore(); other.SetPublicState(new PublicState { turn = 1, phase = Protocol.Info });
            Check(other.TradePlans.Get("AI").Intervals.Length == 0 && other.TradePlans.Get("AI").Rank == 0, "client plans are private");
            client.Receive(new ServerMessage { type = "PUBLIC_STATE", state = new PublicState { turn = 1, phase = Protocol.Selection } });
            Check(plans.Get("AI").Intervals.Length == 2, "phase transition retains this turn's plans");
            client.Receive(new ServerMessage { type = "PUBLIC_STATE", state = new PublicState { turn = 2, phase = Protocol.Info } });
            Check(plans.Turn == 2 && plans.Get("AI").Intervals.Length == 0 && plans.Get("AI").Rank == 0, "new market turn clears old plans");
        }
        private static void Timing()
        {
            foreach (double receipt in new[] { 89.999, 90.0 })
            {
                var h = new Harness(new GameSettings(turns: 1, enableProgressionFeatures: false));
                Check(h.Room.Snapshot().phaseEndsAtServerSeconds == 90, "INFO deadline");
                var hidden = h.Market.GetCompanyIds().SelectMany(id => Enumerable.Range(0, 6).Select(b => h.Market.GetBundle(1, id, b)))
                    .First(b => !b.IsPublic);
                var command = new Command { type = "BUY_INFORMATION_WINDOW", requestId = "boundary", turn = 1,
                    payload = new CommandPayload { companyId = "BIO", startDay = 26, endDay = 30 } };
                h.Clock.Seconds = 100; // main thread handles a packet received earlier
                var result = h.Room.ProcessCommandAt("c1", command, receipt);
                Check(result.success == (receipt < 90), "receipt, not processing time");
                if (receipt == 90) Check(result.errorCode == "DEADLINE_EXCEEDED" && h.Info.GetCoins("NEXT", "P1") == 15, "exact deadline unchanged");
                h.Clock.Seconds = 200; h.Room.Tick();
                var replay = h.Room.ProcessCommandAt("c1", command, 200);
                Check(replay.success == result.success && replay.errorCode == result.errorCode, "replay before ended/deadline");
            }
        }
        private static void Timeline()
        {
            var early = new Harness(new GameSettings(turns: 1, enableProgressionFeatures: false));
            early.Clock.Seconds = 20;
            early.Room.Tick(); early.FinishPublic();
            for (int p = 1; p <= 4; p++) Check(early.Send(p, "COMPLETE_PHASE").success, "early completion");
            Check(early.Room.Snapshot().selectionDeadlineAtServerSeconds == 24, "new pick deadline");
            var h = new Harness(new GameSettings(turns: 1, enableProgressionFeatures: false));
            var times = new[] { 90, 122, 145, 155, 160 };
            var phases = new[] { Protocol.Selection, Protocol.Investment, Protocol.Reveal, Protocol.Result, Protocol.Ended };
            for (int i = 0; i < times.Length; i++)
            { h.Clock.Seconds = times[i]; h.Room.Tick(); Check(h.Room.Snapshot().phase == phases[i], "160 timeline " + times[i]); }
            Check(h.Market.GetScoreboard(1) != null, "settled real B");
        }
        private static PlayerView StrategyView(int aiExit, int bankExit)
        {
            return new PlayerView { turn = 1, coins = 3, selectedCompanies = new[] { "AI", "BANK" },
                companies = new[] { "AI", "BANK" }.Select(id => new VisibleCompany { companyId = id,
                    visibleBundles = new[] { new VisibleBundle { bundleIndex = 0, startDay = 1, source = "PUBLIC",
                        dailyChangesBps = new[] { 0, 0, 0, 0, 0, 0, 0, id == "AI" ? aiExit : bankExit } } } }).ToArray() };
        }
        private static void Strategies()
        {
            var a = StrategyView(7000, 100);
            var b = StrategyView(-4000, 500);
            var state = new PublicState { companySeats = new[] { new CompanySeat { companyId = "AI", remainingSeats = 1 }, new CompanySeat { companyId = "BANK", remainingSeats = 1 } } };
            foreach (var difficulty in new[] { BotDifficulty.Normal, BotDifficulty.Hard })
            {
                a.selectedCompanies = b.selectedCompanies = Array.Empty<string>();
                var first = new InformationBotStrategy(4, new BotPolicy(difficulty));
                var second = new InformationBotStrategy(4, new BotPolicy(difficulty));
                Check(first.SelectCompany(a, state) == "AI" && second.SelectCompany(b, state) == "BANK", "known endpoints change decision");
            }
            foreach (BotDifficulty difficulty in Enum.GetValues(typeof(BotDifficulty)))
            {
                var policy = new BotPolicy(difficulty);
                Check(policy.PurchaseBudget == (int)difficulty + 1 && policy.MaxIntervals == (difficulty == BotDifficulty.Hard ? 2 : 1), "difficulty policy");
                a.selectedCompanies = new[] { "AI", "BANK" };
                var first = new InformationBotStrategy(99, policy).BuildOrder(a);
                var second = new InformationBotStrategy(99, policy).BuildOrder(DtoCopy.Copy(a));
                Check(first.Zip(second, (x, y) => x.companyId == y.companyId && x.weightBps == y.weightBps &&
                    x.intervals.Zip(y.intervals, (u, v) => u.entryDay == v.entryDay && u.exitDay == v.exitDay).All(v => v)).All(v => v), "seed deterministic");
                Check(OrderValidator.Validate(first, a.selectedCompanies, new InvestmentRules()) == "", "B accepts generated order");
            }
            var empty = new PlayerView { selectedCompanies = new[] { "AI", "BANK" }, companies = Array.Empty<VisibleCompany>() };
            var fallback = new InformationBotStrategy(2, new BotPolicy(BotDifficulty.Hard)).BuildOrder(empty);
            Check(fallback.All(i => i.weightBps == 5000 && i.intervals[0].entryDay == 0 && i.intervals[0].exitDay == 7), "unknown endpoints fallback");
            var days = InformationBotStrategy.KnownDays(a.companies[0]); Check(days[0] == 0 && !days[9].HasValue, "unknown != zero");
            a.selectedCompanies = new[] { "AI" }; state.companySeats[1].remainingSeats = 0;
            Check(new InformationBotStrategy(4, new BotPolicy(BotDifficulty.Normal)).SelectCompany(a, state) == null, "capacity and duplicate filtered");
        }
        private static void Difficulty()
        {
            var market = new MarketService(new SeededMarketProvider(3).Get);
            var room = new GameRoom("difficulty", market, new SharedInformationService(market), new TestClock());
            Check(room.SetBotDifficulty("Hard") && room.Snapshot().botDifficulty == "Hard", "lobby setting");
            Check(!room.SetBotDifficulty("Impossible"), "invalid difficulty");
            var h = new Harness(); Check(!h.Room.SetBotDifficulty("Easy"), "frozen after start");
        }
        private sealed class BotClient : IGameClient
        {
            public readonly List<Command> Sent = new List<Command>();
            public event Action<ServerMessage> MessageReceived;
            public void SendCommand(Command c) => Sent.Add(c);
            public void Pump() { }
            public void Dispose() { }
            public void Receive(ServerMessage m) => MessageReceived?.Invoke(m);
            public void Reply(Command c, bool success, ResultPayload payload = null) => Receive(new ServerMessage {
                type = "COMMAND_RESULT", response = new CommandResult { type = c.type + "_RESULT", requestId = c.requestId,
                    turn = c.turn, success = success, errorCode = success ? "" : "NOT_ENOUGH_COINS", payload = payload } });
        }
        private static void BotFailures()
        {
            var client = new BotClient(); var bot = new DevBot(client, 0);
            client.Receive(new ServerMessage { type = "WELCOME", session = new Session { playerId = "P2" } });
            client.Receive(new ServerMessage { type = "PUBLIC_STATE", state = new PublicState { turn = 1, phase = Protocol.Info, phaseEndsAtServerSeconds = 90 } });
            var view = StrategyView(10, 5); client.Receive(new ServerMessage { type = "PLAYER_VIEW", playerView = view });
            bot.Update(0); bot.Update(1);
            Check(client.Sent.Count == 1 && client.Sent[0].type == "BUY_INFORMATION", "one pending purchase, no finish");
            var first = client.Sent[0]; client.Reply(first, false); client.Reply(first, false);
            bot.Update(2); Check(client.Sent.Count == 2 && client.Sent[1].type == "BUY_INFORMATION", "bounded new candidate after rejection");
            client.Reply(client.Sent[1], false); bot.Update(3); bot.Update(4);
            Check(client.Sent.Count(c => c.type == "BUY_INFORMATION") == 2 && client.Sent.Count(c => c.type == "COMPLETE_PHASE") == 1, "budget and completion bounded");
            view.selectedCompanies = new[] { "AI", "BANK" };
            client.Receive(new ServerMessage { type = "PUBLIC_STATE", state = new PublicState { turn = 1, phase = Protocol.Investment, phaseEndsAtServerSeconds = 113 } });
            client.Receive(new ServerMessage { type = "PLAYER_VIEW", playerView = view });
            bot.Update(5); bot.Update(6);
            var order = client.Sent.Last(); Check(order.type == "SUBMIT_INVESTMENT" && client.Sent.Count(c => c.type == order.type) == 1, "one pending order");
            client.Reply(order, false); bot.Update(7); var fallback = client.Sent.Last();
            Check(fallback.payload.investments.All(i => i.weightBps == 5000 && i.intervals[0].entryDay == 0 && i.intervals[0].exitDay == 7), "rejected order falls back");
            client.Reply(fallback, true, new ResultPayload { automatic = true, locked = true, investments = fallback.payload.investments });
            bot.Update(8); Check(client.Sent.Count(c => c.type == "SUBMIT_INVESTMENT") == 2, "automatic lock suppresses further orders");
            bot.Dispose();
            var near = new BotClient(); var nearBot = new DevBot(near, 0);
            near.Receive(new ServerMessage { type = "WELCOME", session = new Session { playerId = "P2" } });
            near.Receive(new ServerMessage { type = "PUBLIC_STATE", state = new PublicState { turn = 1, phase = Protocol.Info, serverElapsedSeconds = 89, phaseEndsAtServerSeconds = 90 } });
            near.Receive(new ServerMessage { type = "PLAYER_VIEW", playerView = view }); nearBot.Update(0);
            Check(near.Sent.Single().type == "COMPLETE_PHASE", "deadline fallback no purchase"); nearBot.Dispose();
        }
        private static void HiddenValues()
        {
            var source = new SeededMarketProvider(81).Get(1);
            var altered = new SeededMarketProvider(81).Get(1);
            foreach (var c in altered.companies)
                for (int d = 1; d <= 30; d++) if (!c.publicBundleIndices.Contains((d - 1) / 5)) c.dailyChangesBps[d] = -c.dailyChangesBps[d] + 100;
            Func<MarketData, PlayerView> privateView = data =>
            {
                var market = new MarketService(data); var info = new SharedInformationService(market);
                var context = new RequestContext("hidden", 1, Protocol.Info, "P1");
                market.BeginTurn(context, new[] { "P1", "P2", "P3", "P4" }); info.GrantTurnCoins(context, new[] { "P1", "P2", "P3", "P4" });
                return info.BuildPlayerView(context, market.GetOwnInvestmentState("P1"));
            };
            var first = privateView(source); var second = privateView(altered);
            var state = new PublicState { companySeats = source.companies.Select(c => new CompanySeat { companyId = c.companyId, remainingSeats = 2 }).ToArray() };
            foreach (BotDifficulty difficulty in Enum.GetValues(typeof(BotDifficulty)))
            {
                var a = new InformationBotStrategy(42, new BotPolicy(difficulty)); var b = new InformationBotStrategy(42, new BotPolicy(difficulty));
                var pa = a.NextPurchase(first, new HashSet<string>()); var pb = b.NextPurchase(second, new HashSet<string>());
                Check(pa.CompanyId == pb.CompanyId && pa.BundleIndex == pb.BundleIndex && a.SelectCompany(first, state) == b.SelectCompany(second, state), "hidden-value isolation " + difficulty);
            }
        }
        private static void TenTurns()
        {
            var h = new Harness(new GameSettings(abilitySelectionSeconds: 7));
            h.Clock.Seconds = 7; h.Room.Tick();
            Check(h.Room.Snapshot().phase == Protocol.Info && h.Room.Snapshot().phaseEndsAtServerSeconds == 97, "separate ability deadline");
            double start = 7;
            for (int t = 1; t <= 10; t++)
            {
                var state = h.Room.Snapshot(); Check(state.turn == t && state.selectionIndex == 0, "fresh turn " + t);
                Check(h.Market.GetSelectedCompanies("P1").Length == 0 && !h.Market.IsOrderLocked("P1"), "selections/orders reset");
                if (t <= 8) Check(state.seatPlayerIds[0] == "P" + ((t - 1) % 4 + 1), "seat rotation " + t);
                int duration = t == 3 || t == 6 || t == 9 ? 175 : 160;
                h.Clock.Seconds = start + duration - 0.001; h.Room.Tick();
                Check(h.Room.Snapshot().turn == t && h.Room.Snapshot().phase == Protocol.Result, "result before next turn");
                h.Clock.Seconds = start + duration; h.Room.Tick(); start += duration;
                Check(h.Room.Snapshot().phase == (t == 10 ? Protocol.Ended : (t + 1 == 3 || t + 1 == 6 || t + 1 == 9) ? Protocol.Auction : Protocol.Info), "continues through ten");
            }
            Check(h.Market.GetScoreboard(10).gameComplete && !string.IsNullOrEmpty(h.Market.GetScoreboard(10).winnerPlayerId), "final winner");
            Check(h.Market.GetScoreboard(10).rows.All(r => r.returnHistory.Length == 11), "ten results");
            Check(h.Info.GetCoins("NEXT", "P1") >= 200, "15/25 coin carry");
        }
        private static void Auctions()
        {
            var h = new Harness(manual: true); h.Start();
            while (h.Room.Snapshot().turn < 3) h.Room.AdvanceManualPhase();
            Check(h.Room.Snapshot().phase == Protocol.Auction && h.Room.Snapshot().phaseEndsAtServerSeconds == 15, "turn3 auction 15s");
            long before = h.Market.GetTurnStartAssets("P1");
            Check(h.Send(1, "SUBMIT_BID", new CommandPayload { bidAmount = before / 10 + 1 }).errorCode == "INVALID_BID", "bid cap");
            var cmd = new Command { type = "SUBMIT_BID", requestId = "secret", turn = 3, payload = new CommandPayload { bidAmount = 10 } };
            Check(h.Room.ProcessCommand("c1", cmd).success && h.Room.ProcessCommand("c1", cmd).success, "bid replay");
            Check(h.Send(1, "SUBMIT_BID", new CommandPayload { bidAmount = 11 }).errorCode == "BID_LOCKED", "confirmed immutable");
            for (int p = 2; p <= 4; p++) h.Send(p, "SUBMIT_BID", new CommandPayload());
            Check(h.Room.Snapshot().phase == Protocol.Info, "early all confirmed");
            Check(h.Market.GetOwnInvestmentState("P1").TotalAssets == before - 10, "winner cost");
            var view = new PlayerView(); h.Info.PopulateView(new RequestContext("NEXT", 3, Protocol.Info, "P1"), view);
            Check(view.inventory.Contains("BLACKBOX"), "auction reward");
            while (h.Room.Snapshot().turn < 6) h.Room.AdvanceManualPhase();
            for (int p = 1; p <= 4; p++) h.Send(p, "SUBMIT_BID", new CommandPayload { bidAmount = p == 2 ? 10 : 0 });
            h.Info.PopulateView(new RequestContext("NEXT", 6, Protocol.Info, "P2"), view);
            Check(view.inventory.Contains("FREE_PASS"), "turn six free pass");
            while (h.Room.Snapshot().turn < 9) h.Room.AdvanceManualPhase();
            var prefs = new[] { "D", "C", "B", "A" };
            long[] bids = { 100, 90, 10, 20 };
            var starting = Enumerable.Range(1, 4).Select(p => h.Market.GetTurnStartAssets("P" + p)).ToArray();
            for (int p = 1; p <= 4; p++) Check(h.Send(p, "SUBMIT_BID", new CommandPayload {
                bidAmount = bids[p - 1], seatPreferences = prefs, useFreePass = p == 2 }).success, "nine bid");
            var seats = h.Room.Snapshot().seatPlayerIds;
            Check(seats.Distinct().Count() == 4 && h.Room.Snapshot().phase == Protocol.Info, "nine seats assigned");
            Check(seats[3] == "P2", "free pass 90*125 beats 100*100 and takes preferred D");
            for (int p = 1; p <= 4; p++) Check(h.Market.GetOwnInvestmentState("P" + p).TotalAssets == starting[p - 1] - bids[p - 1], "all pay original bids");
            h.Info.PopulateView(new RequestContext("NEXT", 9, Protocol.Info, "P2"), view);
            Check(!view.inventory.Contains("FREE_PASS"), "free pass consumed once");
            while (h.Room.Snapshot().turn < 10) h.Room.AdvanceManualPhase();
            Check(h.Room.Snapshot().phase == Protocol.Info && h.Room.Snapshot().seatPlayerIds.SequenceEqual(seats), "ten retains seats no auction");
        }
        private static void Abilities()
        {
            var h = new Harness();
            var context = new RequestContext("NEXT", 0, Protocol.AbilitySelection, "P2");
            var view = new PlayerView(); h.Info.PopulateView(context, view);
            Check(view.ability.candidates.Length == 3 && view.ability.candidates.Distinct().Count() == 3, "three distinct candidates");
            string choice = view.ability.candidates[1];
            Check(h.Send(2, "SELECT_ABILITY", new CommandPayload { abilityId = "UNKNOWN" }).errorCode == "INVALID_ABILITY", "invalid choice");
            Check(h.Send(2, "SELECT_ABILITY", new CommandPayload { abilityId = choice }).success, "valid choice");
            Check(h.Send(2, "SELECT_ABILITY", new CommandPayload { abilityId = choice }).errorCode == "ABILITY_LOCKED", "choice locked");
            h.Room.DrainMessages();
            h.Room.Disconnect("c2");
            Check(h.Room.Join("returning2", "ignored", h.Tokens["P2"]) == "", "ability-phase reconnect");
            var restored = h.Room.DrainMessages().Single(d => d.PlayerId == "P2" && d.Message.type == "PLAYER_VIEW").Message.playerView;
            Check(restored.ability.selectedId == choice && restored.ability.candidates.SequenceEqual(view.ability.candidates), "choice and candidates restored");
            Check(h.Send(3, "COMPLETE_PHASE").errorCode == "HOST_ONLY", "only host chooses defaults");
            h.Start();
            h.Info.PopulateView(new RequestContext("NEXT", 1, Protocol.Info, "P2"), view);
            Check(view.ability.selectedId == choice && h.Room.Snapshot().phase == Protocol.Info, "choice persists defaults applied");
            foreach (var id in new[] { "P1", "P3", "P4" })
            {
                h.Info.PopulateView(new RequestContext("NEXT", 1, Protocol.Info, id), view);
                Check(view.ability.selectedId == view.ability.candidates[0], "first candidate default");
            }
        }
        private static void AuctionDeadline()
        {
            foreach (double receipt in new[] { 334.999, 335.0 })
            {
                var h = new Harness(); h.Start(); h.Clock.Seconds = 320; h.Room.Tick();
                Check(h.Room.Snapshot().turn == 3 && h.Room.Snapshot().phase == Protocol.Auction, "auction timeline");
                var command = new Command { type = "SUBMIT_BID", requestId = "auction-boundary", turn = 3, payload = new CommandPayload { bidAmount = 10 } };
                h.Clock.Seconds = 340;
                var result = h.Room.ProcessCommandAt("c1", command, receipt);
                Check(result.success == (receipt < 335), "auction receipt boundary");
                h.Room.Tick();
                Check(h.Room.Snapshot().phase == Protocol.Info && h.Room.Snapshot().phaseEndsAtServerSeconds == 425, "auction adds fifteen, INFO ninety");
                Check(h.Room.ProcessCommandAt("c1", command, 340).success == result.success, "bid replay after phase");
            }
        }
        private static void FinalRanking()
        {
            // Flat market means all assets and best returns tie, exposing coins and initial order.
            var provider = new SeededMarketProvider(1);
            var market = new MarketService(t => { var data = provider.Get(t); foreach (var c in data.companies) Array.Clear(c.dailyChangesBps, 0, 31); return data; });
            var info = new SharedInformationService(market); var clock = new TestClock();
            var room = new GameRoom("final", market, info, clock, fixedOrderForFixture: true, settings: new GameSettings(turns: 1, enableProgressionFeatures: false));
            for (int p = 1; p <= 4; p++) { room.Join("c" + p, "P" + p); room.ProcessCommand("c" + p, RoomValidation.Command("SET_READY", "r", 0, new CommandPayload { ready = true })); }
            room.ProcessCommand("c1", RoomValidation.Command("START_GAME", "s", 0)); clock.Seconds = 155; room.Tick();
            var order = new[] { "P4", "P3", "P2", "P1" };
            var board = market.FinalizeGame(new RequestContext("final", 1, Protocol.Result), order, new[] { 1, 2, 2, 0 });
            Check(board.winnerPlayerId == "P3", "coin then initial order");
            board.winnerPlayerId = "tampered"; Check(market.GetScoreboard(1).winnerPlayerId == "P3", "final board copy");
            var performance = new MarketService(t => {
                var data = provider.Get(t);
                foreach (var c in data.companies)
                {
                    Array.Clear(c.dailyChangesBps, 0, 31);
                    if (c.companyId == "AI" || c.companyId == "SPACE") c.dailyChangesBps[5] = t == 1 ? 10000 : -5000;
                }
                return data;
            });
            var ids = new[] { "P1", "P2", "P3", "P4" };
            for (int t = 1; t <= 2; t++)
            {
                performance.BeginTurn(new RequestContext("performance", t, Protocol.Info), ids);
                for (int p = 0; p < 4; p++)
                {
                    string[] companies = p == 0 ? new[] { "AI", "SPACE" } : p == 1 ? new[] { "BANK", "FOOD" } : new[] { "BIO", "AUTO" };
                    foreach (var c in companies) performance.ValidateAndSelectCompany(new RequestContext("performance", t, Protocol.Selection, ids[p]), c);
                    performance.SubmitInvestment(new RequestContext("performance", t, Protocol.Investment, ids[p]), companies.Select(c => new Investment {
                        companyId = c, weightBps = 5000, intervals = new[] { new Interval { entryDay = 0, exitDay = 5 } } }).ToArray());
                }
                performance.LockAllOrders(new RequestContext("performance", t, Protocol.Investment));
                performance.SettleTurn(new RequestContext("performance", t, Protocol.Result));
                if (t == 1)
                {
                    var winner = performance.FinalizeGame(new RequestContext("performance", 1, Protocol.Result), order, new[] { 100, 100, 100, 0 });
                    Check(winner.winnerPlayerId == "P1", "assets dominate coins");
                }
            }
            var performanceBoard = performance.FinalizeGame(new RequestContext("performance", 2, Protocol.Result), order, new[] { 0, 0, 0, 0 });
            Check(performanceBoard.rows.All(r => r.totalAssets == 10000) && performanceBoard.winnerPlayerId == "P1", "best single-turn return precedes lottery");
        }
        private static void MarketGeneration()
        {
            var provider = new SeededMarketProvider(5);
            for (int t = 1; t <= 10; t++)
            {
                var data = provider.Get(t); new MarketBook(data, new InvestmentRules());
                foreach (var c in data.companies)
                {
                    int daily = c.companyId == "BANK" ? 80 : c.companyId == "FOOD" ? 120 : c.companyId == "AUTO" ? 350 : c.companyId == "BIO" ? 1500 : 800;
                    daily = daily * (100 + 5 * (t - 1)) / 100;
                    Check(c.dailyChangesBps.Skip(1).Select((v, i) => Math.Abs(v - c.dailyChangesBps[i]) <= daily).All(v => v), "daily increment cap");
                }
                data.companies[0].dailyChangesBps[1] = int.MaxValue;
                Check(provider.Get(t).companies[0].dailyChangesBps[1] != int.MaxValue, "cached market isolation");
            }
        }
        private static void History()
        {
            var h = new Harness(manual: true); h.Start();
            while (h.Room.Snapshot().turn < 3) h.Room.AdvanceManualPhase();
            h.Room.DrainMessages(); var before = h.Room.Snapshot();
            Check(h.Send(2, "GET_HISTORY", new CommandPayload { historyTurn = 1 }).success, "past view request");
            var delivery = h.Room.DrainMessages().Single(d => d.Message.type == "HISTORY_VIEW");
            Check(delivery.PlayerId == "P2" && delivery.Message.history.playerView.recipientPlayerId == "P2", "own history only");
            Check(h.Room.Snapshot().turn == before.turn && h.Room.Snapshot().phase == before.phase &&
                h.Room.Snapshot().phaseEndsAtServerSeconds == before.phaseEndsAtServerSeconds, "history does not advance current state");
            Check(h.Send(2, "GET_HISTORY", new CommandPayload { historyTurn = 3 }).errorCode == "HISTORY_UNAVAILABLE", "unsettled history unavailable");
            var store = new ClientStateStore();
            store.Apply(new ServerMessage { type = "WELCOME", session = new Session { playerId = "P2" } });
            store.SetPublicState(before); store.Apply(delivery.Message);
            Check(store.CurrentTurn == 3 && store.History.turn == 1 && store.View == null, "separate history cache");
            delivery.Message.history.playerView.selectedCompanies[0] = "tampered";
            Check(store.History.playerView.selectedCompanies[0] != "tampered", "history deep copy");
            while (h.Room.Snapshot().phase != Protocol.Ended) h.Room.AdvanceManualPhase();
            Check(h.Send(2, "GET_HISTORY", new CommandPayload { historyTurn = 10 }).success, "history after final end");
            Check(h.Room.Snapshot().turn == 10 && h.Room.Snapshot().phase == Protocol.Ended, "end state unchanged");
        }
        private static void Wire(IWireCodec codec)
        {
            foreach (string value in new[] { "1.5", "1e3", "9223372036854775808", "\"1\"" })
            {
                bool rejected = false;
                try { codec.DecodeClient("{\"command\":{\"payload\":{\"bidAmount\":" + value + "}}}"); }
                catch (FormatException) { rejected = true; }
                Check(rejected, "strict long bid " + value);
            }
            var message = new ServerMessage { type = "PLAYER_VIEW", playerView = new PlayerView {
                ability = new AbilityState { candidates = new[] { "POLITICIAN", "GAMBLER", "SYNDICATE" }, selectedId = "POLITICIAN" },
                inventory = new[] { "FREE_PASS" }, auction = new AuctionState { bidAmount = 123, seatPreferences = new[] { "D", "A", "B", "C" } } } };
            var decoded = codec.DecodeServer(codec.EncodeServer(message));
            var copy = DtoCopy.Copy(decoded.playerView); copy.ability.candidates[0] = "tampered"; copy.auction.seatPreferences[0] = "tampered"; copy.inventory[0] = "tampered";
            Check(decoded.playerView.ability.candidates[0] == "POLITICIAN" && decoded.playerView.auction.seatPreferences[0] == "D" && decoded.playerView.inventory[0] == "FREE_PASS", "nested copies and codec");
        }
        private static async Task TcpTenTurns(IWireCodec codec, int botCount)
        {
            var clock = new TestClock(); var market = MarketService.CreateRandom(651);
            var info = new SharedInformationService(market);
            var room = new GameRoom("tcp-next-" + botCount, market, info, clock, fixedOrderForFixture: true);
            var server = new TcpGameServer(room, codec, clock, 0, IPAddress.Loopback);
            var clients = new List<TcpGameClient>(); var bots = new List<DevBot>(); var humans = new List<GameClientController>();
            var logs = new List<List<ServerMessage>>();
            try
            {
                for (int p = 0; p < 4; p++)
                {
                    var client = await TcpGameClient.Connect("127.0.0.1", server.Port, "Client " + p, codec);
                    clients.Add(client); var log = new List<ServerMessage>(); logs.Add(log); client.MessageReceived += log.Add;
                    if (p >= 4 - botCount) bots.Add(new DevBot(client, 0, p + 10));
                    else humans.Add(new GameClientController(client));
                }
                DateTime deadline = DateTime.UtcNow.AddSeconds(40);
                float now = 0;
                while (room.Snapshot().phase != Protocol.Ended || clients.Any(c => !logs[clients.IndexOf(c)].Any(m => m.type == "ROOM_ENDED")))
                {
                    server.Pump(); foreach (var client in clients) client.Pump();
                    foreach (var client in clients.Skip(4 - botCount)) if (!string.IsNullOrEmpty(client.PlayerId)) room.RegisterLocalBot(client.PlayerId);
                    foreach (var human in humans) DriveHuman(human);
                    foreach (var bot in bots) bot.Update(now);
                    if (DateTime.UtcNow > deadline) throw new TimeoutException("10-turn TCP " + botCount + " bots at " + room.Snapshot().turn + "/" + room.Snapshot().phase);
                    now += 0.005f; clock.Seconds = now; await Task.Delay(1);
                }
                Check(room.Snapshot().turn == 10 && market.GetScoreboard(10).gameComplete, "TCP ten results");
                for (int p = 0; p < 4; p++)
                {
                    string id = "P" + (p + 1);
                    Check(logs[p].Where(m => m.type == "PLAYER_VIEW").All(m => m.playerView.recipientPlayerId == id), "TCP private routing");
                    Check(logs[p].Where(m => m.type == "PUBLIC_STATE").SelectMany(m => m.state.events ?? Array.Empty<PublicEvent>()).All(e => e.kind != "BID_SUBMITTED"), "no bids broadcast");
                }
                foreach (var log in logs.Skip(4 - botCount))
                {
                    Check(log.Any(m => m.type == "COMMAND_RESULT" && m.response.type == "BUY_INFORMATION_WINDOW_RESULT" && m.response.success), "bot actually buys a window via TCP");
                    foreach (var message in log.Where(m => m.type == "COMMAND_RESULT" && m.response.turn == 1 &&
                        (m.response.type == "BUY_INFORMATION_WINDOW_RESULT" || m.response.type == "SUBMIT_INVESTMENT_RESULT")))
                        BotCommandEvidence.Add(codec.EncodeServer(message));
                }
                Check(market.GetScoreboard(10).rows.All(r => r.returnHistory.Length == 11), "TCP history");
            }
            finally
            {
                foreach (var h in humans) h.Dispose(); foreach (var bot in bots) bot.Dispose();
                foreach (var c in clients) c.Dispose(); server.Dispose();
            }
        }
        private static void DriveHuman(GameClientController c)
        {
            var state = c.Store.Public; var view = c.Store.View;
            if (state == null || c.Store.EndReason != "") return;
            if (state.phase == Protocol.Lobby)
            {
                if (!c.IsLocalReady && !c.Store.HasPending("SET_READY")) c.SetReady(true);
                if (c.IsHost && c.AllPlayersReady && !c.Store.HasPending("START_GAME")) c.StartGame();
                return;
            }
            if (view == null || view.turn != state.turn) return;
            if (state.publicSelectionActive && state.publicSelectionPlayerId == c.LocalPlayerId && !c.Store.HasPending(CommandTypes.RevealInformationWindow))
            {
                foreach (var id in view.companies.Select(v => v.companyId))
                {
                    int start = Enumerable.Range(1, 26).FirstOrDefault(d => !InformationWindowRules.OverlapsPublic(state, id, d));
                    if (start > 0) { c.SelectInformationWindow(id, start); break; }
                }
            }
            if (state.phase == Protocol.AbilitySelection && string.IsNullOrEmpty(view.ability?.selectedId) && !c.Store.HasPending("SELECT_ABILITY"))
            {
                var screen = BlackWhale.UI.Presentation.LiveScreenModels.Ability(state, view, "", false, 0);
                c.SelectAbility(BlackWhale.UI.Presentation.LiveScreenModels.ServerAbilityId(screen.Candidates[0]));
            }
            if (state.phase == Protocol.Auction && !view.auction.confirmed && !c.Store.HasPending("SUBMIT_BID"))
            {
                var screen = BlackWhale.UI.Presentation.LiveScreenModels.Auction(state, view, null, false, 15);
                c.SubmitBid(screen.Bid, screen.SeatPreference, screen.UseFreePass);
            }
            if (c.CanFinishPhase() && state.phase != Protocol.AbilitySelection) c.FinishPhase();
            if (c.IsMyTurnToSelect() && !c.Store.HasPending("SELECT_COMPANY"))
            { string id = state.companySeats.Select(s => s.companyId).FirstOrDefault(c.CanSelect); if (id != null) c.SelectCompany(id); }
            if (state.phase == Protocol.Investment && !c.Store.OrderLocked && !c.Store.HasPending("SUBMIT_INVESTMENT") && view.selectedCompanies?.Length == 2)
                c.SubmitInvestment(view.selectedCompanies.Select(id => new Investment { companyId = id, weightBps = 5000,
                    intervals = new[] { new Interval { entryDay = 0, exitDay = 5 } } }).ToArray());
        }
    }
}
