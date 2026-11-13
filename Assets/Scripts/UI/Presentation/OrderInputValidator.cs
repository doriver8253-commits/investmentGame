// 주문창의 '사전 검사'. 잘못된 값을 보내기 전에 안내하려는 용도이며 최종 판정은 서버(B)가 한다.
// 서버 응답의 errorCode가 이 결과와 달라도 서버 값을 우선 표시한다.
// 규칙(rules-v2): 서로 다른 선택 기업 2개, 첫 기업 비중 5000/6000/7000 & 합 10000,
// 종가 진입 fixture는 기업당 1~2개·보유 5~10일을 유지한다.
// 일반 게임(시가 진입)은 기간/개수 제한 없이 1~30일 당일 거래도 가능하며 날짜/중복을 검사한다.
using System.Collections.Generic;
using System.Linq;
using BlackWhale.Shared;

namespace BlackWhale.UI.Presentation
{
    public static class OrderInputValidator
    {
        /// <returns>문제 없으면 빈 문자열, 있으면 공통 오류 코드.</returns>
        public static string Validate(IList<Investment> investments, IEnumerable<string> selectedCompanies, bool entryAtOpen = false)
        {
            var selected = selectedCompanies?.ToList() ?? new List<string>();

            if (investments == null || investments.Count != UiRules.CompaniesPerOrder
                || investments.Any(i => i == null))
                return ErrorCodes.CompanyNotSelected;

            var first = investments[0];
            var second = investments[1];
            if (!UiRules.FirstCompanyWeightOptionsBps.Contains(first.weightBps)
                || first.weightBps + second.weightBps != UiRules.TotalWeightBps)
                return ErrorCodes.InvalidRatio;

            if (first.companyId == second.companyId) return ErrorCodes.CompanyNotSelected;

            foreach (var inv in investments)
            {
                if (!selected.Contains(inv.companyId)) return ErrorCodes.CompanyNotSelected;

                var error = ValidateIntervals(inv.intervals, entryAtOpen);
                if (error.Length > 0) return error;
            }

            return "";
        }

        public static string ValidateIntervals(IList<Interval> intervals, bool entryAtOpen = false)
        {
            if (intervals == null || intervals.Count == 0) return ErrorCodes.MissingInterval;
            if (!entryAtOpen && intervals.Count > UiRules.MaxIntervalsPerCompany) return ErrorCodes.TooManyIntervals;

            foreach (var iv in intervals)
            {
                var error = ValidateInterval(iv, entryAtOpen);
                if (error.Length > 0) return error;
            }

            var sorted = intervals.OrderBy(i => i.entryDay).ToList();
            for (int k = 1; k < sorted.Count; k++)
            {
                // 앞 청산일 == 다음 진입일은 허용
                if (sorted[k].entryDay < sorted[k - 1].exitDay || (entryAtOpen && sorted[k].entryDay == sorted[k - 1].exitDay)) return ErrorCodes.OverlappingIntervals;
            }

            return "";
        }

        public static string ValidateInterval(Interval iv, bool entryAtOpen = false)
        {
            if (iv == null) return ErrorCodes.MissingInterval;
            if (iv.entryDay < UiRules.MinDay || iv.exitDay > UiRules.MaxDay || iv.exitDay < iv.entryDay
                || (iv.exitDay == iv.entryDay && (!entryAtOpen || iv.entryDay == 0)))
                return ErrorCodes.InvalidDay;

            int holding = iv.exitDay - iv.entryDay;
            int minimumHoldingDays = entryAtOpen ? UiRules.StockMinHoldingDays : UiRules.MinHoldingDays;
            int maximumHoldingDays = entryAtOpen ? UiRules.StockMaxHoldingDays : UiRules.MaxHoldingDays;
            if (holding < minimumHoldingDays || holding > maximumHoldingDays)
                return ErrorCodes.InvalidHoldingDays;

            return "";
        }

        /// <summary>진입일을 고른 뒤 최소 7일 뒤부터 가능한 청산일을 만든다.</summary>
        public static IEnumerable<int> AllowedExitDays(int entryDay)
        {
            for (int d = entryDay + UiRules.MinHoldingDays; d <= entryDay + UiRules.MaxHoldingDays; d++)
                if (d <= UiRules.MaxDay) yield return d;
        }
    }
}
