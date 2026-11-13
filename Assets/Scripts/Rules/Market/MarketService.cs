using System;
using System.Collections.Generic;
using System.Linq;
using BlackWhale.Shared;

namespace BlackWhale.Rules
{
    /// <summary>
    /// B implementation of IMarketService: market source, company selection and capacity,
    /// investment orders, assets, settlement and scoreboard. A (GameRoom) calls this; it never
    /// talks to the network or UI. Every rejected call leaves all state unchanged.
    /// </summary>
    public sealed partial class MarketService : IMarketService, IMarketProgression, IMarketHistory, IWindowMarketDataSource, IMarketReservations
    {
        private readonly InvestmentRules rules;
        private readonly Func<int, MarketData> marketForTurn;

        private MarketBook market;
        private int turn;
        private string[] players = new string[0];
        private bool allLocked;

        private readonly Dictionary<string, List<string>> selected = new Dictionary<string, List<string>>();
        private readonly Dictionary<string, Investment[]> orders = new Dictionary<string, Investment[]>();
        private readonly Dictionary<string, Investment[]> reservations = new Dictionary<string, Investment[]>();
        private readonly Dictionary<string, long> assets = new Dictionary<string, long>();
        private readonly Dictionary<string, long> turnStartAssets = new Dictionary<string, long>();
        private readonly Dictionary<string, int> lastTurnReturn = new Dictionary<string, int>();
        private readonly Dictionary<string, List<ReturnPoint>> history = new Dictionary<string, List<ReturnPoint>>();
        private readonly Dictionary<int, Scoreboard> settledBoards = new Dictionary<int, Scoreboard>();
        private readonly Dictionary<int, PlayerSettlement[]> settledPlayers = new Dictionary<int, PlayerSettlement[]>();
        private readonly Dictionary<int, MarketBook> markets = new Dictionary<int, MarketBook>();
        private readonly Dictionary<int, Dictionary<string, OwnInvestmentState>> ownHistory = new Dictionary<int, Dictionary<string, OwnInvestmentState>>();
        private readonly Dictionary<string, long> costs = new Dictionary<string, long>();
        private readonly Dictionary<string, long> costRequests = new Dictionary<string, long>();
        private readonly Dictionary<string, int> bestTurnReturn = new Dictionary<string, int>();

        /// <summary>Fixture mode: the same fixed market (e.g. B_market_server.json) for its turn.</summary>
        public MarketService(MarketData fixedMarket, InvestmentRules rules = null)
            : this(t => fixedMarket, rules) { }

        /// <summary>
        /// Normal v3 play: one seeded pattern market per turn and movable information windows.
        /// Same seed + turn = same market, so a test or replay can fix the seed.
        /// </summary>
        public static MarketService CreateRandom(int seed, InvestmentRules rules = null)
        {
            var liveRules = (rules ?? new InvestmentRules()).Copy(); liveRules.entryAtOpen = true;
            liveRules.minHoldingDays = 1; liveRules.maxHoldingDays = 30; liveRules.maxIntervalsPerCompany = 0;
            return new MarketService(new PatternMarketGenerator(seed, liveRules).ForTurn, liveRules) { UsesWindowInformation = true };
        }

        public bool UsesWindowInformation { get; private set; }

        public int[] GetWindow(int requestedTurn, string companyId, int startDay, int length = 5)
        {
            if (startDay < 1 || length < 1 || startDay > 30 || length > 31 - startDay) throw new ArgumentOutOfRangeException(nameof(startDay));
            if (!markets.TryGetValue(requestedTurn, out var book) || !book.HasCompany(companyId))
                throw new ArgumentException("Unknown market or company.");
            return Enumerable.Range(startDay, length).Select(d => book.ChangeBps(companyId, d)).ToArray();
        }
        public int[] GetOpenWindow(int requestedTurn, string companyId, int startDay, int length = 5)
        {
            GetWindow(requestedTurn, companyId, startDay, length); // validate the same range
            return Enumerable.Range(startDay, length).Select(d => markets[requestedTurn].ChangeBps(companyId, d - 1)).ToArray();
        }

        /// <summary>Provider mode: one market per turn, e.g. <see cref="RandomMarketGenerator.ForTurn"/>.</summary>
        public MarketService(Func<int, MarketData> marketForTurn, InvestmentRules rules = null)
        {
            this.marketForTurn = marketForTurn ?? throw new ArgumentNullException(nameof(marketForTurn));
            this.rules = (rules ?? new InvestmentRules()).Copy();
        }

        public int CurrentTurn { get { return turn; } }

        // ---------------- IMarketDataSource (for C) ----------------

        public string[] GetCompanyIds() { return (string[])InvestmentRules.CompanyIds.Clone(); }

        public ServerInformationBundle GetBundle(int requestedTurn, string companyId, int bundleIndex)
        {
            if (!markets.TryGetValue(requestedTurn, out var book)) throw new ArgumentOutOfRangeException(nameof(requestedTurn));
            if (!book.HasCompany(companyId)) throw new ArgumentException("Unknown company: " + companyId);
            if (bundleIndex < 0 || bundleIndex >= InvestmentRules.BundlesPerCompany) throw new ArgumentOutOfRangeException(nameof(bundleIndex));
            int startDay;
            int[] values = book.Bundle(companyId, bundleIndex, out startDay);
            return new ServerInformationBundle { CompanyId = companyId, BundleIndex = bundleIndex, StartDay = startDay,
                IsPublic = book.IsPublic(companyId, bundleIndex), DailyChangesBps = values };
        }

        // ---------------- Turn start ----------------

        public void BeginTurn(RequestContext context, string[] playerIds)
        {
            if (context.Turn < 1 || context.Turn < turn) throw new ArgumentOutOfRangeException(nameof(context));
            if (context.Turn == turn) return; // same turn again: no reset, no second comeback grant
            if (playerIds == null || playerIds.Length == 0 || playerIds.Any(string.IsNullOrWhiteSpace) ||
                playerIds.Distinct().Count() != playerIds.Length) throw new ArgumentException("Distinct player IDs required.", nameof(playerIds));
            var loaded = new MarketBook(marketForTurn(context.Turn), rules); // validate before mutating anything
            if (loaded.Turn != context.Turn) throw new ArgumentException("Market turn does not match the requested turn.");

            if (turn > 0) SaveOwnHistory();

            turn = context.Turn;
            market = loaded;
            markets.Add(turn, loaded);
            players = (string[])playerIds.Clone();
            selected.Clear();
            orders.Clear();
            reservations.Clear();
            allLocked = false;
            costs.Clear();
            foreach (string id in players)
            {
                if (!assets.ContainsKey(id))
                {
                    assets[id] = rules.initialAssets;
                    lastTurnReturn[id] = 0;
                    history[id] = new List<ReturnPoint> { new ReturnPoint { turn = 0, returnBps = 0 } };
                }
                else if (assets[id] <= 0) assets[id] = rules.comebackGrant; // 0 assets: not eliminated, 1,000 support
                turnStartAssets[id] = assets[id];
                costs[id] = 0;
                selected[id] = new List<string>();
            }
            if (StockEnabled) BeginStockTurn();
        }

        // ---------------- Selection ----------------

        public RuleResult ValidateAndSelectCompany(RequestContext context, string companyId)
        {
            string code = CheckContext(context, Protocol.Selection);
            if (code != "") return new RuleResult(code);
            if (!market.HasCompany(companyId)) return new RuleResult(ErrorCodes.InvalidCompany);
            if (selected[context.PlayerId].Contains(companyId)) return new RuleResult(ErrorCodes.DuplicateCompany);
            if (selected[context.PlayerId].Count >= 2) return new RuleResult(ErrorCodes.SelectionComplete);
            if (RemainingSeats(companyId) <= 0) return new RuleResult(ErrorCodes.CompanyFull);

            selected[context.PlayerId].Add(companyId);
            return new RuleResult(payload: new ResultPayload { companyId = companyId });
        }

        public CompanySeat[] GetCompanySeats()
        {
            if (StockEnabled) return new CompanySeat[0]; // ShareMarket replaces legacy company seats.
            return InvestmentRules.CompanyIds.Select(id => new CompanySeat { companyId = id, remainingSeats = RemainingSeats(id) }).ToArray();
        }

        public string[] GetSelectedCompanies(string playerId)
        {
            if (StockEnabled) return StockHoldings(playerId).Where(p => p.Value > 0).Select(p => p.Key).ToArray();
            List<string> list;
            return selected.TryGetValue(playerId, out list) ? list.ToArray() : new string[0];
        }

        private int RemainingSeats(string companyId)
        {
            return rules.maxPlayersPerCompany - selected.Values.Count(s => s.Contains(companyId));
        }

        // ---------------- Orders ----------------

        public RuleResult SaveReservations(RequestContext context, Investment[] input)
        {
            if (context.Turn != turn) return new RuleResult("STALE_TURN");
            if (!selected.ContainsKey(context.PlayerId)) return new RuleResult("INVALID_PLAYER");
            if (context.Phase != Protocol.Info && context.Phase != Protocol.Selection && context.Phase != Protocol.Investment)
                return new RuleResult("WRONG_PHASE");
            if (allLocked || IsOrderLocked(context.PlayerId)) return new RuleResult(ErrorCodes.OrderLocked);
            if (input == null || input.Length > InvestmentRules.CompanyIds.Length || input.Any(i => i == null || !market.HasCompany(i.companyId)))
                return new RuleResult(ErrorCodes.InvalidCompany);
            if (input.Select(i => i.companyId).Distinct().Count() != input.Length) return new RuleResult(ErrorCodes.DuplicateCompany);
            if (StockEnabled && input.Any(i => i.companyId != "BANK" && context.Phase == Protocol.Investment && StockHoldings(context.PlayerId)[i.companyId] == 0))
                return new RuleResult(ErrorCodes.InvalidCompany);
            foreach (var i in input)
            {
                if ((i.intervals?.Length ?? 0) == 0) continue;
                string error = OrderValidator.ValidateIntervals(i.intervals, rules);
                if (error.Length > 0) return new RuleResult(error);
            }
            var weighted = input.Where(i => i.weightBps != 0).ToArray();
            if (StockEnabled && weighted.Length != 0) return new RuleResult(ErrorCodes.InvalidRatio);
            if (weighted.Length != 0 && (weighted.Length != 2 ||
                Array.IndexOf(rules.firstCompanyWeightOptionsBps, weighted[0].weightBps) < 0 ||
                weighted[0].weightBps + weighted[1].weightBps != InvestmentRules.FullWeightBps))
                return new RuleResult(ErrorCodes.InvalidRatio);
            reservations[context.PlayerId] = Copy(input);
            if (StockEnabled) ApplyStockPlans(context.PlayerId);
            return new RuleResult();
        }

        public Investment[] GetReservations(string playerId)
            => reservations.TryGetValue(playerId, out var saved) ? Copy(saved) : new Investment[0];

        public Investment[] BuildAutomaticInvestments(string playerId)
        {
            if (StockEnabled) return BuildStockInvestments(playerId);
            var saved = GetReservations(playerId);
            var companies = GetSelectedCompanies(playerId);
            var weighted = saved.Where(i => i.weightBps != 0).ToArray();
            bool useWeights = weighted.Length == 2 && weighted.All(i => companies.Contains(i.companyId));
            return (useWeights ? weighted.Select(i => i.companyId) : companies).Select(id => new Investment {
                companyId = id, weightBps = useWeights ? weighted.Single(i => i.companyId == id).weightBps : 5000,
                intervals = saved.FirstOrDefault(i => i.companyId == id)?.intervals is Interval[] ranges && ranges.Length > 0
                    ? ranges : new[] { new Interval { entryDay = 0, exitDay = 5 } }
            }).ToArray();
        }

        public RuleResult SubmitInvestment(RequestContext context, Investment[] investments)
        {
            string code = CheckContext(context, Protocol.Investment);
            if (code != "") return new RuleResult(code);
            if (allLocked || orders.ContainsKey(context.PlayerId)) return new RuleResult(ErrorCodes.OrderLocked);

            if (StockEnabled)
            {
                var saved = SaveReservations(context, investments);
                if (!saved.Success) return saved;
                orders[context.PlayerId] = BuildStockInvestments(context.PlayerId);
                return new RuleResult(payload: new ResultPayload { locked = true, investments = Copy(orders[context.PlayerId]) });
            }

            code = OrderValidator.Validate(investments, selected[context.PlayerId], rules);
            if (code != "") return new RuleResult(code);

            orders[context.PlayerId] = Copy(investments);
            return new RuleResult(payload: new ResultPayload { locked = true, investments = Copy(investments) });
        }

        public bool IsOrderLocked(string playerId) { return orders.ContainsKey(playerId); }

        public void LockAllOrders(RequestContext context)
        {
            if (market == null || context.Turn != turn || context.Phase != Protocol.Investment)
                throw new InvalidOperationException("Orders can only be locked for the current investment phase.");
            if (allLocked) return;
            string missing = players.FirstOrDefault(p => !orders.ContainsKey(p));
            if (missing != null) throw new InvalidOperationException("Order missing for " + missing + "; A must submit the default order first.");
            allLocked = true;
        }

        public OwnInvestmentState GetOwnInvestmentState(string playerId)
        {
            Investment[] own;
            long total;
            return new OwnInvestmentState
            {
                TotalAssets = assets.TryGetValue(playerId, out total) ? total - (settledBoards.ContainsKey(turn) ? 0 : costs[playerId]) : rules.initialAssets,
                SelectedCompanies = GetSelectedCompanies(playerId),
                Investments = orders.TryGetValue(playerId, out own) ? Copy(own) : new Investment[0]
            };
        }

        public RevealedCompany[] GetRevealedMarket(RequestContext context)
        {
            if (!markets.TryGetValue(context.Turn, out var book)) throw new ArgumentOutOfRangeException(nameof(context));
            if (context.Turn == turn ? !allLocked : !settledBoards.ContainsKey(context.Turn))
                throw new InvalidOperationException("Market cannot be revealed before all orders are locked.");
            return InvestmentRules.CompanyIds.Select(id => new RevealedCompany { companyId = id, dailyChangesBps = book.AllDays(id),
                investments = players.Select(player => new { player, own = GetOwnInvestmentState(player, context.Turn) })
                    .SelectMany(p => p.own.Investments.Where(i => i.companyId == id).Select(i => new RevealedInvestment {
                        playerId = p.player, returnBps = StockEnabled ? StockHistoryRate(p.player, id, context.Turn) : Settlement.CompanyReturnBps(book, i, rules),
                        shares = StockEnabled ? StockSharesForHistory(p.player, id, context.Turn) : 0,
                        intervals = i.intervals.Select(v => new RevealedTrade { entryDay = v.entryDay, exitDay = v.exitDay,
                            returnBps = StockEnabled ? Settlement.ReturnBps(10000 + book.ChangeBps(id, Math.Max(0, v.entryDay - 1)), 10000 + book.ChangeBps(id, v.exitDay)) : book.ChangeBps(id, v.exitDay) - book.ChangeBps(id, rules.entryAtOpen ? Math.Max(0, v.entryDay - 1) : v.entryDay) }).ToArray()
                    })).ToArray() }).ToArray();
        }

        // Server-side history. Callers may project only the requesting player's state to clients.
        public OwnInvestmentState GetOwnInvestmentState(string playerId, int requestedTurn)
        {
            if (requestedTurn == turn) return GetOwnInvestmentState(playerId);
            if (!ownHistory.TryGetValue(requestedTurn, out var snapshot) || !snapshot.TryGetValue(playerId, out var own))
                throw new ArgumentOutOfRangeException(nameof(requestedTurn));
            return new OwnInvestmentState { TotalAssets = own.TotalAssets,
                SelectedCompanies = (string[])own.SelectedCompanies.Clone(), Investments = Copy(own.Investments) };
        }

        public Scoreboard GetScoreboard(int requestedTurn)
        { return settledBoards.TryGetValue(requestedTurn, out var board) ? CopyBoard(board) : null; }

        private void SaveOwnHistory()
        { ownHistory[turn] = players.ToDictionary(id => id, GetOwnInvestmentState); }

        // ---------------- Settlement ----------------

        /// <summary>Applies the turn once. Calling again for the same turn returns the same board without changing assets.</summary>
        public Scoreboard SettleTurn(RequestContext context)
        {
            Scoreboard done;
            if (settledBoards.TryGetValue(context.Turn, out done)) return CopyBoard(done);
            if (context.Turn != turn) throw new InvalidOperationException("Only the current turn can be settled.");
            if (!allLocked) throw new InvalidOperationException("Settlement before lock.");

            // Compute everything first, then apply, so a failure never leaves half-updated assets.
            PlayerSettlement[] results = players.Select(id =>
                StockEnabled ? SettleStockPlayer(id) : Settlement.SettlePlayer(id, turnStartAssets[id], costs[id], orders[id], market, rules)).ToArray();

            foreach (PlayerSettlement r in results)
            {
                if (StockEnabled) AdvanceStockStreaks(r.playerId);
                assets[r.playerId] = r.endingAssets;
                lastTurnReturn[r.playerId] = r.turnReturnBps;
                bestTurnReturn[r.playerId] = bestTurnReturn.TryGetValue(r.playerId, out var best) ? Math.Max(best, r.turnReturnBps) : r.turnReturnBps;
                history[r.playerId].Add(new ReturnPoint { turn = turn, returnBps = Settlement.ReturnBps(rules.initialAssets, r.endingAssets) });
            }
            settledPlayers[turn] = results;
            Scoreboard board = BuildScoreboard();
            settledBoards[turn] = board;
            SaveOwnHistory();
            return CopyBoard(board);
        }

        /// <summary>Server-only detail (company profits) for logs/tests. Never broadcast.</summary>
        public PlayerSettlement[] GetSettlementDetail(int settledTurn)
        {
            PlayerSettlement[] r;
            return settledPlayers.TryGetValue(settledTurn, out r) ? r.Select(p => new PlayerSettlement
            {
                playerId = p.playerId, startingAssets = p.startingAssets, profit = p.profit,
                endingAssets = p.endingAssets, turnReturnBps = p.turnReturnBps,
                companyProfits = p.companyProfits.Select(c => new CompanyProfit { companyId = c.companyId,
                    allocatedAssets = c.allocatedAssets, profit = c.profit, returnBps = c.returnBps }).ToList()
            }).ToArray() : null;
        }

        public long GetAssets(string playerId) { return assets[playerId]; }
        public long GetTurnStartAssets(string playerId) => turnStartAssets[playerId];
        public RuleResult CommitCost(RequestContext context, long amount)
        {
            string key = context.RoomId + ":" + context.Turn + ":" + context.PlayerId + ":" + context.RequestId;
            if (costRequests.TryGetValue(key, out var previous))
                return new RuleResult(previous == amount ? "" : "REQUEST_ID_CONFLICT");
            if (context.Turn != turn) return new RuleResult("STALE_TURN");
            if (context.Phase != Protocol.Auction && context.Phase != Protocol.Info && context.Phase != Protocol.Investment)
                return new RuleResult("WRONG_PHASE");
            if (string.IsNullOrEmpty(context.RequestId) || !costs.ContainsKey(context.PlayerId) || amount < 0)
                return new RuleResult("INVALID_REQUEST");
            if (allLocked || orders.ContainsKey(context.PlayerId)) return new RuleResult("ORDER_LOCKED");
            if (amount > turnStartAssets[context.PlayerId] - costs[context.PlayerId]) return new RuleResult("NOT_ENOUGH_ASSETS");
            costs[context.PlayerId] += amount; costRequests.Add(key, amount);
            return new RuleResult();
        }
        public Scoreboard FinalizeGame(RequestContext context, string[] initialOrder, int[] finalCoins)
        {
            if (!settledBoards.TryGetValue(context.Turn, out var board)) throw new InvalidOperationException("Settle before final ranking.");
            if (initialOrder == null || finalCoins == null || initialOrder.Length != players.Length ||
                finalCoins.Length != initialOrder.Length || !initialOrder.OrderBy(x => x).SequenceEqual(players.OrderBy(x => x)))
                throw new ArgumentException("Final snapshot must contain each player once.");
            if (!board.gameComplete)
            {
                var ranked = initialOrder.Select((id, i) => new { Id = id, Index = i, Coins = finalCoins[i] })
                    .OrderByDescending(p => assets[p.Id]).ThenByDescending(p => p.Coins)
                    .ThenByDescending(p => bestTurnReturn[p.Id]).ThenBy(p => p.Index).ToArray();
                board.gameComplete = true; board.winnerPlayerId = ranked[0].Id;
            }
            return CopyBoard(board);
        }

        private Scoreboard BuildScoreboard()
        {
            var current = players.ToDictionary(p => p, p => assets[p]);
            return new Scoreboard
            {
                turn = turn,
                phase = Protocol.Result,
                rankingFinalForTurn = true,
                rows = Settlement.Rank(current).Select(r => new ScoreRow
                {
                    rank = r.Value,
                    playerId = r.Key,
                    totalAssets = assets[r.Key],
                    turnReturnBps = lastTurnReturn[r.Key],
                    cumulativeProfit = assets[r.Key] - rules.initialAssets,
                    cumulativeReturnBps = Settlement.ReturnBps(rules.initialAssets, assets[r.Key]),
                    returnHistory = history[r.Key].Select(h => new ReturnPoint { turn = h.turn, returnBps = h.returnBps }).ToArray()
                }).ToArray()
            };
        }

        // ---------------- helpers ----------------

        private static Scoreboard CopyBoard(Scoreboard board)
        {
            return new Scoreboard { turn = board.turn, phase = board.phase, rankingFinalForTurn = board.rankingFinalForTurn,
                gameComplete = board.gameComplete, winnerPlayerId = board.winnerPlayerId,
                rows = board.rows.Select(r => new ScoreRow { rank = r.rank, playerId = r.playerId,
                    totalAssets = r.totalAssets, turnReturnBps = r.turnReturnBps, cumulativeProfit = r.cumulativeProfit,
                    cumulativeReturnBps = r.cumulativeReturnBps,
                    returnHistory = r.returnHistory.Select(h => new ReturnPoint { turn = h.turn, returnBps = h.returnBps }).ToArray()
                }).ToArray() };
        }

        private string CheckContext(RequestContext context, string phase)
        {
            if (market == null || context.Turn != turn) return ErrorCodes.StaleTurn;
            if (context.Phase != phase) return ErrorCodes.WrongPhase;
            if (!selected.ContainsKey(context.PlayerId ?? "")) return ErrorCodes.InvalidRequest;
            return "";
        }

        private static Investment[] Copy(Investment[] source)
        {
            return source.Select(i => new Investment
            {
                companyId = i.companyId,
                weightBps = i.weightBps,
                intervals = (i.intervals ?? new Interval[0]).Select(p => new Interval { entryDay = p.entryDay, exitDay = p.exitDay }).ToArray()
            }).ToArray();
        }
    }
}
