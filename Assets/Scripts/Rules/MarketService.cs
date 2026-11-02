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
    public sealed class MarketService : IMarketService
    {
        private readonly InvestmentRules rules;
        private readonly Func<int, MarketData> marketForTurn;

        private MarketBook market;
        private int turn;
        private string[] players = new string[0];
        private bool allLocked;

        private readonly Dictionary<string, List<string>> selected = new Dictionary<string, List<string>>();
        private readonly Dictionary<string, Investment[]> orders = new Dictionary<string, Investment[]>();
        private readonly Dictionary<string, long> assets = new Dictionary<string, long>();
        private readonly Dictionary<string, long> turnStartAssets = new Dictionary<string, long>();
        private readonly Dictionary<string, int> lastTurnReturn = new Dictionary<string, int>();
        private readonly Dictionary<string, List<ReturnPoint>> history = new Dictionary<string, List<ReturnPoint>>();
        private readonly Dictionary<int, Scoreboard> settledBoards = new Dictionary<int, Scoreboard>();
        private readonly Dictionary<int, PlayerSettlement[]> settledPlayers = new Dictionary<int, PlayerSettlement[]>();

        /// <summary>Fixture mode: the same fixed market (e.g. B_market_server.json) for its turn.</summary>
        public MarketService(MarketData fixedMarket, InvestmentRules rules = null)
            : this(t => fixedMarket, rules) { }

        /// <summary>Provider mode: one market per turn (random generation is a later increment).</summary>
        public MarketService(Func<int, MarketData> marketForTurn, InvestmentRules rules = null)
        {
            this.marketForTurn = marketForTurn ?? throw new ArgumentNullException(nameof(marketForTurn));
            this.rules = rules ?? new InvestmentRules();
        }

        public int CurrentTurn { get { return turn; } }

        // ---------------- IMarketDataSource (for C) ----------------

        public string[] GetCompanyIds() { return (string[])InvestmentRules.CompanyIds.Clone(); }

        public ServerInformationBundle GetBundle(int requestedTurn, string companyId, int bundleIndex)
        {
            if (market == null || requestedTurn != turn) throw new ArgumentOutOfRangeException(nameof(requestedTurn));
            if (!market.HasCompany(companyId)) throw new ArgumentException("Unknown company: " + companyId);
            if (bundleIndex < 0 || bundleIndex >= InvestmentRules.BundlesPerCompany) throw new ArgumentOutOfRangeException(nameof(bundleIndex));
            int startDay;
            int[] values = market.Bundle(companyId, bundleIndex, out startDay);
            return new ServerInformationBundle { CompanyId = companyId, BundleIndex = bundleIndex, StartDay = startDay,
                IsPublic = market.IsPublic(companyId, bundleIndex), DailyChangesBps = values };
        }

        // ---------------- Turn start ----------------

        public void BeginTurn(RequestContext context, string[] playerIds)
        {
            if (context.Turn <= turn) return; // same turn again: no reset, no second comeback grant
            var loaded = new MarketBook(marketForTurn(context.Turn), rules); // validate before mutating anything

            turn = context.Turn;
            market = loaded;
            players = (string[])playerIds.Clone();
            selected.Clear();
            orders.Clear();
            allLocked = false;
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
                selected[id] = new List<string>();
            }
        }

        // ---------------- Selection ----------------

        public RuleResult ValidateAndSelectCompany(RequestContext context, string companyId)
        {
            string code = CheckContext(context, Protocol.Selection);
            if (code != "") return new RuleResult(code);
            if (!market.HasCompany(companyId)) return new RuleResult(ErrorCodes.InvalidCompany);
            if (selected[context.PlayerId].Contains(companyId)) return new RuleResult(ErrorCodes.DuplicateCompany);
            if (RemainingSeats(companyId) <= 0) return new RuleResult(ErrorCodes.CompanyFull);

            selected[context.PlayerId].Add(companyId);
            return new RuleResult(payload: new ResultPayload { companyId = companyId });
        }

        public CompanySeat[] GetCompanySeats()
        {
            return InvestmentRules.CompanyIds.Select(id => new CompanySeat { companyId = id, remainingSeats = RemainingSeats(id) }).ToArray();
        }

        public string[] GetSelectedCompanies(string playerId)
        {
            List<string> list;
            return selected.TryGetValue(playerId, out list) ? list.ToArray() : new string[0];
        }

        private int RemainingSeats(string companyId)
        {
            return rules.maxPlayersPerCompany - selected.Values.Count(s => s.Contains(companyId));
        }

        // ---------------- Orders ----------------

        public RuleResult SubmitInvestment(RequestContext context, Investment[] investments)
        {
            string code = CheckContext(context, Protocol.Investment);
            if (code != "") return new RuleResult(code);
            if (allLocked || orders.ContainsKey(context.PlayerId)) return new RuleResult(ErrorCodes.OrderLocked);

            code = OrderValidator.Validate(investments, selected[context.PlayerId], rules);
            if (code != "") return new RuleResult(code);

            orders[context.PlayerId] = Copy(investments);
            return new RuleResult(payload: new ResultPayload { locked = true, investments = Copy(investments) });
        }

        public bool IsOrderLocked(string playerId) { return orders.ContainsKey(playerId); }

        public void LockAllOrders(RequestContext context)
        {
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
                TotalAssets = assets.TryGetValue(playerId, out total) ? total : rules.initialAssets,
                SelectedCompanies = GetSelectedCompanies(playerId),
                Investments = orders.TryGetValue(playerId, out own) ? Copy(own) : new Investment[0]
            };
        }

        public RevealedCompany[] GetRevealedMarket(RequestContext context)
        {
            if (!allLocked) throw new InvalidOperationException("Market cannot be revealed before all orders are locked.");
            return InvestmentRules.CompanyIds.Select(id => new RevealedCompany { companyId = id, dailyChangesBps = market.AllDays(id) }).ToArray();
        }

        // ---------------- Settlement ----------------

        /// <summary>Applies the turn once. Calling again for the same turn returns the same board without changing assets.</summary>
        public Scoreboard SettleTurn(RequestContext context)
        {
            Scoreboard done;
            if (settledBoards.TryGetValue(context.Turn, out done)) return done;
            if (context.Turn != turn) throw new InvalidOperationException("Only the current turn can be settled.");
            if (!allLocked) throw new InvalidOperationException("Settlement before lock.");

            // Compute everything first, then apply, so a failure never leaves half-updated assets.
            PlayerSettlement[] results = players.Select(id =>
                Settlement.SettlePlayer(id, turnStartAssets[id], 0, orders[id], market, rules)).ToArray();

            foreach (PlayerSettlement r in results)
            {
                assets[r.playerId] = r.endingAssets;
                lastTurnReturn[r.playerId] = r.turnReturnBps;
                history[r.playerId].Add(new ReturnPoint { turn = turn, returnBps = Settlement.ReturnBps(rules.initialAssets, r.endingAssets) });
            }
            settledPlayers[turn] = results;
            Scoreboard board = BuildScoreboard();
            settledBoards[turn] = board;
            return board;
        }

        /// <summary>Server-only detail (company profits) for logs/tests. Never broadcast.</summary>
        public PlayerSettlement[] GetSettlementDetail(int settledTurn)
        {
            PlayerSettlement[] r;
            return settledPlayers.TryGetValue(settledTurn, out r) ? r : null;
        }

        public long GetAssets(string playerId) { return assets[playerId]; }

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
