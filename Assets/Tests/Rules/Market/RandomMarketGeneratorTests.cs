using System;
using System.Linq;
using NUnit.Framework;
using BlackWhale.Shared;

namespace BlackWhale.Rules.Tests
{
    /// <summary>NEXT-B02: per-turn random market (02_결정_사항 §10). Pass criteria N01~N03 (B part).</summary>
    public sealed class RandomMarketGeneratorTests
    {
        private static readonly string[] Players = { "P1", "P2", "P3", "P4" };

        private static RequestContext Ctx(string phase, string player = "", int turn = 1)
        { return new RequestContext("TEST", turn, phase, player, "req"); }

        private static int[] Days(MarketData m, string id) { return m.companies.Single(c => c.companyId == id).dailyChangesBps; }

        [Test]
        public void ShapeMatchesMarketBook()
        {
            for (int turn = 1; turn <= 10; turn++)
            {
                MarketData m = RandomMarketGenerator.Generate(12345, turn);
                Assert.AreEqual(turn, m.turn);
                CollectionAssert.AreEquivalent(InvestmentRules.CompanyIds, m.companies.Select(c => c.companyId).ToArray());
                foreach (CompanyMarketData c in m.companies)
                {
                    Assert.AreEqual(31, c.dailyChangesBps.Length);
                    Assert.AreEqual(0, c.dailyChangesBps[0]);
                    Assert.AreEqual(2, c.publicBundleIndices.Length);
                    Assert.AreNotEqual(c.publicBundleIndices[0], c.publicBundleIndices[1]);
                    Assert.IsTrue(c.publicBundleIndices.All(b => b >= 0 && b < 6));
                }
                Assert.DoesNotThrow(() => new MarketBook(m, new InvestmentRules()));
            }
        }

        [Test]
        public void SameSeedSameMarketOtherSeedOtherMarket()
        {
            for (int turn = 1; turn <= 10; turn++)
                foreach (string id in InvestmentRules.CompanyIds)
                    CollectionAssert.AreEqual(Days(RandomMarketGenerator.Generate(7, turn), id), Days(RandomMarketGenerator.Generate(7, turn), id));
            Assert.IsFalse(Days(RandomMarketGenerator.Generate(7, 1), "BIO").SequenceEqual(Days(RandomMarketGenerator.Generate(8, 1), "BIO")));
            Assert.IsFalse(Days(RandomMarketGenerator.Generate(7, 1), "BIO").SequenceEqual(Days(RandomMarketGenerator.Generate(7, 2), "BIO")));
        }

        [Test]
        public void KnownValuesForSeed42AreStable()
        {
            // Locks the algorithm: Unity/Mono and .NET must give these exact values. Changing the
            // generator on purpose means updating this test and telling the team (02 rules review).
            MarketData m = RandomMarketGenerator.Generate(42, 1);
            CollectionAssert.AreEqual(Expected42Bank, Days(m, "BANK").Take(6).ToArray());
            CollectionAssert.AreEqual(Expected42Bio, Days(m, "BIO").Take(6).ToArray());
            CollectionAssert.AreEqual(new[] { 2, 3 }, m.companies.Single(c => c.companyId == "BANK").publicBundleIndices);
        }

        private static readonly int[] Expected42Bank = { 0, 62, 46, 115, 38, -32 };
        private static readonly int[] Expected42Bio = { 0, -976, 515, 186, -412, -1662 };

        [TestCase("BANK", 1, 80)] [TestCase("FOOD", 1, 120)] [TestCase("AUTO", 1, 350)]
        [TestCase("AI", 1, 800)] [TestCase("SPACE", 1, 800)] [TestCase("BIO", 1, 1500)]
        [TestCase("BANK", 10, 116)] [TestCase("FOOD", 10, 174)] [TestCase("AUTO", 10, 508)] // 507.5 -> 508
        [TestCase("AI", 10, 1160)] [TestCase("BIO", 10, 2175)]
        [TestCase("AUTO", 2, 368)]   // 367.5 -> 368 (.5 rounds up)
        [TestCase("BANK", 2, 84)] [TestCase("FOOD", 4, 138)]
        public void DailyLimitGrowsFivePercentPerTurn(string id, int turn, int expected)
        {
            Assert.AreEqual(expected, RandomMarketGenerator.DailyLimitBps(id, turn));
        }

        [Test]
        public void DailyStepAndAbsoluteCapRespectedOverManySeeds()
        {
            bool sawCap = false;
            for (int seed = 0; seed < 300; seed++)
                for (int turn = 1; turn <= 10; turn += 3)
                {
                    MarketData m = RandomMarketGenerator.Generate(seed, turn);
                    int[] swings = RandomMarketGenerator.SwingBundles(seed, turn);
                    for (int i = 0; i < m.companies.Length; i++)
                    {
                        CompanyMarketData c = m.companies[i];
                        int limit = RandomMarketGenerator.DailyLimitBps(c.companyId, turn);
                        int cap = RandomMarketGenerator.AbsoluteCapBps[c.companyId];
                        for (int d = 1; d <= 30; d++)
                        {
                            // Days inside the hidden swing bundle may jump more than the daily limit.
                            bool inSwing = swings[i] >= 0 && d > swings[i] * 5 && d <= swings[i] * 5 + 5;
                            if (!inSwing)
                                Assert.LessOrEqual(Math.Abs(c.dailyChangesBps[d] - c.dailyChangesBps[d - 1]), limit, c.companyId + " day " + d);
                            Assert.LessOrEqual(Math.Abs(c.dailyChangesBps[d]), cap, c.companyId + " day " + d);
                            if (Math.Abs(c.dailyChangesBps[d]) == cap) sawCap = true;
                        }
                    }
                }
            Assert.IsTrue(sawCap, "clamp should be reached sometimes (BANK/FOOD over 30 days)");
        }

        [Test]
        public void HiddenSwingOnlyInNonPublicBundlesAboutHalfTheTime()
        {
            int total = 0, withSwing = 0, big = 0;
            var used = new System.Collections.Generic.HashSet<int>();
            for (int seed = 0; seed < 300; seed++)
                for (int turn = 1; turn <= 10; turn++)
                {
                    MarketData m = RandomMarketGenerator.Generate(seed, turn);
                    int[] swings = RandomMarketGenerator.SwingBundles(seed, turn);
                    Assert.AreEqual(m.companies.Length, swings.Length);
                    for (int i = 0; i < swings.Length; i++)
                    {
                        total++;
                        if (swings[i] < 0) continue;
                        withSwing++; used.Add(swings[i]);
                        CollectionAssert.DoesNotContain(m.companies[i].publicBundleIndices, swings[i], "swing must stay hidden");
                        int limit = RandomMarketGenerator.DailyLimitBps(m.companies[i].companyId, turn);
                        int[] v = m.companies[i].dailyChangesBps;
                        bool jump = false;
                        for (int d = swings[i] * 5 + 1; d <= swings[i] * 5 + 5; d++)
                            if (Math.Abs(v[d] - v[d - 1]) > limit) jump = true;
                        if (jump) big++;
                    }
                }
            double rate = (double)withSwing / total;
            Assert.That(rate, Is.InRange(0.45, 0.55), "about SwingChancePercent of companies per turn");
            Assert.AreEqual(6, used.Count, "any bundle can hold the swing");
            Assert.Greater((double)big / withSwing, 0.9, "a swing is a jump bigger than a normal day");
        }

        [Test]
        public void HiddenSwingIsDeterministicForSeedAndTurn()
        {
            // Same seed/turn twice: identical (pure function), and the swing bundle is part of that result.
            CollectionAssert.AreEqual(RandomMarketGenerator.SwingBundles(5, 4), RandomMarketGenerator.SwingBundles(5, 4));
            MarketData a = RandomMarketGenerator.Generate(5, 4), b = new RandomMarketGenerator(5).ForTurn(4);
            for (int i = 0; i < a.companies.Length; i++)
                CollectionAssert.AreEqual(a.companies[i].dailyChangesBps, b.companies[i].dailyChangesBps);
        }

        [Test]
        public void FreeBundlesAreSpreadOverAllPairs()
        {
            var pairs = new System.Collections.Generic.HashSet<string>();
            for (int seed = 0; seed < 200; seed++)
                foreach (CompanyMarketData c in RandomMarketGenerator.Generate(seed, 1).companies)
                    pairs.Add(c.publicBundleIndices[0] + "-" + c.publicBundleIndices[1]);
            Assert.AreEqual(15, pairs.Count); // all 6C2 pairs appear
        }

        [Test]
        public void ForTurnIsCachedAndReturnsCopies()
        {
            var g = new RandomMarketGenerator(99);
            MarketData first = g.ForTurn(3);
            first.companies[0].dailyChangesBps[5] = 123456;
            first.companies[0].publicBundleIndices[0] = 5;
            MarketData again = g.ForTurn(3);
            CollectionAssert.AreEqual(RandomMarketGenerator.Generate(99, 3).companies[0].dailyChangesBps, again.companies[0].dailyChangesBps);
            CollectionAssert.AreEqual(RandomMarketGenerator.Generate(99, 3).companies[0].publicBundleIndices, again.companies[0].publicBundleIndices);
            Assert.Throws<ArgumentOutOfRangeException>(() => g.ForTurn(0));
        }

        [Test]
        public void ServiceUsesTheGeneratorForEveryTurnAndKeepsPastTurns()
        {
            MarketService s = MarketService.CreateRandom(2026);
            for (int turn = 1; turn <= 10; turn++)
            {
                s.BeginTurn(Ctx(Protocol.Info, turn: turn), Players);
                s.BeginTurn(Ctx(Protocol.Info, turn: turn), Players); // repeated start: no re-roll
                MarketData expected = RandomMarketGenerator.Generate(2026, turn);
                foreach (CompanyMarketData c in expected.companies)
                    for (int b = 0; b < 6; b++)
                    {
                        ServerInformationBundle bundle = s.GetBundle(turn, c.companyId, b);
                        CollectionAssert.AreEqual(c.dailyChangesBps.Skip(1 + 5 * b).Take(5).ToArray(), bundle.DailyChangesBps);
                        Assert.AreEqual(c.publicBundleIndices.Contains(b), bundle.IsPublic);
                    }
                PlayTurn(s, turn);
            }
            // Past turn still readable and unchanged after 10 turns.
            CollectionAssert.AreEqual(RandomMarketGenerator.Generate(2026, 1).companies[0].dailyChangesBps.Skip(1).Take(5).ToArray(),
                s.GetBundle(1, RandomMarketGenerator.Generate(2026, 1).companies[0].companyId, 0).DailyChangesBps);
            Assert.AreEqual(11, s.GetScoreboard(10).rows[0].returnHistory.Length); // turn 0 + 10 turns
            Assert.IsTrue(s.GetScoreboard(10).rows.All(r => r.totalAssets >= 0));
        }

        // Every player: first two free companies by seat order, safe 50:50 0->5 order.
        private static void PlayTurn(MarketService s, int turn)
        {
            string[] order = { "P1", "P2", "P3", "P4", "P4", "P3", "P2", "P1" };
            foreach (string p in order)
            {
                string pick = s.GetCompanySeats().First(c => c.remainingSeats > 0 && !s.GetSelectedCompanies(p).Contains(c.companyId)).companyId;
                Assert.IsTrue(s.ValidateAndSelectCompany(Ctx(Protocol.Selection, p, turn), pick).Success);
            }
            foreach (string p in Players)
            {
                string[] mine = s.GetSelectedCompanies(p);
                var order2 = mine.Select(id => new Investment { companyId = id, weightBps = 5000,
                    intervals = new[] { new Interval { entryDay = 0, exitDay = 5 } } }).ToArray();
                Assert.IsTrue(s.SubmitInvestment(Ctx(Protocol.Investment, p, turn), order2).Success, p + " turn " + turn);
            }
            s.LockAllOrders(Ctx(Protocol.Investment, turn: turn));
            s.SettleTurn(Ctx(Protocol.Reveal, turn: turn));
        }
    }
}
