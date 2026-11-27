using System;
using System.Collections.Generic;
using System.Linq;
using BlackWhale.Shared;

namespace BlackWhale.Bots
{
    public enum BotDifficulty { Easy, Normal, Hard }
    public enum BotPersonality { Adventurous, Stable, Strategic }
    public sealed class BotPolicy
    {
        public BotDifficulty Difficulty { get; }
        public int PurchaseBudget { get; }
        public int MaxIntervals { get; }
        public BotPersonality Personality { get; }
        public double PredictionNoise => Difficulty == BotDifficulty.Easy ? 1800 : Difficulty == BotDifficulty.Normal ? 650 : 120;
        public int TimingNoiseDays => Difficulty == BotDifficulty.Easy ? 3 : Difficulty == BotDifficulty.Normal ? 1 : 0;
        public double CompetitionNoise => Difficulty == BotDifficulty.Easy ? 1 : Difficulty == BotDifficulty.Normal ? .4 : .08;
        public double ReactionNoiseSeconds => Difficulty == BotDifficulty.Easy ? 4 : Difficulty == BotDifficulty.Normal ? 1.5 : .3;
        public BotPolicy(BotDifficulty difficulty, BotPersonality personality = BotPersonality.Strategic)
        {
            if (!Enum.IsDefined(typeof(BotDifficulty), difficulty)) throw new ArgumentOutOfRangeException(nameof(difficulty));
            if (!Enum.IsDefined(typeof(BotPersonality), personality)) throw new ArgumentOutOfRangeException(nameof(personality));
            Difficulty = difficulty; Personality = personality; PurchaseBudget = 3; MaxIntervals = 2;
        }
    }
    public sealed class InformationPurchase
    { public string CompanyId; public int BundleIndex, StartDay, EndDay; }
    // Only references Shared: no server services, fixture data, Unity or clock.
    public sealed class InformationBotStrategy
    {
        private readonly Random random;
        public BotPolicy Policy { get; }
        public InformationBotStrategy(int seed, BotPolicy policy)
        { random = new Random(seed); Policy = policy ?? throw new ArgumentNullException(nameof(policy)); }

        private double Noise(double amplitude) => (random.NextDouble() * 2 - 1) * amplitude;
        public float NextReactionSeconds() => (float)(random.NextDouble() * Policy.ReactionNoiseSeconds);
        private double Preference(string id)
        {
            if (Policy.Personality == BotPersonality.Adventurous)
                return id == "AI" || id == "BIO" || id == "SPACE" ? 700 : -300;
            if (Policy.Personality == BotPersonality.Stable)
                return id == "FOOD" || id == "AUTO" ? 900 : -600;
            return 0;
        }
        // Extrapolate only from this bot's visible samples, never the server's hidden chart.
        private double Forecast(VisibleCompany company, int day)
        {
            var days = KnownDays(company);
            day = Math.Max(0, Math.Min(30, day));
            if (days[day].HasValue) return days[day].Value;
            int left = day, right = day;
            while (left > 0 && !days[left].HasValue) left--;
            while (right < 30 && !days[right].HasValue) right++;
            if (days[left].HasValue && days[right].HasValue && right > left)
                return days[left].Value + (days[right].Value - days[left].Value) * (day - left) / (double)(right - left);
            int previous = left - 1;
            while (previous >= 0 && !days[previous].HasValue) previous--;
            double slope = previous >= 0 && days[left].HasValue ? (days[left].Value - days[previous].Value) / (double)(left - previous) : 0;
            return (days[left] ?? 0) + slope * Math.Min(5, day - left);
        }
        private double BankGain(PlayerView view, int day, int horizon)
        {
            var bank = view.companies?.FirstOrDefault(c => c.companyId == "BANK");
            double interest = view.portfolio?.augments?.Contains("CASH_INTEREST") == true ? 50 * (horizon - day) : 0;
            return Forecast(bank, horizon) - Forecast(bank, day) + interest;
        }
        private double StockScore(PlayerView view, PublicState state, string id)
        {
            var company = view.companies?.FirstOrDefault(c => c.companyId == id);
            int day = view.portfolio?.processedDay ?? 0;
            int horizon = Math.Min(30, day + 5);
            double gain = Forecast(company, horizon) - Forecast(company, day) - BankGain(view, day, horizon);
            var market = state.shareMarket?.FirstOrDefault(c => c.companyId == id);
            double occupied = (market?.owners?.Where(o => o.playerId != view.recipientPlayerId).Sum(o => o.shares + o.reserved) ?? 0) / (double)StockRules.Supply;
            // Scarce shares receive an urgency premium, blurred by competition judgement noise.
            return gain + Preference(id) + 350 * Math.Max(0, occupied + Noise(Policy.CompetitionNoise)) + Noise(Policy.PredictionNoise);
        }
        private string rankingKey;
        private string[] ranking;
        public string[] RankStockCompanies(PlayerView view, PublicState state)
        {
            string key = view.turn + "|" + state.developerRevision + "|" + state.phase + "|" + state.selectionIndex + "|" + view.portfolio?.processedDay;
            if (rankingKey == key) return (string[])ranking.Clone();
            rankingKey = key;
            ranking = (state.shareMarket ?? Array.Empty<CompanyShareState>()).Where(c => c.companyId != "BANK")
                .Select(c => new { Id = c.companyId, Score = StockScore(view, state, c.companyId) })
                .OrderByDescending(c => c.Score).ThenBy(c => c.Id).Select(c => c.Id).ToArray();
            return (string[])ranking.Clone();
        }
        public string SelectAugment(PlayerView view)
        {
            string[] preference = Policy.Personality == BotPersonality.Adventurous
                ? new[] { "BIO_RESEARCH", "SPACE_PIONEER", "CONCENTRATION", "LONG_TERM", "ANALYST", "HIDDEN_LOSS_CONTROL", "DIVERSIFY", "CASH_INTEREST" }
                : Policy.Personality == BotPersonality.Stable
                ? new[] { "CASH_INTEREST", "HIDDEN_LOSS_CONTROL", "DIVERSIFY", "LONG_TERM", "ANALYST", "CONCENTRATION", "BIO_RESEARCH", "SPACE_PIONEER" }
                : new[] { "ANALYST", "DIVERSIFY", "LONG_TERM", "CONCENTRATION", "HIDDEN_LOSS_CONTROL", "BIO_RESEARCH", "SPACE_PIONEER", "CASH_INTEREST" };
            return preference.FirstOrDefault(id => view.portfolio.candidates.Contains(id)) ?? view.portfolio.candidates.FirstOrDefault();
        }
        private sealed class StockDecision { public bool Buy; public float ReadyAt; }
        private readonly Dictionary<string, StockDecision> stockDecisions = new Dictionary<string, StockDecision>();
        private int decisionTurn = -1, decisionRevision = -1;
        public int StockTarget(PlayerView view, PublicState state, string id, float now)
        {
            var portfolio = view.portfolio;
            var holding = portfolio.holdings.First(h => h.companyId == id);
            var market = state.shareMarket.First(c => c.companyId == id);
            int day = portfolio.processedDay;
            if (decisionTurn != view.turn || decisionRevision != state.developerRevision) {
                stockDecisions.Clear(); decisionTurn = view.turn; decisionRevision = state.developerRevision;
            }
            string key = id + ":" + day;
            if (!stockDecisions.TryGetValue(key, out var decision)) {
                int offset = random.Next(-Policy.TimingNoiseDays, Policy.TimingNoiseDays + 1);
                var company = view.companies.FirstOrDefault(c => c.companyId == id);
                int horizon = Math.Min(30, day + 3 + offset);
                double gain = Forecast(company, horizon) - Forecast(company, day) - BankGain(view, day, horizon);
                double occupied = (market.owners?.Where(o => o.playerId != view.recipientPlayerId).Sum(o => o.shares + o.reserved) ?? 0) / (double)StockRules.Supply;
                double urgency = gain > 0 ? 350 * Math.Max(0, occupied + Noise(Policy.CompetitionNoise)) : 0;
                double hiddenBias = day >= 25 ? (Policy.Personality == BotPersonality.Adventurous ? 700
                    : Policy.Personality == BotPersonality.Stable && id != "FOOD" && id != "AUTO" ? -1400 : 0) : 0;
                bool buy = gain + Preference(id) + hiddenBias + urgency + Noise(Policy.PredictionNoise) > 0;
                // 교환 턴: 남의 계좌를 운용하므로 내릴 것 같은 기업을 사고 BANK는 비운다.
                if (state.swapTurn) buy = id != "BANK" && gain + Noise(Policy.PredictionNoise) < 0;
                decision = new StockDecision { Buy = buy, ReadyAt = now + NextReactionSeconds() };
                stockDecisions.Add(key, decision);
            }
            if (now < decision.ReadyAt) return holding.activeShares;
            return decision.Buy ? holding.activeShares + portfolio.AvailableToBuy(market) : 0;
        }

        public static int?[] KnownDays(VisibleCompany company)
        {
            return InformationWindowRules.KnownDays(company);
        }
        public InformationPurchase NextPublicChoice(PlayerView view, PublicState state)
        {
            foreach (var company in view.companies.OrderBy(c => (state.publicInformation ?? Array.Empty<PublicInformationChoice>()).Count(w => w.companyId == c.companyId)))
            {
                if (state.stockSystem) {
                    if (InformationWindowRules.CanRevealPublic(state, company.companyId)) return new InformationPurchase { CompanyId = company.companyId };
                    continue;
                }
                for (int start = 1; start <= 26; start++)
                    if (!InformationWindowRules.OverlapsPublic(state, company.companyId, start))
                        return new InformationPurchase { CompanyId = company.companyId, StartDay = start };
            }
            return null;
        }
        public InformationPurchase NextRandomPurchase(PlayerView view, LobbySettings settings)
        {
            if (view == null || view.coins <= 0) return null;
            var options = settings ?? new LobbySettings();
            int first = Math.Max(1, options.informationStartDay);
            var candidates = (view.companies ?? Array.Empty<VisibleCompany>()).Where(c => c.companyId != "BANK")
                .Select(c => new { Company = c.companyId, Unknown = Enumerable.Range(first, Math.Max(0, options.informationEndDay - first + 1))
                    .Count(day => !InformationWindowRules.KnownDays(c)[day].HasValue) }).Where(c => c.Unknown > 0).ToArray();
            if (candidates.Length == 0) return null;
            var choices = candidates.OrderByDescending(c => c.Unknown * 100 + Preference(c.Company) + Noise(Policy.PredictionNoise)).Take(1).ToArray();
            return new InformationPurchase { CompanyId = choices[random.Next(choices.Length)].Company };
        }
        public InformationPurchase NextPurchase(PlayerView view, ISet<string> attempted)
        {
            if (view == null || view.coins <= 0 && (view.ability?.freeInformationCredits ?? 0) == 0) return null;
            var candidates = new List<Tuple<InformationPurchase, int>>();
            foreach (var company in view.companies ?? Array.Empty<VisibleCompany>())
            {
                var days = KnownDays(company);
                if (company.visibleWindows != null)
                {
                    for (int start = 1; start <= 30; start++)
                    {
                        if (attempted.Contains(company.companyId + ":" + start)) continue;
                        int budget = view.coins + (view.ability?.freeInformationCredits ?? 0);
                        int end = Math.Min(30, start + Math.Min(5, budget) - 1);
                        int newDays = InformationWindowRules.NewDays(company, start, end);
                        if (newDays == 0) continue;
                        int potential = newDays;
                        for (int d = start; d <= end; d++)
                            if (!days[d].HasValue)
                                for (int other = 0; other <= 30; other++)
                                    if (days[other].HasValue && Math.Abs(d - other) >= 5 && Math.Abs(d - other) <= 10) potential++;
                        candidates.Add(Tuple.Create(new InformationPurchase { CompanyId = company.companyId, StartDay = start, EndDay = end }, potential));
                    }
                    continue;
                }
                for (int b = 0; b < 6; b++)
                {
                    if ((company.visibleBundles ?? Array.Empty<VisibleBundle>()).Any(v => v.bundleIndex == b) || attempted.Contains(company.companyId + ":" + b)) continue;
                    int potential = 0;
                    for (int day = b * 5 + 1; day <= b * 5 + 5; day++)
                        for (int other = 0; other <= 30; other++)
                            if (days[other].HasValue && Math.Abs(day - other) >= 5 && Math.Abs(day - other) <= 10) potential++;
                    candidates.Add(Tuple.Create(new InformationPurchase { CompanyId = company.companyId, BundleIndex = b }, potential));
                }
            }
            if (candidates.Count == 0) return null;
            int best = candidates.Max(c => c.Item2);
            var ties = candidates.Where(c => c.Item2 == best).ToArray();
            return ties[random.Next(ties.Length)].Item1;
        }
        private sealed class Plan
        { public string Company; public long Score; public Interval[] Intervals; }
        private Plan Evaluate(PlayerView view, string companyId)
        {
            var days = KnownDays(view?.companies?.FirstOrDefault(c => c.companyId == companyId));
            var entryValues = view?.entryAtOpen == true ? InformationWindowRules.KnownOpens(view.companies?.FirstOrDefault(c => c.companyId == companyId)) : days;
            var candidates = new List<Plan>();
            for (int entry = 0; entry <= (view?.entryAtOpen == true ? 26 : 25); entry++)
                for (int exit = entry + 5 - (view?.entryAtOpen == true && entry > 0 ? 1 : 0); exit <= Math.Min(30, entry + 10 - (view?.entryAtOpen == true && entry > 0 ? 1 : 0)); exit++)
                    if (entryValues[entry].HasValue && days[exit].HasValue)
                        candidates.Add(new Plan { Company = companyId, Score = (long)(days[exit].Value - entryValues[entry].Value + Noise(Policy.PredictionNoise)),
                            Intervals = new[] { new Interval { entryDay = entry, exitDay = exit } } });
            if (candidates.Count == 0) return new Plan { Company = companyId, Score = 0,
                Intervals = new[] { new Interval { entryDay = 0, exitDay = 5 } } };
            var best = candidates.OrderByDescending(c => c.Score).First();
            if (Policy.MaxIntervals == 2)
                foreach (var a in candidates)
                    foreach (var b in candidates)
                        if ((a.Intervals[0].exitDay < b.Intervals[0].entryDay || view?.entryAtOpen != true && a.Intervals[0].exitDay == b.Intervals[0].entryDay) && a.Score + b.Score > best.Score)
                            best = new Plan { Company = companyId, Score = a.Score + b.Score, Intervals = new[] { a.Intervals[0], b.Intervals[0] } };
            return best;
        }
        public string SelectCompany(PlayerView view, PublicState state)
        {
            var own = view?.selectedCompanies ?? Array.Empty<string>();
            var plans = (state?.companySeats ?? Array.Empty<CompanySeat>())
                .Where(s => s.remainingSeats > 0 && !own.Contains(s.companyId)).Select(s => Evaluate(view, s.companyId)).ToArray();
            if (plans.Length == 0) return null;
            foreach (var plan in plans) plan.Score += (long)(Preference(plan.Company) + Noise(Policy.PredictionNoise));
            long best = plans.Max(p => p.Score);
            var ties = plans.Where(p => p.Score == best).ToArray();
            return ties[random.Next(ties.Length)].Company;
        }
        public Investment[] BuildOrder(PlayerView view, bool fallback = false)
        {
            var selected = view?.selectedCompanies;
            if (selected == null || selected.Length != 2 || selected[0] == selected[1]) return null;
            var plans = selected.Select(c => Evaluate(view, c)).ToArray();
            if (!fallback) plans = plans.OrderByDescending(p => p.Score).ToArray();
            int firstWeight = !fallback && plans[0].Score > plans[1].Score ? 7000 : 5000;
            return plans.Select((p, i) => new Investment { companyId = p.Company, weightBps = i == 0 ? firstWeight : 10000 - firstWeight,
                intervals = fallback ? new[] { new Interval { entryDay = 0, exitDay = 5 } } : p.Intervals.Select(t =>
                    new Interval { entryDay = t.entryDay, exitDay = t.exitDay }).ToArray() }).ToArray();
        }
    }
}
