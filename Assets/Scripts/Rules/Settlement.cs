using System;
using System.Collections.Generic;
using System.Linq;
using BlackWhale.Shared;

namespace BlackWhale.Rules
{
    // Server-only per-player result (B_settlement_expected.json shape). Not a client message.
    public sealed class CompanyProfit
    {
        public string companyId;
        public long allocatedAssets, profit;
        public int returnBps;
    }

    public sealed class PlayerSettlement
    {
        public string playerId;
        public long startingAssets, profit, endingAssets;
        public int turnReturnBps;
        public List<CompanyProfit> companyProfits = new List<CompanyProfit>();
    }

    // Settlement formula from 00_공통_맥락.md. C# long division truncates toward zero (-1.5 -> -1), as required.
    public static class Settlement
    {
        public static void Allocate(long investable, int firstWeightBps, out long first, out long second)
        {
            first = investable * firstWeightBps / InvestmentRules.FullWeightBps;
            second = investable - first;
        }

        public static int CompanyReturnBps(MarketBook market, Investment investment, InvestmentRules rules)
        {
            int sum = 0;
            foreach (Interval i in investment.intervals)
                sum += market.ChangeBps(investment.companyId, i.exitDay) - market.ChangeBps(investment.companyId, i.entryDay);
            return Math.Max(sum, rules.minimumCompanyReturnBps);
        }

        public static long Profit(long allocated, int returnBps) { return allocated * returnBps / InvestmentRules.FullWeightBps; }

        public static int ReturnBps(long baseAssets, long current)
        { return baseAssets == 0 ? 0 : (int)((current - baseAssets) * InvestmentRules.FullWeightBps / baseAssets); }

        public static PlayerSettlement SettlePlayer(string playerId, long startingAssets, long turnCosts,
            Investment[] investments, MarketBook market, InvestmentRules rules)
        {
            long investable = startingAssets - turnCosts;
            var result = new PlayerSettlement { playerId = playerId, startingAssets = startingAssets };
            long total = 0;
            if (investments != null && investments.Length == 2)
            {
                long first, second;
                Allocate(investable, investments[0].weightBps, out first, out second);
                long[] allocated = { first, second };
                for (int k = 0; k < 2; k++)
                {
                    int rate = CompanyReturnBps(market, investments[k], rules);
                    long p = Profit(allocated[k], rate);
                    total += p;
                    result.companyProfits.Add(new CompanyProfit { companyId = investments[k].companyId,
                        allocatedAssets = allocated[k], returnBps = rate, profit = p });
                }
            }
            result.endingAssets = Math.Max(rules.minimumAssets, investable + total);
            result.profit = result.endingAssets - startingAssets;
            result.turnReturnBps = ReturnBps(startingAssets, result.endingAssets);
            return result;
        }

        // Competition ranking: 10000,10000,9000,8000 -> 1,1,3,4. Display order ties by playerId.
        public static List<KeyValuePair<string, int>> Rank(IDictionary<string, long> assets)
        {
            return assets.OrderByDescending(a => a.Value).ThenBy(a => a.Key, StringComparer.Ordinal)
                .Select(a => new KeyValuePair<string, int>(a.Key, 1 + assets.Count(o => o.Value > a.Value))).ToList();
        }
    }
}
