using System;
using System.Linq;

namespace BlackWhale.Shared
{
    public static class InformationWindowRules
    {
        public const int Length = 5, MaxStartDay = 26;
        // 한 턴에 같은 기업을 깊게 팔수록 비싸진다: 1~3번째 1코인, 4~6번째 2코인, 7번째부터 3코인.
        // 여러 기업을 넓게 볼지, 한 기업 패턴을 깊게 확인할지 고르게 만든다.
        public const int CheapDays = 3, MiddleDays = 6;
        public static int RandomDayCost(int alreadyPurchased) => alreadyPurchased < CheapDays ? 1 : alreadyPurchased < MiddleDays ? 2 : 3;
        public static int RandomDaysCost(int alreadyPurchased, int count)
            => Enumerable.Range(Math.Max(0, alreadyPurchased), Math.Max(0, count)).Sum(RandomDayCost);
        /// <summary>이번 턴 이 기업에서 내가 코인으로 산 날짜 수(공용·증강 정보 제외).</summary>
        public static int PurchasedDays(VisibleCompany company)
            => (company?.visibleWindows ?? Array.Empty<VisibleInformationWindow>())
                .Where(w => w?.source == "PURCHASED").Sum(w => w.dailyChangesBps?.Length ?? 0);
        public static int NextRandomDayCost(VisibleCompany company) => RandomDayCost(PurchasedDays(company));
        /// <summary>정보 탐색 중 전체 플레이어가 이 기업에 산 날짜 수(익명 합계). 없으면 0.</summary>
        public static int Interest(PublicState state, string company)
            => (state?.informationInterest ?? Array.Empty<CompanyInterest>()).FirstOrDefault(i => i?.companyId == company)?.purchasedDays ?? 0;
        public static int[] PublicDays(PublicInformationChoice choice) => choice.days ?? Enumerable.Range(choice.startDay, Length).ToArray();
        public static string PublicChoiceLabel(PublicInformationChoice choice)
        {
            var days = PublicDays(choice);
            bool continuous = days.Length > 0 && days.Last() - days[0] + 1 == days.Length;
            return choice.playerId + " " + choice.companyId + " " + (continuous && days.Length > 1
                ? days[0] + "~" + days.Last() : string.Join(",", days)) + "일" + (choice.automatic ? "(자동)" : "");
        }
        public static bool CanRevealPublic(PublicState state, string company)
        {
            var options = state.lobbySettings ?? new LobbySettings();
            if (options.publicRevealDays == 0) return false;
            var used = (state.publicInformation ?? Array.Empty<PublicInformationChoice>()).Where(c => c.companyId == company)
                .SelectMany(PublicDays).ToArray();
            if (options.scatteredPublicDays) return 30 - used.Distinct().Count() >= options.publicRevealDays;
            return Enumerable.Range(1, 31 - options.publicRevealDays).Any(start =>
                !Enumerable.Range(start, options.publicRevealDays).Any(d => used.Contains(d)));
        }
        public static int?[] KnownDays(VisibleCompany company)
        {
            var values = new int?[31]; values[0] = 0;
            foreach (var b in company?.visibleBundles ?? Array.Empty<VisibleBundle>())
                if (b?.dailyChangesBps != null) Copy(values, b.startDay, b.dailyChangesBps);
            foreach (var w in company?.visibleWindows ?? Array.Empty<VisibleInformationWindow>())
                if (w?.dailyChangesBps != null) Copy(values, w.startDay, w.dailyChangesBps);
            return values;
        }
        private static void Copy(int?[] values, int start, int[] source)
        { for (int i = 0; i < source.Length; i++) if (start + i >= 1 && start + i <= 30) values[start + i] = source[i]; }
        public static int NewDays(VisibleCompany company, int start, int end = -1)
        { var known = KnownDays(company); if (end == -1) end = start + 4; return start < 1 || end > 30 || start > end ? 0 : Enumerable.Range(start, end - start + 1).Count(d => !known[d].HasValue); }
        public static int?[] KnownOpens(VisibleCompany company)
        {
            var closes = KnownDays(company); var opens = new int?[31]; opens[0] = 0;
            for (int d = 1; d <= 30; d++) if (closes[d].HasValue) opens[d] = closes[d - 1];
            foreach (var w in company?.visibleWindows ?? Array.Empty<VisibleInformationWindow>())
                if (w?.dailyOpenBps != null)
                    for (int i = 0; i < w.dailyOpenBps.Length; i++)
                        if (w.startDay + i >= 1 && w.startDay + i <= 30) opens[w.startDay + i] = w.dailyOpenBps[i];
            return opens;
        }
        public static bool OverlapsPublic(PublicState state, string company, int start)
            => (state?.publicInformation ?? Array.Empty<PublicInformationChoice>()).Any(c =>
                c.companyId == company && PublicDays(c).Any(d => d >= start && d < start + Length));
    }
}
