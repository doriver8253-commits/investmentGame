using System;
using System.Collections.Generic;
using System.Linq;
using BlackWhale.Flow;
using BlackWhale.Rules;
using BlackWhale.Rules.Information;
using BlackWhale.Shared;
using BlackWhale.UI.State;

namespace BlackWhale.Tests
{
    /// <summary>실시간 뉴스(루머·속보)와 턴 미션 검증.</summary>
    public static class NewsValidation
    {
        private static void Check(bool ok, string message) => RoomValidation.Check(ok, "NEWS: " + message);
        private static readonly string[] Players = { "P1", "P2", "P3", "P4" };
        private static RequestContext Ctx(int turn, string phase, string player = "") => new RequestContext("N", turn, phase, player);

        public static string[] Run()
        {
            var names = new List<string>();
            Action<string, Action> run = (name, test) => { test(); names.Add("NEWS/" + name); };
            run("shocks-are-steps-on-top-of-the-same-pattern", Steps);
            run("information-shows-only-shocks-that-already-happened", KnownValues);
            run("rumor-three-days-early-then-breaking-news", Feed);
            run("mission-bonus-for-clearing-player-only", Mission);
            run("room-publishes-news-and-mission-and-skips-mission-on-swap-turn", Room);
            return names.ToArray();
        }

        private static (MarketService plain, MarketService news) Pair(int seed, int turn = 1)
        {
            var plain = MarketService.CreateStockSystem(seed);
            var news = MarketService.CreateStockSystem(seed, marketNews: true);
            for (int t = 1; t <= turn; t++)
            {
                plain.BeginTurn(Ctx(t, Protocol.Info), Players); news.BeginTurn(Ctx(t, Protocol.Info), Players);
            }
            return (plain, news);
        }

        private static int[] Actual(MarketService m, int turn, string company)
        {
            m.LockPortfolios(); // 잠그면 전부 처리된 실제 시세가 보인다
            return m.GetWindow(turn, company, 0, 31);
        }

        private static void Steps()
        {
            for (int seed = 1; seed <= 40; seed++)
            {
                var (plain, news) = Pair(seed);
                var diffs = InvestmentRules.CompanyIds.ToDictionary(c => c, c => {
                    var a = Actual(news, 1, c); var b = Actual(plain, 1, c);
                    return Enumerable.Range(0, 31).Select(d => a[d] - b[d]).ToArray(); });
                Check(diffs["BANK"].All(v => v == 0), "BANK never gets news");
                var hit = diffs.Where(p => p.Value.Any(v => v != 0)).ToArray();
                Check(hit.Length == NewsRules.ShocksPerTurn, "exactly two companies get a shock each turn");
                foreach (var p in hit)
                {
                    int day = Array.FindIndex(p.Value, v => v != 0);
                    Check(day >= NewsRules.FirstShockDay && day <= NewsRules.LastShockDay, "shock day before the hidden prelude");
                    Check(p.Value.Skip(day).All(v => v == p.Value[day]), "a shock shifts every later day by the same amount (a step)");
                    Check(Math.Abs(p.Value[day]) >= new StockVolatilitySettings().DailyLimitBps(p.Key) * NewsRules.ShockMinTenths / 10, "shock is bigger than a normal daily limit");
                }
                var again = Pair(seed).news;
                Check(InvestmentRules.CompanyIds.All(c => Actual(again, 1, c).SequenceEqual(Actual(news, 1, c))), "same seed, same news");
            }
        }

        private static void KnownValues()
        {
            var (plain, news) = Pair(11);
            var shockCompany = InvestmentRules.CompanyIds.First(c => !Actual(Pair(11).news, 1, c).SequenceEqual(Actual(Pair(11).plain, 1, c)));
            var actual = Actual(Pair(11).news, 1, shockCompany);
            var basePath = Actual(Pair(11).plain, 1, shockCompany);
            int day = Enumerable.Range(0, 31).First(d => actual[d] != basePath[d]);
            news.BeginLiveTrading();
            Check(news.GetWindow(1, shockCompany, 1, 30).SequenceEqual(basePath.Skip(1)), "before the news, bought information shows the plain pattern");
            Check(news.GetOpenWindow(1, shockCompany, 1, 30).SequenceEqual(basePath.Take(30)), "opens also hide future news");
            while (news.ProcessedDay < day - 1) news.SimulateDay();
            Check(news.GetWindow(1, shockCompany, day, 1)[0] == basePath[day], "the day before, still the old picture");
            news.SimulateDay();
            Check(news.GetWindow(1, shockCompany, 1, 30).SequenceEqual(actual.Skip(1)), "after the news, past and future known points move together");
        }

        private static void Feed()
        {
            var news = Pair(5).news;
            news.BeginLiveTrading();
            Check(news.GetNews().Length == 0, "no news before trading starts");
            var seen = new List<MarketNews>();
            for (int day = 1; day <= 30; day++)
            {
                news.SimulateDay();
                var feed = news.GetNews();
                Check(feed.Length >= seen.Count && feed.Take(seen.Count).Select(n => n.text).SequenceEqual(seen.Select(n => n.text)), "news only grows, oldest first");
                foreach (var n in feed.Skip(seen.Count)) Check(n.day == day, "news appears on its own day");
                seen = feed.ToList();
            }
            Check(seen.Count == NewsRules.ShocksPerTurn * 2, "one rumor and one breaking story per shock");
            foreach (var b in seen.Where(n => n.kind == NewsRules.Breaking))
            {
                var r = seen.Single(n => n.kind == NewsRules.Rumor && n.companyId == b.companyId);
                Check(r.day == Math.Max(1, b.day - NewsRules.RumorLeadDays) && r.eventDay == b.day, "rumor comes three days early and names the day");
                Check(r.accuracyPercent == NewsRules.RumorAccuracyPercent && (r.direction == 1 || r.direction == -1), "rumor shows its honest accuracy");
                Check(Math.Sign(b.moveBps) == b.direction && b.text.Contains(b.companyId), "breaking news states the real move");
            }
            int right = 0, total = 0;
            for (int seed = 1; seed <= 300; seed++)
            {
                var m = Pair(seed).news; m.BeginLiveTrading(); while (m.ProcessedDay < 30) m.SimulateDay();
                var f = m.GetNews();
                foreach (var b in f.Where(n => n.kind == NewsRules.Breaking))
                { total++; if (f.Single(n => n.kind == NewsRules.Rumor && n.companyId == b.companyId).direction == b.direction) right++; }
            }
            double rate = 100.0 * right / total;
            Check(rate > NewsRules.RumorAccuracyPercent - 8 && rate < NewsRules.RumorAccuracyPercent + 8, "rumor accuracy is close to the stated " + NewsRules.RumorAccuracyPercent + "% (got " + rate.ToString("0.0") + "%)");
            Check(!Pair(5).plain.GetNews().Any() && Pair(5).plain.GetMission() == null, "news and missions stay off unless enabled");
        }

        private static void Mission()
        {
            var seen = new HashSet<string>();
            for (int seed = 1; seed <= 60; seed++)
            {
                var m = Pair(seed).news;
                var mission = m.GetMission();
                Check(mission != null && mission.rewardBps == NewsRules.MissionRewardBps && !string.IsNullOrEmpty(mission.title), "normal turn has a public mission");
                seen.Add(mission.id);
                // 미리 실제 시세로 속보 기업을 알아낸 뒤, P1은 미션 기업 + 속보 기업 + 나머지로 4개 기업을 30일 내내 들고 간다.
                var probe = Pair(seed);
                var shockCompanies = InvestmentRules.CompanyIds.Where(c => !Actual(probe.news, 1, c).SequenceEqual(Actual(probe.plain, 1, c))).ToList();
                var hold = new List<string>();
                if (mission.companyId != null) hold.Add(mission.companyId);
                foreach (var c in shockCompanies.Concat(InvestmentRules.CompanyIds.Where(c => c != "BANK"))) if (!hold.Contains(c) && hold.Count < 4) hold.Add(c);
                m.BeginLiveTrading();
                int[] sizes = { 8, 8, 7, 7 };
                for (int i = 0; i < hold.Count; i++)
                    Check(m.SetPosition(Ctx(1, Protocol.Investment, "P1"), new ShareOrder { companyId = hold[i], targetShares = sizes[i] }).Success, "P1 position");
                m.LockPortfolios();
                var board = m.SettleTurn(Ctx(1, Protocol.Reveal));
                var detail = m.GetSettlementDetail(1).ToDictionary(p => p.playerId);
                var p1 = board.rows.Single(r => r.playerId == "P1");
                var p2 = board.rows.Single(r => r.playerId == "P2");
                Check(p1.missionBonus == 10000L * NewsRules.MissionRewardBps / 10000, "P1 clears the mission (" + mission.id + ")");
                Check(p2.missionBonus == 0, "P2 (all BANK) does not");
                long withoutBonus = detail["P1"].companyProfits.Sum(c => c.profit) + 10000;
                Check(detail["P1"].endingAssets == Math.Max(0, withoutBonus) + p1.missionBonus && p1.totalAssets == detail["P1"].endingAssets, "bonus is part of total assets");
                Check(DtoCopy.Copy(board).rows.Single(r => r.playerId == "P1").missionBonus == p1.missionBonus, "scoreboard copy keeps the bonus");
            }
            Check(seen.Count == 4, "all four mission kinds appear across seeds");
        }

        private static void Room()
        {
            var clock = new TestClock();
            var market = MarketService.CreateStockSystem(7741, marketNews: true);
            var room = new GameRoom("NEWS", market, new SharedInformationService(market), clock, fixedOrderForFixture: true,
                settings: new GameSettings(5, 6, 2, enableProgressionFeatures: false));
            int seq = 0;
            Func<int, string, CommandPayload, CommandResult> send = (p, type, payload) => room.ProcessCommand("c" + p,
                new Command { type = type, turn = room.Snapshot().turn, requestId = "n" + (++seq), developerRevision = room.Snapshot().developerRevision, payload = payload });
            for (int i = 1; i <= 4; i++) room.Join("c" + i, "Player " + i);
            for (int i = 1; i <= 4; i++) send(i, "SET_READY", new CommandPayload { ready = true });
            send(1, "START_GAME", null);
            Action deadline = () => { var s = room.Snapshot(); clock.Seconds = s.phaseEndsAtServerSeconds; room.Tick(); };
            Check(room.Snapshot().mission != null && room.Snapshot().news.Length == 0, "turn one shows its mission, no news yet");
            while (room.Snapshot().phase != Protocol.Investment) deadline();
            double start = room.Snapshot().phaseEndsAtServerSeconds - 30 * StockRules.DaySeconds;
            clock.Seconds = start + 30 * StockRules.DaySeconds - 0.01; room.Tick();
            var s1 = room.Snapshot();
            Check(s1.news.Count(n => n.kind == NewsRules.Breaking) == NewsRules.ShocksPerTurn, "live trading publishes the breaking news");
            Check(DtoCopy.Copy(s1).news.Length == s1.news.Length && DtoCopy.Copy(s1).mission.id == s1.mission.id, "client copy keeps news and mission");
            int swapTurn = SwapRules.Turns.FirstOrDefault();
            while (room.Snapshot().phase != Protocol.Ended)
            {
                var s = room.Snapshot();
                if (s.turn == swapTurn && s.phase == Protocol.Info) Check(s.swapTurn && s.mission == null, "swap turn has no mission");
                if (s.turn != swapTurn && s.phase == Protocol.Info) Check(s.mission != null, "other turns have one");
                deadline();
            }
            Check(market.GetScoreboard(6)?.gameComplete == true, "game finishes with news on");
        }
    }
}
