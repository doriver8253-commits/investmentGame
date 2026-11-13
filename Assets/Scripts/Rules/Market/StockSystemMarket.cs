using System;
using System.Collections.Generic;
using System.Linq;
using BlackWhale.Shared;

namespace BlackWhale.Rules
{
    public sealed partial class MarketService : IStockMarketSystem
    {
        private sealed class Slot
        {
            public string Claim;
            public Interval[] Plan = Array.Empty<Interval>();
            public decimal Value = 1;
        }
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
        public bool StockEnabled { get; private set; }
        public static MarketService CreateStockSystem(int seed)
        {
            var rules = new InvestmentRules { entryAtOpen = true, minHoldingDays = 1, maxHoldingDays = 30, maxIntervalsPerCompany = 0 };
            return new MarketService(t => GenerateStockMarket(seed, t), rules) { StockEnabled = true, stockSeed = seed, UsesWindowInformation = true };
        }
        private static uint StockHash(int seed, int turn, uint salt)
        {
            unchecked { uint x = (uint)seed ^ (uint)turn * 2654435761U ^ salt; x ^= x >> 16; x *= 2246822519U; x ^= x >> 13; x *= 3266489917U; return x ^ (x >> 16); }
        }
        public static int BioSuccessChance(int seed, int turn) => 3500 + (int)(StockHash(seed, turn, 17) % 4001);
        private static MarketData GenerateStockMarket(int seed, int turn)
        {
            var data = PatternMarketGenerator.Generate(seed, turn);
            var bank = data.companies.Single(c => c.companyId == "BANK");
            bank.dailyChangesBps = bank.dailyChangesBps.Select(v => (int)(200.0 * Math.Tanh(v / 2500.0))).ToArray();
            var bio = data.companies.Single(c => c.companyId == "BIO");
            int effect = StockHash(seed, turn, 53) % 10000 < BioSuccessChance(seed, turn) ? 1800 : -1600;
            var raw = bio.dailyChangesBps.Select((v, d) => v + effect * Math.Max(0, d - 12) / 18.0).ToArray();
            double scale = 1; int limit = RandomMarketGenerator.DailyLimitBps("BIO", turn);
            for (int d = 1; d <= 30; d++)
            {
                if (raw[d] != 0) scale = Math.Min(scale, 7600 / Math.Abs(raw[d]));
                if (raw[d] != raw[d - 1]) scale = Math.Min(scale, (limit - 1) / Math.Abs(raw[d] - raw[d - 1]));
            }
            bio.dailyChangesBps = raw.Select(v => (int)Math.Round(v * scale)).ToArray();
            return data;
        }
        private Dictionary<string, int> StockHoldings(string player)
            => stockPlayers.TryGetValue(player, out var p) ? p.Shares : new Dictionary<string, int>();
        private bool HasStockAugment(string player, string id) => stockPlayers[player].Augments.Contains(id);
        private void BeginStockTurn()
        {
            ProcessedDay = 0;
            foreach (string id in players)
            {
                if (!stockPlayers.TryGetValue(id, out var p)) stockPlayers.Add(id, p = new StockPlayer());
                p.Discount = p.PendingDiscount + (p.Augments.Contains("ANALYST") ? 2 : 0); p.PendingDiscount = 0;
                // The previous turn's profit is already in assets/history. Each new turn
                // starts with all thirty slots in BANK and every company supply unclaimed.
                foreach (var slot in p.Slots) { slot.Claim = null; slot.Value = 1; slot.Plan = Array.Empty<Interval>(); }
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
                owners = players.Select(player => PublicOwner(id, player)).ToArray() }).ToArray();
        private ShareOwner PublicOwner(string company, string player)
        {
            var p = stockPlayers[player];
            if (company == "BANK") return new ShareOwner { playerId = player, shares = BankSlots(p) };
            int owned = p.Slots.Count(s => s.Claim == company && Invested(s, ProcessedDay));
            return new ShareOwner { playerId = player, shares = owned, reserved = p.Shares[company] - owned };
        }
        private bool Invested(Slot slot, int day) => slot.Claim != null && (slot.Plan.Length == 0 ||
            slot.Plan.Any(i => i.entryDay <= day && day <= i.exitDay));
        private int BankSlots(StockPlayer p) => p.Slots.Count(slot => !Invested(slot, ProcessedDay));
        public PortfolioState GetPortfolio(string playerId)
        {
            if (!stockPlayers.TryGetValue(playerId, out var p)) return new PortfolioState { holdings = new PortfolioHolding[0], augments = new string[0], candidates = new string[0], insights = new StockInsight[0] };
            return new PortfolioState { usedSlots = p.Shares.Values.Sum(), informationDiscount = p.Discount, augmentChosen = p.Chosen,
                draftSharesRemaining = p.DraftSharesRemaining,
                priorityUsed = p.PriorityUsed, concentrationCompany = p.Focus, augments = p.Augments.ToArray(), candidates = (string[])p.Candidates.Clone(),
                holdings = p.Shares.Select(v => new PortfolioHolding { companyId = v.Key, shares = v.Value, consecutiveTurns = p.Streak[v.Key] }).ToArray(),
                insights = p.Augments.Contains("BIO_RESEARCH") ? new[] { new StockInsight { companyId = "BIO", probabilityBps = BioSuccessChance(stockSeed, turn),
                    message = "BIO 연구 이벤트 성공 가능성 · 최종 수익 보장 아님" } } : new StockInsight[0] };
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
                int cap = p.Focus == v.companyId ? 12 : StockRules.PersonalLimit;
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
                p.Candidates = StockRules.Augments.Where(a => a != "FLEXIBLE" && !p.Augments.Contains(a)).OrderBy(a => random.Next()).Take(3).ToArray();
                p.Chosen = false;
            }
        }
        public RuleResult ChooseAugment(RequestContext c, string id, string company)
        {
            string error = CheckContext(c, StockRules.AugmentPhase); if (error != "") return new RuleResult(error);
            var p = stockPlayers[c.PlayerId];
            if (p.Chosen) return new RuleResult("AUGMENT_ALREADY_CHOSEN");
            if (id == null || !p.Candidates.Contains(id)) return new RuleResult("INVALID_AUGMENT");
            if (id == "CONCENTRATION" && (company == "BANK" || !InvestmentRules.CompanyIds.Contains(company))) return new RuleResult("INVALID_COMPANY");
            p.Augments.Add(id); p.Chosen = true;
            if (id == "ANALYST") p.Discount = Math.Min(4, p.Discount + 2);
            if (id == "CONCENTRATION") p.Focus = company;
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
            foreach (var p in stockPlayers.Values)
            {
                foreach (var slot in p.Slots)
                {
                    string company = Invested(slot, day) ? slot.Claim : "BANK";
                    // Outside the BANK reservation, idle slots remain cash without market returns.
                    if (company == "BANK" && p.BankPlan.Length > 0 && !p.BankPlan.Any(i => i.entryDay <= day && day <= i.exitDay)) continue;
                    decimal open = 10000 + market.ChangeBps(company, day - 1);
                    decimal close = 10000 + market.ChangeBps(company, day);
                    decimal change = slot.Value * (close / open - 1);
                    if (company == "BANK") p.DepositDays += slot.Value;
                    p.Gain[company] += change;
                    slot.Value += change;
                    p.DailyQuantity[company][day]++;
                    if (slot.Claim != null && slot.Plan.Length > 0 && slot.Plan.Max(i => i.exitDay) == day)
                    {
                        p.Shares[slot.Claim]--;
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
            foreach (var id in players) ApplyStockPlans(id);
            while (ProcessedDay < 30) SimulateDay();
            foreach (var id in players)
                orders[id] = stockPlayers[id].DailyQuantity.Where(v => v.Value.Max() > 0).Select(v => new Investment {
                    companyId = v.Key, weightBps = v.Value.Max() * 10000 / StockRules.Slots, intervals = HeldIntervals(v.Value) }).ToArray();
            stockReturns[turn] = players.ToDictionary(id => id, id => stockPlayers[id].Gain.ToDictionary(
                v => v.Key, v => (int)Math.Round(10000m * v.Value / Math.Max(1, stockPlayers[id].DailyQuantity[v.Key].Max()))));
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
        /// <summary>
        /// A company counts as invested this turn when at least one share was actually held on
        /// at least one day. Planned exits before day 30 no longer break the streak.
        /// </summary>
        private static bool HeldThisTurn(StockPlayer p, string company) => company != "BANK" && p.DailyQuantity[company].Max() > 0;
        /// <summary>Consecutive turns including the current one (used by SPACE_PIONEER / LONG_TERM).</summary>
        private static int StreakIncludingThisTurn(StockPlayer p, string company) => HeldThisTurn(p, company) ? p.Streak[company] + 1 : 0;
        /// <summary>
        /// DIVERSIFY: on at least one day, three or more companies were held at the same time
        /// with three or more shares each (uses daily holdings, not shares left at settlement).
        /// </summary>
        private static bool DiversifiedOnSomeDay(StockPlayer p)
            => Enumerable.Range(1, 30).Any(day => InvestmentRules.CompanyIds.Count(id => id != "BANK" && p.DailyQuantity[id][day] >= 3) >= 3);
        /// <summary>Called once per settled turn, after the settlement results are computed.</summary>
        private void AdvanceStockStreaks(string player)
        {
            var p = stockPlayers[player];
            foreach (var company in InvestmentRules.CompanyIds) p.Streak[company] = StreakIncludingThisTurn(p, company);
        }
        private int StockCompanyRate(string player, string company, int raw)
        {
            var p = stockPlayers[player]; int rate = Math.Max(-10000, raw);
            if (p.Augments.Contains("DIVERSIFY") && DiversifiedOnSomeDay(p) && rate < 0) rate = rate * 80 / 100;
            if (p.Augments.Contains("SPACE_PIONEER") && company == "SPACE" && StreakIncludingThisTurn(p, company) >= 2) rate += 200;
            if (p.Augments.Contains("LONG_TERM") && StreakIncludingThisTurn(p, company) >= 3) rate += 200;
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
            if (p.Augments.Contains("CASH_INTEREST"))
                profit += Math.Min((long)(investable * p.DepositDays / (30 * StockRules.Slots) * .02m), investable / 100);
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
                p.Augments.Contains("CONTRARIAN") && previous.ChangeBps(id, 30) < 0 ||
                p.Augments.Contains("TREND") && Enumerable.Range(28, 3).All(d => previous.ChangeBps(id, d) > previous.ChangeBps(id, d - 1)))
                .Select(id => new VisibleInformationWindow { companyId = id, recordId = "augment-" + turn + "-" + playerId + "-" + id, source = "AUGMENT", startDay = 1,
                    dailyChangesBps = GetWindow(turn, id, 1, 3), dailyOpenBps = GetOpenWindow(turn, id, 1, 3) }).ToArray();
        }
    }
}
