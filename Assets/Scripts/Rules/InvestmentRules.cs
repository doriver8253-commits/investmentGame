using System;

namespace BlackWhale.Rules
{
    // rules-v2 (00_rules.json) values used by B. Field names match 00_rules.json.
    [Serializable]
    public sealed class InvestmentRules
    {
        public int daysPerTurn = 30;
        public int bundleSizeDays = 5;
        public long initialAssets = 10000;
        public int maxPlayersPerCompany = 2;
        public int[] firstCompanyWeightOptionsBps = { 5000, 6000, 7000 };
        public int maxIntervalsPerCompany = 2;
        public int minHoldingDays = 5;
        public int maxHoldingDays = 10;
        public int minEntryDay = 0;
        public int maxExitDay = 30;
        public int minimumCompanyReturnBps = -10000;
        public long minimumAssets = 0;
        public long comebackGrant = 1000;

        public const int FullWeightBps = 10000;
        public const int BundlesPerCompany = 6;
        public const int PublicBundlesPerCompany = 2;
        public static readonly string[] CompanyIds = { "AI", "SPACE", "FOOD", "BANK", "BIO", "AUTO" };
    }
}
