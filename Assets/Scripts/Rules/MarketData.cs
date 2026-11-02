using System;
using System.Collections.Generic;

namespace BlackWhale.Rules
{
    // Server-only market source. Same shape as B_market_server.json. Never sent to clients before reveal.
    [Serializable]
    public sealed class MarketData
    {
        public int turn;
        public CompanyMarketData[] companies;
    }

    [Serializable]
    public sealed class CompanyMarketData
    {
        public string companyId;
        public int[] dailyChangesBps;      // day 0..30, change vs day 0 in bps, [0] == 0
        public int[] publicBundleIndices;  // two free bundles (0..5)
    }

    // Validated, read-only view over one turn's market.
    public sealed class MarketBook
    {
        private readonly Dictionary<string, CompanyMarketData> companies = new Dictionary<string, CompanyMarketData>();
        private readonly InvestmentRules rules;
        public int Turn { get; private set; }

        public MarketBook(MarketData data, InvestmentRules rules)
        {
            this.rules = rules;
            if (data == null || data.companies == null) throw new ArgumentException("Market data is empty.");
            Turn = data.turn;
            foreach (CompanyMarketData c in data.companies)
            {
                if (Array.IndexOf(InvestmentRules.CompanyIds, c.companyId) < 0 || companies.ContainsKey(c.companyId))
                    throw new ArgumentException("Unknown or duplicate company: " + c.companyId);
                if (c.dailyChangesBps == null || c.dailyChangesBps.Length != rules.daysPerTurn + 1 || c.dailyChangesBps[0] != 0)
                    throw new ArgumentException(c.companyId + ": need " + (rules.daysPerTurn + 1) + " daily values with day 0 = 0.");
                if (c.publicBundleIndices == null || c.publicBundleIndices.Length != InvestmentRules.PublicBundlesPerCompany
                    || c.publicBundleIndices[0] == c.publicBundleIndices[1]
                    || Array.Exists(c.publicBundleIndices, b => b < 0 || b >= InvestmentRules.BundlesPerCompany))
                    throw new ArgumentException(c.companyId + ": need two distinct free bundles in 0..5.");
                companies.Add(c.companyId, c);
            }
            if (companies.Count != InvestmentRules.CompanyIds.Length) throw new ArgumentException("Exactly 6 companies required.");
        }

        public bool HasCompany(string companyId) { return companyId != null && companies.ContainsKey(companyId); }

        public int ChangeBps(string companyId, int day) { return companies[companyId].dailyChangesBps[day]; }

        public int[] Bundle(string companyId, int bundleIndex, out int startDay)
        {
            startDay = 1 + bundleIndex * rules.bundleSizeDays;
            var values = new int[rules.bundleSizeDays];
            Array.Copy(companies[companyId].dailyChangesBps, startDay, values, 0, values.Length);
            return values;
        }

        public bool IsPublic(string companyId, int bundleIndex)
        { return Array.IndexOf(companies[companyId].publicBundleIndices, bundleIndex) >= 0; }

        public int[] AllDays(string companyId) { return (int[])companies[companyId].dailyChangesBps.Clone(); }
    }
}
