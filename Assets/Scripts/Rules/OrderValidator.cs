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
                Interval[] periods = inv.intervals ?? new Interval[0];
                if (periods.Length > rules.maxIntervalsPerCompany) return ErrorCodes.TooManyIntervals;
                if (periods.Length == 0) return ErrorCodes.MissingInterval;
                foreach (Interval p in periods)
                {
                    if (p == null || !(rules.minEntryDay <= p.entryDay && p.entryDay < p.exitDay && p.exitDay <= rules.maxExitDay))
                        return ErrorCodes.InvalidDay;
                    int holding = p.exitDay - p.entryDay;
                    if (holding < rules.minHoldingDays || holding > rules.maxHoldingDays) return ErrorCodes.InvalidHoldingDays;
                }
                Interval[] sorted = periods.OrderBy(p => p.entryDay).ToArray();
                for (int k = 1; k < sorted.Length; k++)
                    if (sorted[k - 1].exitDay > sorted[k].entryDay) return ErrorCodes.OverlappingIntervals; // touching is allowed
            }
            return "";
        }
    }
}
