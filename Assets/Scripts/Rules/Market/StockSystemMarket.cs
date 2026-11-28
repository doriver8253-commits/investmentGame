using System;
using System.Collections.Generic;
using System.Linq;
using BlackWhale.Shared;

namespace BlackWhale.Rules
{
    public sealed partial class MarketService : IStockMarketSystem, IDeveloperCheckpoint
    {
        private sealed class Slot
        {
            public string Claim;
            public bool Active;
            public Interval[] Plan = Array.Empty<Interval>();
        }
        private readonly Dictionary<string, List<StockFill>> fills = InvestmentRules.CompanyIds.ToDictionary(id => id, id => new List<StockFill>());
        private bool liveTrading;
        public int ProcessedDay { get; private set; }
        private sealed class StockPlayer
        {
            public readonly Dictionary<string, int> Shares = InvestmentRules.CompanyIds.ToDictionary(c => c, c => 0);
            public readonly Dictionary<string, int> Streak = InvestmentRules.CompanyIds.ToDictionary(c => c, c => 0);
            public readonly List<string> Augments = new List<string>();
            public string[] Candidates = new string[0];
            public bool Chosen = true, PriorityUsed;
            public string Focus;
            public readonly Slot[] Slots = Enumerable.Range(0, StockRules.Slots).Select(i => new Slot()).ToArray();
            public readonly Dictionary<string, int[]> DailyQuantity = InvestmentRules.CompanyIds.ToDictionary(id => id, id => new int[31]);
            public readonly Dictionary<string, decimal> Gain = InvestmentRules.CompanyIds.ToDictionary(id => id, id => 0m);
            public Interval[] BankPlan = Array.Empty<Interval>();
            public decimal DepositDays;
            public int Discount, OpportunityTurn, PendingDiscount;
            public int DraftSharesRemaining = StockRules.SharesPerPick;
        }
        private readonly Dictionary<string, StockPlayer> stockPlayers = new Dictionary<string, StockPlayer>();
        private readonly Dictionary<int, Dictionary<string, Dictionary<string, int>>> stockHistory = new Dictionary<int, Dictionary<string, Dictionary<string, int>>>();
        private int stockSeed;
        private static StockPlayer CopyStockPlayer(StockPlayer original)
        {
            var copy = new StockPlayer { Chosen = original.Chosen, PriorityUsed = original.PriorityUsed, Focus = original.Focus,
                Candidates = (string[])original.Candidates.Clone(), BankPlan = Copy(original.BankPlan), DepositDays = original.DepositDays,
                Discount = original.Discount, OpportunityTurn = original.OpportunityTurn, PendingDiscount = original.PendingDiscount,
                DraftSharesRemaining = original.DraftSharesRemaining };
            copy.Augments.AddRange(original.Augments);
            foreach (var company in InvestmentRules.CompanyIds) {
                copy.Shares[company] = original.Shares[company]; copy.Streak[company] = original.Streak[company];
                copy.Gain[company] = original.Gain[company]; copy.DailyQuantity[company] = (int[])original.DailyQuantity[company].Clone();
            }
            for (int i = 0; i < original.Slots.Length; i++) {
                copy.Slots[i].Claim = original.Slots[i].Claim; copy.Slots[i].Active = original.Slots[i].Active;
                copy.Slots[i].Plan = Copy(original.Slots[i].Plan);
            }
            return copy;
        }
        private static Interval[] Copy(Interval[] values) => values.Select(i => new Interval { entryDay = i.entryDay, exitDay = i.exitDay }).ToArray();
        public Action CaptureDeveloperCheckpoint(string roomId)
        {
            int savedDay = ProcessedDay; bool savedLive = liveTrading, savedLocked = allLocked;
            var restore = new[] {
                DeveloperCheckpoint.Map(stockPlayers, CopyStockPlayer),
                DeveloperCheckpoint.Map(fills, values => new List<StockFill>(values)),
                DeveloperCheckpoint.Map(orders, Copy), DeveloperCheckpoint.Map(reservations, Copy),
                DeveloperCheckpoint.Map(selected, values => new List<string>(values)),
                DeveloperCheckpoint.Map(assets, value => value), DeveloperCheckpoint.Map(turnStartAssets, value => value),
                DeveloperCheckpoint.Map(costs, value => value), DeveloperCheckpoint.Map(costRequests, value => value),
                DeveloperCheckpoint.Map(lastTurnReturn, value => value), DeveloperCheckpoint.Map(bestTurnReturn, value => value),
                DeveloperCheckpoint.Map(history, values => values.Select(v => new ReturnPoint { turn = v.turn, returnBps = v.returnBps }).ToList()),
                DeveloperCheckpoint.Map(settledBoards, CopyBoard),
                DeveloperCheckpoint.Map(settledPlayers, values => values.Select(p => new PlayerSettlement { playerId = p.playerId,
                    startingAssets = p.startingAssets, profit = p.profit, endingAssets = p.endingAssets, turnReturnBps = p.turnReturnBps,
                    companyProfits = p.companyProfits.Select(c => new CompanyProfit { companyId = c.companyId, allocatedAssets = c.allocatedAssets, profit = c.profit, returnBps = c.returnBps }).ToList() }).ToArray()),
                DeveloperCheckpoint.Map(stockReturns, values => values.ToDictionary(p => p.Key, p => p.Value.ToDictionary(c => c.Key, c => c.Value))),
                DeveloperCheckpoint.Map(stockHistory, values => values.ToDictionary(p => p.Key, p => p.Value.ToDictionary(c => c.Key, c => c.Value)))
            };
            var restoreLive = CaptureLiveMarket();
            return () => { foreach (var action in restore) action(); restoreLive(); ProcessedDay = savedDay; liveTrading = savedLive; allLocked = savedLocked; };
        }
        public bool StockEnabled { get; private set; }
        /// <param name="marketNews">true면 실시간 시장(군중 반응·추세·폭락/급등), 뉴스(루머·속보), 턴 미션을 켠다.
        /// 실제 게임(DevTabletUI)은 켜고, 기존 규칙 검증·봇 실험은 끈 채로 비교한다.</param>
        public static MarketService CreateStockSystem(int seed, StockPatternLibrary library = null, bool marketNews = false)
        {
            var rules = new InvestmentRules { initialAssets = 10000, entryAtOpen = true, minHoldingDays = 1, maxHoldingDays = 30, maxIntervalsPerCompany = 0 };
            var frozenLibrary = (library ?? StockPatternLibrary.CreateDefault()).Copy();
            return new MarketService(t => marketNews ? LiveBase(GenerateStockMarket(seed, t, frozenLibrary)) : GenerateStockMarket(seed, t, frozenLibrary), rules)
                { StockEnabled = true, stockSeed = seed, UsesWindowInformation = true, NewsEnabled = marketNews };
        }
        // Pattern-only markets have no additional BIO outcome event.
        public static int BioSuccessChance(int seed, int turn) => 0;
        private static MarketData GenerateStockMarket(int seed, int turn, StockPatternLibrary library)
            => PatternMarketGenerator.Generate(seed, turn, library: library);
        private Dictionary<string, int> StockHoldings(string player)
            => stockPlayers.TryGetValue(player, out var p) ? p.Shares : new Dictionary<string, int>();
        private bool HasStockAugment(string player, string id) => stockPlayers[player].Augments.Contains(id);
        private void BeginStockTurn()
        {
            ProcessedDay = 0;
            liveTrading = false;
            ClearSwap(); // 교환 턴이면 GameRoom이 턴 시작 직후 다시 설정한다.
            BeginNewsTurn();
            foreach (var history in fills.Values) history.Clear();
            foreach (string id in players)
            {
                if (!stockPlayers.TryGetValue(id, out var p)) stockPlayers.Add(id, p = new StockPlayer());
                p.Discount = 0; p.PendingDiscount = 0;
                // The previous turn's profit is already in assets/history. Each new turn
                // starts with all thirty slots in BANK and every company supply unclaimed.
                foreach (var slot in p.Slots) { slot.Claim = null; slot.Active = false; slot.Plan = Array.Empty<Interval>(); }
                foreach (string company in InvestmentRules.CompanyIds) {
                    p.Shares[company] = 0;
                    Array.Clear(p.DailyQuantity[company], 0, 31); p.Gain[company] = 0;
                }
                p.BankPlan = Array.Empty<Interval>();
                p.DepositDays = 0;
                p.DraftSharesRemaining = StockRules.SharesPerPick;
                p.Chosen = true; p.Candidates = new string[0];
            }
        }
        public CompanyShareState[] GetShareMarket()
            => InvestmentRules.CompanyIds.Select(id => new CompanyShareState { companyId = id,
                totalShares = id == "BANK" ? StockRules.BankSupply : StockRules.Supply,
                fills = fills[id].ToArray(), owners = players.Select(player => PublicOwner(id, player)).ToArray() }).ToArray();
        private ShareOwner PublicOwner(string company, string player)
        {
            var p = stockPlayers[player];
            if (!liveTrading && (ProcessedDay > 0 || p.Shares.Values.Any(q => q > 0)))
            {
                if (company == "BANK") return new ShareOwner { playerId = player, shares = BankSlots(p) };
                int invested = p.Slots.Count(slot => slot.Claim == company && Invested(slot, ProcessedDay));
                return new ShareOwner { playerId = player, shares = invested, reserved = p.Shares[company] - invested };
            }
            return new ShareOwner { playerId = player, shares = p.Shares[company] };
        }
        private bool Invested(Slot slot, int day) => slot.Claim != null && (slot.Plan.Length == 0 ||
            slot.Plan.Any(i => i.entryDay <= day && day <= i.exitDay));
        private int BankSlots(StockPlayer p) => p.Slots.Count(slot => !Invested(slot, ProcessedDay));
        private static int ActiveShares(StockPlayer p, string company) =>
            p.Slots.Count(s => s.Active && (s.Claim ?? "BANK") == company);

        public void BeginLiveTrading() { liveTrading = true; }
        public RuleResult SetPosition(RequestContext context, ShareOrder order)
        {
            string error = CheckContext(context, Protocol.Investment);
            if (error != "") return new RuleResult(error);
            if (!liveTrading || allLocked || ProcessedDay >= 30) return new RuleResult("ORDER_LOCKED");
            var p = stockPlayers[context.PlayerId];
            if (order?.companyId == null || !p.Shares.ContainsKey(order.companyId)) return new RuleResult("INVALID_COMPANY");
            string company = order.companyId;
            int target = order.targetShares;
            int delta = target - p.Shares[company];
            if (target < 0 || target > StockRules.ShareLimit(p.Augments.ToArray(), company)) return new RuleResult("SHARE_LIMIT");
            if (delta == 0) return new RuleResult();
            if (delta > StockRules.Slots - p.Shares.Values.Sum()) return new RuleResult("PORTFOLIO_FULL");
            int supply = company == "BANK" ? StockRules.BankSupply : StockRules.Supply;
            if (delta > supply - stockPlayers.Values.Sum(owner => owner.Shares[company]))
            {
                if (p.Augments.Contains("OPPORTUNITY") && p.OpportunityTurn != turn) {
                    p.OpportunityTurn = turn; p.Discount = Math.Min(4, p.Discount + 2); p.PendingDiscount = 2;
                }
                return new RuleResult("INSUFFICIENT_SHARES");
            }
            var slots = delta > 0 ? p.Slots.Where(s => s.Claim == null).Take(delta)
                : p.Slots.Where(s => s.Claim == company).Skip(target);
            foreach (var slot in slots) {
                slot.Active = delta > 0;
                slot.Claim = slot.Active ? company : null;
            }
            p.Shares[company] = target;
            if (target == 0) p.Streak[company] = 0;
            fills[company].Add(new StockFill { playerId = context.PlayerId, day = ProcessedDay, quantity = delta });
            return new RuleResult();
        }
        public PortfolioState GetPortfolio(string playerId)
        {
            if (!stockPlayers.TryGetValue(playerId, out var p)) return new PortfolioState { holdings = new PortfolioHolding[0], augments = new string[0], candidates = new string[0], insights = new StockInsight[0] };
            return new PortfolioState { processedDay = ProcessedDay, usedSlots = p.Shares.Values.Sum(), informationDiscount = p.Discount, augmentChosen = p.Chosen,
                draftSharesRemaining = p.DraftSharesRemaining,
                priorityUsed = p.PriorityUsed, concentrationCompany = p.Focus, augments = p.Augments.ToArray(), candidates = (string[])p.Candidates.Clone(),
                holdings = p.Shares.Select(v => new PortfolioHolding { companyId = v.Key, shares = v.Value, activeShares = ActiveShares(p, v.Key), consecutiveTurns = p.Streak[v.Key] }).ToArray(),
                insights = Array.Empty<StockInsight>() };
        }
        public RuleResult TradeShares(RequestContext c, ShareOrder[] requested)
        {
            string error = CheckContext(c, Protocol.Selection); if (error != "") return new RuleResult(error);
            if (allLocked) return new RuleResult("ORDER_LOCKED");
            var p = stockPlayers[c.PlayerId];
            if (requested == null || requested.Length == 0 || requested.Length > InvestmentRules.CompanyIds.Length - 1 || requested.Any(v => v == null)) return new RuleResult("INVALID_SHARE_ORDER");
            if (requested.Select(v => v.companyId).Distinct().Count() != requested.Length) return new RuleResult("DUPLICATE_COMPANY");
            var candidate = new Dictionary<string, int>(p.Shares);
            foreach (var v in requested)
            {
                if (v.companyId == "BANK") return new RuleResult("AUTOMATIC_BANK");
                if (v.companyId == null || !candidate.ContainsKey(v.companyId)) return new RuleResult("INVALID_COMPANY");
                int cap = StockRules.ShareLimit(p.Augments.ToArray(), v.companyId);
                if (v.targetShares < 0 || v.targetShares > cap) return new RuleResult("SHARE_LIMIT");
                candidate[v.companyId] = v.targetShares;
            }
            if (candidate.Values.Sum() > StockRules.Slots) return new RuleResult("PORTFOLIO_FULL");
            int purchased = requested.Sum(v => Math.Max(0, v.targetShares - p.Shares[v.companyId]));
            if (purchased > p.DraftSharesRemaining) return new RuleResult("DRAFT_SHARE_LIMIT");
            var plans = GetReservations(c.PlayerId);
            foreach (var v in requested)
            {
                var ranges = plans.FirstOrDefault(i => i.companyId == v.companyId)?.intervals;
                if (v.targetShares > 0 && ranges?.Length > 0 && ranges.All(i => i.exitDay <= ProcessedDay))
                    return new RuleResult("INVALID_DAY");
            }
            foreach (var v in requested)
                if (players.Where(id => id != c.PlayerId).Sum(id => stockPlayers[id].Shares[v.companyId]) + v.targetShares > StockRules.Supply)
                {
                    if (p.Augments.Contains("OPPORTUNITY") && p.OpportunityTurn != turn)
                    { p.OpportunityTurn = turn; p.Discount = Math.Min(4, p.Discount + 2); p.PendingDiscount = 2; }
                    return new RuleResult("INSUFFICIENT_SHARES");
                }
            foreach (var v in requested)
            {
                foreach (var slot in p.Slots.Where(x => x.Claim == v.companyId).Skip(v.targetShares))
                { slot.Claim = null; slot.Plan = Array.Empty<Interval>(); }
            }
            foreach (var v in requested)
            {
                int existing = p.Slots.Count(x => x.Claim == v.companyId);
                foreach (var slot in p.Slots.Where(x => x.Claim == null).Take(v.targetShares - existing))
                    slot.Claim = v.companyId;
                var plan = plans.FirstOrDefault(i => i.companyId == v.companyId)?.intervals ?? Array.Empty<Interval>();
                foreach (var slot in p.Slots.Where(x => x.Claim == v.companyId)) slot.Plan = plan;
                p.Shares[v.companyId] = v.targetShares;
                if (v.targetShares == 0) p.Streak[v.companyId] = 0;
            }
            p.DraftSharesRemaining -= purchased;
            return new RuleResult();
        }
        public void BeginDraftPick(string playerId) => stockPlayers[playerId].DraftSharesRemaining = StockRules.SharesPerPick;
        public void BeginAugmentSelection()
        {
            foreach (var id in players)
            {
                var p = stockPlayers[id];
                var random = new Random(unchecked(stockSeed ^ turn * 7919 ^ Array.IndexOf(players, id) * 397));
                p.Candidates = StockRules.Augments.Where(a => !p.Augments.Contains(a)).OrderBy(a => random.Next()).Take(3).ToArray();
                p.Chosen = false;
            }
        }
        public RuleResult ChooseAugment(RequestContext c, string id, string company)
        {
            string error = CheckContext(c, StockRules.AugmentPhase); if (error != "") return new RuleResult(error);
            var p = stockPlayers[c.PlayerId];
            if (p.Chosen) return new RuleResult("AUGMENT_ALREADY_CHOSEN");
            if (id == null || !p.Candidates.Contains(id)) return new RuleResult("INVALID_AUGMENT");
            p.Augments.Add(id); p.Chosen = true;
            return new RuleResult();
        }
        public bool AllAugmentsChosen => players.All(id => stockPlayers[id].Chosen);
        public void CompleteAugmentSelection()
        {
            foreach (var id in players.Where(id => !stockPlayers[id].Chosen))
                ChooseAugment(new RequestContext("", turn, StockRules.AugmentPhase, id), stockPlayers[id].Candidates[0], "AI");
        }
        public RuleResult ConsumePriority(string playerId)
        {
            var p = stockPlayers[playerId];
            if (!p.Augments.Contains("PRIORITY") || p.PriorityUsed) return new RuleResult("PRIORITY_UNAVAILABLE");
            p.PriorityUsed = true; return new RuleResult();
        }
        public void SimulateDay()
        {
            if (ProcessedDay >= 30) return;
            int day = ++ProcessedDay;
            if (NewsEnabled && liveTrading) ApplyLiveMoves(day); // 실시간 시장: 오늘의 군중 반응·추세·속보를 시세에 더한다
            foreach (var p in stockPlayers.Values)
            {
                foreach (var slot in p.Slots)
                {
                    string company = liveTrading ? slot.Active ? slot.Claim ?? "BANK" : "BANK" : Invested(slot, day) ? slot.Claim : "BANK";
                    // Outside the BANK reservation, idle slots remain cash without market returns.
                    if (!liveTrading && company == "BANK" && p.BankPlan.Length > 0 && !p.BankPlan.Any(i => i.entryDay <= day && day <= i.exitDay)) continue;
                    // Each slot represents a fixed share of the turn's starting capital.
                    // Sum chart percentage-point differences across held days, without price ratios or compounding.
                    decimal change = (market.ChangeBps(company, day) - market.ChangeBps(company, day - 1)) / 10000m;
                    // Hidden protection changes the holder's daily return, never the shared chart or its prelude.
                    if (day >= 26 && company != "BANK" && p.Augments.Contains("HIDDEN_LOSS_CONTROL"))
                        change *= change < 0 ? .2m : .5m;
                    if (company == "BANK") p.DepositDays++;
                    if (company == "BANK" && p.Augments.Contains("CASH_INTEREST")) change += .005m;
                    p.Gain[company] += change;
                    p.DailyQuantity[company][day]++;
                    if (!liveTrading && slot.Claim != null && slot.Plan.Length > 0 && slot.Plan.Max(i => i.exitDay) == day)
                    {
                        p.Shares[slot.Claim]--;
                        if (p.Shares[slot.Claim] == 0) p.Streak[slot.Claim] = 0;
                        slot.Claim = null;
                        slot.Plan = Array.Empty<Interval>();
                    }
                }
            }
        }
        private static Interval[] HeldIntervals(int[] quantities)
        {
            var result = new List<Interval>();
            for (int day = 1; day <= 30; day++)
            {
                if (quantities[day] == 0) continue;
                int first = day;
                while (day < 30 && quantities[day + 1] > 0) day++;
                result.Add(new Interval { entryDay = first, exitDay = day });
            }
            return result.ToArray();
        }
        public void LockPortfolios()
        {
            if (allLocked) return;
            if (!liveTrading) foreach (var id in players) ApplyStockPlans(id);
            while (ProcessedDay < 30) SimulateDay();
            foreach (var id in players)
                orders[id] = stockPlayers[id].DailyQuantity.Where(v => v.Value.Max() > 0).Select(v => new Investment {
                    companyId = v.Key, weightBps = v.Value.Max() * 10000 / StockRules.Slots, intervals = HeldIntervals(v.Value) }).ToArray();
            stockReturns[turn] = players.ToDictionary(id => id, id => stockPlayers[id].Gain.ToDictionary(
                v => v.Key, v => StockCompanyRate(id, v.Key, (int)Math.Round(10000m * v.Value / Math.Max(1, stockPlayers[id].DailyQuantity[v.Key].Max())))));
            stockHistory[turn] = players.ToDictionary(id => id, id => stockPlayers[id].DailyQuantity.ToDictionary(v => v.Key, v => v.Value.Max()));
            allLocked = true;
        }
        private Investment[] BuildStockInvestments(string playerId)
        {
            var plans = GetReservations(playerId);
            return StockHoldings(playerId).Where(v => v.Value > 0 && v.Key != "BANK").Select(v => new Investment {
                companyId = v.Key,
                intervals = plans.FirstOrDefault(i => i.companyId == v.Key)?.intervals is Interval[] ranges && ranges.Length > 0
                    ? ranges : Array.Empty<Interval>()
            }).Concat(plans.Where(i => i.companyId == "BANK")).ToArray();
        }
        private void ApplyStockPlans(string playerId)
        {
            var plans = BuildStockInvestments(playerId);
            stockPlayers[playerId].BankPlan = plans.FirstOrDefault(i => i.companyId == "BANK")?.intervals ?? Array.Empty<Interval>();
            foreach (var slot in stockPlayers[playerId].Slots.Where(s => s.Claim != null))
                slot.Plan = plans.Single(i => i.companyId == slot.Claim).intervals;
        }
        private readonly Dictionary<int, Dictionary<string, Dictionary<string, int>>> stockReturns = new Dictionary<int, Dictionary<string, Dictionary<string, int>>>();
        private int StockHistoryRate(string player, string company, int requestedTurn) => stockReturns[requestedTurn][player][company];
        private int StockSharesForHistory(string player, string company, int requestedTurn)
            => stockHistory.TryGetValue(requestedTurn, out var snapshot) ? snapshot[player][company] : stockPlayers[player].Shares[company];
        private int StockCompanyRate(string player, string company, int raw)
        {
            var p = stockPlayers[player]; int rate = Math.Max(-10000, raw);
            if (p.Augments.Contains("DIVERSIFY") && p.DailyQuantity.Values.Count(q => q[30] >= 3) >= 3 && rate < 0) rate = rate * 80 / 100;
            if (p.Augments.Contains("LONG_TERM") && p.DailyQuantity[company].Skip(1).Count(q => q > 0) >= 10 && rate > 0) rate *= 3;
            return rate;
        }
        private PlayerSettlement SettleStockPlayer(string player)
        {
            long investable = turnStartAssets[player] - costs[player], profit = 0;
            var p = stockPlayers[player];
            var result = new PlayerSettlement { playerId = player, startingAssets = turnStartAssets[player] };
            foreach (var v in p.DailyQuantity.Where(v => v.Value.Max() > 0))
            {
                long allocated = investable * v.Value.Max() / StockRules.Slots;
                long rawProfit = (long)Math.Round(investable * p.Gain[v.Key] / StockRules.Slots, MidpointRounding.AwayFromZero);
                int rawRate = (int)Math.Round(10000m * p.Gain[v.Key] / v.Value.Max());
                int rate = StockCompanyRate(player, v.Key, rawRate);
                long amount = rawProfit + Settlement.Profit(allocated, rate - rawRate);
                result.companyProfits.Add(new CompanyProfit { companyId = v.Key, allocatedAssets = allocated, profit = amount, returnBps = rate });
                profit += amount;
            }
            result.endingAssets = Math.Max(0, investable + profit); result.profit = result.endingAssets - result.startingAssets;
            result.turnReturnBps = Settlement.ReturnBps(result.startingAssets, result.endingAssets);
            return result;
        }
        public int InformationDiscount(string playerId) => stockPlayers.TryGetValue(playerId, out var p) ? p.Discount : 0;
        public void ConsumeInformationDiscount(string playerId, int days) => stockPlayers[playerId].Discount -= days;
        public VisibleInformationWindow[] AnalysisWindows(string playerId)
        {
            if (turn <= 1 || !stockPlayers.TryGetValue(playerId, out var p)) return new VisibleInformationWindow[0];
            var previous = markets[turn - 1];
            return InvestmentRules.CompanyIds.Where(id =>
                p.Augments.Contains("TREND") && Enumerable.Range(28, 3).All(d => previous.ChangeBps(id, d) > previous.ChangeBps(id, d - 1)))
                .Select(id => new VisibleInformationWindow { companyId = id, recordId = "augment-" + turn + "-" + playerId + "-" + id, source = "AUGMENT", startDay = 1,
                    dailyChangesBps = GetWindow(turn, id, 1, 3), dailyOpenBps = GetOpenWindow(turn, id, 1, 3) }).ToArray();
        }
    }
}
