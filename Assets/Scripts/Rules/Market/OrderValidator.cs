using System;
using System.Collections.Generic;
using System.Linq;
using BlackWhale.Shared;

namespace BlackWhale.Rules
{
    // Pure order check. Order of checks and codes match validate_fixtures.py (order_error).
    public static class OrderValidator
    {
        public static string Validate(Investment[] investments, IList<string> selectedCompanies, InvestmentRules rules)
        {
            if (investments == null || investments.Length != 2 || investments.Any(i => i == null)
                || investments[0].companyId == investments[1].companyId)
                return ErrorCodes.DuplicateCompany;

            if (!new HashSet<string>(investments.Select(i => i.companyId)).SetEquals(selectedCompanies ?? new string[0]))
                return ErrorCodes.CompanyNotSelected;

            int first = investments[0].weightBps, second = investments[1].weightBps;
            if (Array.IndexOf(rules.firstCompanyWeightOptionsBps, first) < 0 || first + second != InvestmentRules.FullWeightBps)
                return ErrorCodes.InvalidRatio;

            foreach (Investment inv in investments)
            {
                string error = ValidateIntervals(inv.intervals, rules);
                if (error.Length > 0) return error;
            }
            return "";
        }

        public static string ValidateIntervals(Interval[] intervals, InvestmentRules rules)
        {
            Interval[] periods = intervals ?? new Interval[0];
            if (rules.maxIntervalsPerCompany > 0 && periods.Length > rules.maxIntervalsPerCompany) return ErrorCodes.TooManyIntervals;
            if (periods.Length == 0) return ErrorCodes.MissingInterval;
            foreach (Interval p in periods)
            {
                if (p == null || !(rules.minEntryDay <= p.entryDay &&
                    (p.entryDay < p.exitDay || (rules.entryAtOpen && rules.minHoldingDays == 1 && p.entryDay > 0 && p.entryDay == p.exitDay)) && p.exitDay <= rules.maxExitDay))
                    return ErrorCodes.InvalidDay;
                // 주식 모드의 보유 기간은 달력 날짜의 차이로 계산한다.
                // 따라서 1일 진입·7일 청산은 6일로 취급되어 실패하고,
                // 1일 진입·8일 청산부터 최소 7일 조건을 만족한다.
                int holding = rules.entryAtOpen && rules.minHoldingDays == 1 && p.entryDay > 0
                    ? p.exitDay - p.entryDay + 1
                    : p.exitDay - p.entryDay;
                if (holding < rules.minHoldingDays || holding > rules.maxHoldingDays) return ErrorCodes.InvalidHoldingDays;
            }
            Interval[] sorted = periods.OrderBy(p => p.entryDay).ToArray();
            for (int k = 1; k < sorted.Length; k++)
                if (sorted[k - 1].exitDay > sorted[k].entryDay || (rules.entryAtOpen && sorted[k - 1].exitDay == sorted[k].entryDay))
                    return ErrorCodes.OverlappingIntervals;
            return "";
        }
    }
}
