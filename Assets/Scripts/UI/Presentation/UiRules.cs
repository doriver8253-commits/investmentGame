// 화면 표시·입력 사전검사에 쓰는 rules-v2 값(00_rules.json). 최종 판정은 서버(A/B/C).
// 공통 Shared에 같은 값이 생기면 그쪽을 쓰도록 바꾼다.
using System;

namespace BlackWhale.UI.Presentation
{
    public static class UiRules
    {
        public const int MinDay = 0;
        public const int MaxDay = 30;
        public const int BundleSizeDays = 5;
        public const int BundleCount = 6;
        public const int MinHoldingDays = 5;
        public const int MaxHoldingDays = 10;
        public const int StockMinHoldingDays = 7;
        public const int StockMaxHoldingDays = 30;
        public const int MaxIntervalsPerCompany = 2;
        public const int CompaniesPerOrder = 2;
        public const int TotalWeightBps = 10000;
        public static readonly int[] FirstCompanyWeightOptionsBps = { 5000, 6000, 7000 };

        /// <summary>표시 순서: 위 3개(AI, SPACE, FOOD), 아래 3개(BANK, BIO, AUTO). REQ-005.</summary>
        public static readonly string[] CompanyIds = { "AI", "SPACE", "FOOD", "BANK", "BIO", "AUTO" };

        /// <summary>기존 v2 규칙의 0일차 대비 변화율 상한(bps). 현재 차트의 표시 범위에는 사용하지 않는다.</summary>
        public static int AbsoluteChangeCapBps(string companyId)
        {
            switch (companyId)
            {
                case "AI": return 5000;
                case "SPACE": return 5000;
                case "FOOD": return 800;
                case "BANK": return 600;
                case "BIO": return 8000;
                case "AUTO": return 2000;
                default: return 10000;
            }
        }

        public static bool IsKnownCompany(string companyId) => Array.IndexOf(CompanyIds, companyId) >= 0;
    }
}
