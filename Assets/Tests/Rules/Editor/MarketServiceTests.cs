using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using BlackWhale.Shared;

namespace BlackWhale.Rules.Tests
{
    [Serializable] public sealed class FixtureOrder { public string playerId; public Investment[] investments; }
    [Serializable] public sealed class FixtureOrders { public FixtureOrder[] orders; }

    /// <summary>
    /// B rule tests against the team fixtures in "개발 정리/임시 데이터 및 검증용".
    /// Run: Window > General > Test Runner > EditMode > Run All.
    /// </summary>
    public sealed class MarketServiceTests
    {
        private static readonly string[] Players = { "P1", "P2", "P3", "P4" };
        private static readonly string[] Picks = { "AI", "SPACE", "BIO", "AI", "BIO", "AUTO", "BANK", "FOOD" }; // A_selection_replay order
        private static readonly string[] PickOrder = { "P1", "P2", "P3", "P4", "P4", "P3", "P2", "P1" };

        private static string Fixture(string file)
        {
            string root = Path.GetDirectoryName(Application.dataPath);
            return File.ReadAllText(Path.Combine(root, "개발 정리", "임시 데이터 및 검증용", file));
        }

        private static MarketService NewService()
        {
            var rules = JsonUtility.FromJson<InvestmentRules>(Fixture("00_rules.json"));
            var market = JsonUtility.FromJson<MarketData>(Fixture("B_market_server.json"));
            var service = new MarketService(market, rules);
            service.BeginTurn(Ctx(Protocol.Info), Players);
            return service;
        }

        private static RequestContext Ctx(string phase, string player = "", int turn = 1)
        { return new RequestContext("TEST", turn, phase, player, "req"); }

        private static Investment Inv(string id, int w, params int[] days)
        {
            var intervals = new Interval[days.Length / 2];
            for (int i = 0; i < intervals.Length; i++) intervals[i] = new Interval { entryDay = days[2 * i], exitDay = days[2 * i + 1] };
            return new Investment { companyId = id, weightBps = w, intervals = intervals };
        }

        private static MarketService WithP1Picked(params string[] companies)
        {
            MarketService s = NewService();
            foreach (string c in companies) Assert.IsTrue(s.ValidateAndSelectCompany(Ctx(Protocol.Selection, "P1"), c).Success);
            return s;
        }

        // ---------- market ----------

        [Test]
        public void MarketShapeAndBundles()
        {
            MarketService s = NewService();
            Assert.AreEqual(6, s.GetCompanyIds().Length);
            ServerInformationBundle b = s.GetBundle(1, "AI", 1);
            Assert.AreEqual(6, b.StartDay);
            CollectionAssert.AreEqual(new[] { -1500, -2000, -2500, -3000, -4000 }, b.DailyChangesBps);
            foreach (string id in s.GetCompanyIds())
                Assert.AreEqual(2, Enumerable.Range(0, 6).Count(i => s.GetBundle(1, id, i).IsPublic));
        }

        [Test]
        public void MarketHiddenUntilLocked()
        {
            Assert.Throws<InvalidOperationException>(() => NewService().GetRevealedMarket(Ctx(Protocol.Investment)));
        }

        // ---------- selection ----------

        [Test]
        public void PickSuccessFullAndDuplicate()
        {
            MarketService s = NewService();
            Assert.IsTrue(s.ValidateAndSelectCompany(Ctx(Protocol.Selection, "P1"), "AI").Success);
            Assert.AreEqual(ErrorCodes.DuplicateCompany, s.ValidateAndSelectCompany(Ctx(Protocol.Selection, "P1"), "AI").ErrorCode);
            Assert.IsTrue(s.ValidateAndSelectCompany(Ctx(Protocol.Selection, "P2"), "AI").Success);
            Assert.AreEqual(ErrorCodes.CompanyFull, s.ValidateAndSelectCompany(Ctx(Protocol.Selection, "P3"), "AI").ErrorCode);
            Assert.AreEqual(0, s.GetCompanySeats().First(x => x.companyId == "AI").remainingSeats);
            CollectionAssert.IsEmpty(s.GetSelectedCompanies("P3")); // rejected call changed nothing
        }

        [Test]
        public void PickRejectsWrongPhaseStaleTurnAndUnknownCompany()
        {
            MarketService s = NewService();
            Assert.AreEqual(ErrorCodes.WrongPhase, s.ValidateAndSelectCompany(Ctx(Protocol.Info, "P1"), "AI").ErrorCode);
            Assert.AreEqual(ErrorCodes.StaleTurn, s.ValidateAndSelectCompany(Ctx(Protocol.Selection, "P1", 2), "AI").ErrorCode);
            Assert.AreEqual(ErrorCodes.InvalidCompany, s.ValidateAndSelectCompany(Ctx(Protocol.Selection, "P1"), "XYZ").ErrorCode);
        }

        // ---------- orders (validation_cases.json SUBMIT_INVESTMENT) ----------

        [TestCase(10, 15, 6000, "FOOD", "")]
        [TestCase(10, 13, 6000, "FOOD", "INVALID_HOLDING_DAYS")]
        [TestCase(10, 15, 8000, "FOOD", "INVALID_RATIO")]
        [TestCase(25, 31, 6000, "FOOD", "INVALID_DAY")]
        [TestCase(10, 15, 6000, "BANK", "COMPANY_NOT_SELECTED")]
        [TestCase(3, 8, 6000, "FOOD", "")]
        [TestCase(3, 13, 6000, "FOOD", "")]
        [TestCase(3, 7, 6000, "FOOD", "INVALID_HOLDING_DAYS")]
        [TestCase(3, 14, 6000, "FOOD", "INVALID_HOLDING_DAYS")]
        [TestCase(25, 30, 6000, "FOOD", "")]
        [TestCase(26, 31, 6000, "FOOD", "INVALID_DAY")]
        [TestCase(0, 5, 6000, "FOOD", "")]
        [TestCase(8, 3, 6000, "FOOD", "INVALID_DAY")]
        public void OrderSingleInterval(int entry, int exit, int weight, string second, string expected)
        {
            MarketService s = WithP1Picked("AI", "FOOD");
            RuleResult r = s.SubmitInvestment(Ctx(Protocol.Investment, "P1"),
                new[] { Inv("AI", weight, entry, exit), Inv(second, 10000 - weight, 5, 10) });
            Assert.AreEqual(expected, r.ErrorCode);
            Assert.AreEqual(expected == "", s.IsOrderLocked("P1")); // failure leaves no order
        }

        [Test]
        public void OrderMultiIntervalRules()
        {
            Investment food = Inv("FOOD", 4000, 5, 10);
            Assert.AreEqual(ErrorCodes.TooManyIntervals, WithP1Picked("AI", "FOOD").SubmitInvestment(Ctx(Protocol.Investment, "P1"),
                new[] { Inv("AI", 6000, 0, 5, 5, 10, 10, 15), food }).ErrorCode);
            Assert.AreEqual(ErrorCodes.OverlappingIntervals, WithP1Picked("AI", "FOOD").SubmitInvestment(Ctx(Protocol.Investment, "P1"),
                new[] { Inv("AI", 6000, 5, 15, 10, 20), food }).ErrorCode);
            Assert.AreEqual(ErrorCodes.MissingInterval, WithP1Picked("AI", "FOOD").SubmitInvestment(Ctx(Protocol.Investment, "P1"),
                new[] { Inv("AI", 6000), food }).ErrorCode);
            Assert.IsTrue(WithP1Picked("AI", "FOOD").SubmitInvestment(Ctx(Protocol.Investment, "P1"),
                new[] { Inv("AI", 6000, 3, 8, 8, 13), food }).Success); // touching intervals allowed
        }

        [Test]
        public void SecondSubmitIsOrderLocked()
        {
            MarketService s = WithP1Picked("AI", "FOOD");
            Investment[] order = { Inv("AI", 6000, 10, 15), Inv("FOOD", 4000, 5, 10) };
            RuleResult first = s.SubmitInvestment(Ctx(Protocol.Investment, "P1"), order);
            Assert.IsTrue(first.Success && first.Payload.locked);
            Assert.AreEqual(ErrorCodes.OrderLocked, s.SubmitInvestment(Ctx(Protocol.Investment, "P1"), order).ErrorCode);
        }

        // ---------- full fixture turn ----------

        private static MarketService PlayFixtureTurn()
        {
            MarketService s = NewService();
            for (int i = 0; i < 8; i++)
                Assert.IsTrue(s.ValidateAndSelectCompany(Ctx(Protocol.Selection, PickOrder[i]), Picks[i]).Success, "pick " + i);
            foreach (FixtureOrder o in JsonUtility.FromJson<FixtureOrders>(Fixture("B_investment_orders.json")).orders)
                Assert.IsTrue(s.SubmitInvestment(Ctx(Protocol.Investment, o.playerId), o.investments).Success, o.playerId);
            s.LockAllOrders(Ctx(Protocol.Investment));
            return s;
        }

        [Test]
        public void SettlementMatchesExpectedScoreboard()
        {
            MarketService s = PlayFixtureTurn();
            Scoreboard actual = s.SettleTurn(Ctx(Protocol.Reveal));
            Scoreboard expected = JsonUtility.FromJson<Scoreboard>(Fixture("D_scoreboard_expected.json"));
            Assert.AreEqual(expected.rows.Length, actual.rows.Length);
            for (int i = 0; i < expected.rows.Length; i++)
            {
                ScoreRow e = expected.rows[i], a = actual.rows[i];
                Assert.AreEqual(e.playerId, a.playerId);
                Assert.AreEqual(e.rank, a.rank);
                Assert.AreEqual(e.totalAssets, a.totalAssets);
                Assert.AreEqual(e.turnReturnBps, a.turnReturnBps);
                Assert.AreEqual(e.cumulativeProfit, a.cumulativeProfit);
                Assert.AreEqual(e.cumulativeReturnBps, a.cumulativeReturnBps);
                CollectionAssert.AreEqual(e.returnHistory.Select(p => p.turn + ":" + p.returnBps).ToArray(),
                    a.returnHistory.Select(p => p.turn + ":" + p.returnBps).ToArray());
            }
            PlayerSettlement p1 = s.GetSettlementDetail(1).First(p => p.playerId == "P1");
            Assert.AreEqual(4200, p1.companyProfits[0].profit); // AI 6,000 x +70%
            Assert.AreEqual(80, p1.companyProfits[1].profit);   // FOOD 4,000 x +2%
        }

        [Test]
        public void SettleTwiceAppliesOnce()
        {
            MarketService s = PlayFixtureTurn();
            Scoreboard first = s.SettleTurn(Ctx(Protocol.Reveal));
            Assert.AreSame(first, s.SettleTurn(Ctx(Protocol.Reveal)));
            Assert.AreEqual(14280, s.GetAssets("P1"));
        }

        [Test]
        public void LockRequiresAllOrders()
        {
            Assert.Throws<InvalidOperationException>(() => NewService().LockAllOrders(Ctx(Protocol.Investment)));
        }

        // ---------- integrationChecks ----------

        [Test] public void RoundingTowardZero() { Assert.AreEqual(-1, Settlement.Profit(101, -150)); }

        [Test]
        public void RemainderGoesToSecondCompany()
        {
            long first, second;
            Settlement.Allocate(10001, 6000, out first, out second);
            Assert.AreEqual(6000, first); Assert.AreEqual(4001, second);
        }

        [Test]
        public void CumulativeReturnIsNotSum()
        {
            Assert.AreEqual(1000, Settlement.ReturnBps(11000, 12100));
            Assert.AreEqual(2100, Settlement.ReturnBps(10000, 12100));
        }

        [Test]
        public void TiedRanks()
        {
            var ranks = Settlement.Rank(new System.Collections.Generic.Dictionary<string, long>
                { { "P1", 10000 }, { "P2", 10000 }, { "P3", 9000 }, { "P4", 8000 } });
            CollectionAssert.AreEqual(new[] { 1, 1, 3, 4 }, ranks.Select(r => r.Value).ToArray());
        }
    }
}
