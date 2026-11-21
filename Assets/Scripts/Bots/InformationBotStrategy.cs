using System;
using System.Collections.Generic;
using System.Linq;
using BlackWhale.Shared;

namespace BlackWhale.Bots
{
    public enum BotDifficulty { Easy, Normal, Hard }
    public sealed class BotPolicy
    {
        public BotDifficulty Difficulty { get; }
        public int PurchaseBudget { get; }
        public int MaxIntervals { get; }
        public BotPolicy(BotDifficulty difficulty)
        {
            if (!Enum.IsDefined(typeof(BotDifficulty), difficulty)) throw new ArgumentOutOfRangeException(nameof(difficulty));
            Difficulty = difficulty; PurchaseBudget = difficulty == BotDifficulty.Easy ? 1 : difficulty == BotDifficulty.Normal ? 2 : 3;
            MaxIntervals = difficulty == BotDifficulty.Hard ? 2 : 1;
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
                    .Count(day => !InformationWindowRules.KnownDays(c)[day].HasValue) })
                .Where(c => c.Unknown > 0 && InformationWindowRules.NextRandomDayCost(view.companies.First(v => v.companyId == c.Company)) <= view.coins).ToArray();
            if (candidates.Length == 0) return null;
            var choices = Policy.Difficulty == BotDifficulty.Hard ? candidates.Where(c => c.Unknown == candidates.Max(v => v.Unknown)).ToArray() : candidates;
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
                        candidates.Add(new Plan { Company = companyId, Score = (long)days[exit].Value - entryValues[entry].Value,
                            Intervals = new[] { new Interval { entryDay = entry, exitDay = exit } } });
            if (candidates.Count == 0) return new Plan { Company = companyId, Score = 0,
                Intervals = new[] { new Interval { entryDay = 0, exitDay = 5 } } };
            if (Policy.Difficulty == BotDifficulty.Easy)
            {
                var positive = candidates.Where(c => c.Score > 0).ToArray();
                return positive.Length > 0 ? positive[random.Next(positive.Length)] : candidates[random.Next(candidates.Count)];
            }
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
            if (Policy.Difficulty == BotDifficulty.Easy)
            {
                var positive = plans.Where(p => p.Score > 0).ToArray();
                var pool = positive.Length > 0 ? positive : plans;
                return pool[random.Next(pool.Length)].Company;
            }
            long best = plans.Max(p => p.Score);
            var ties = plans.Where(p => p.Score == best).ToArray();
            return ties[random.Next(ties.Length)].Company;
        }
        public Investment[] BuildOrder(PlayerView view, bool fallback = false)
        {
            var selected = view?.selectedCompanies;
            if (selected == null || selected.Length != 2 || selected[0] == selected[1]) return null;
            var plans = selected.Select(c => Evaluate(view, c)).ToArray();
            if (Policy.Difficulty == BotDifficulty.Hard && !fallback) plans = plans.OrderByDescending(p => p.Score).ToArray();
            int firstWeight = Policy.Difficulty == BotDifficulty.Hard && !fallback && plans[0].Score > plans[1].Score ? 7000 : 5000;
            return plans.Select((p, i) => new Investment { companyId = p.Company, weightBps = i == 0 ? firstWeight : 10000 - firstWeight,
                intervals = fallback ? new[] { new Interval { entryDay = 0, exitDay = 5 } } : p.Intervals.Select(t =>
                    new Interval { entryDay = t.entryDay, exitDay = t.exitDay }).ToArray() }).ToArray();
        }
    }
}
