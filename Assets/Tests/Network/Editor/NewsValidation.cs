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
            run("same-seed-same-actions-same-market", Deterministic);
            run("auto-follows-its-pattern-except-breaking-news", AutoPattern);
            run("information-shows-pattern-plus-moves-so-far", KnownValues);
            run("ai-crowding-raises-crash-and-bio-holding-raises-spike", Crowd);
            run("live-price-leaves-the-first-visible-range", NoCeiling);
            run("rumor-three-days-early-then-breaking-news", Feed);
            run("mission-bonus-for-clearing-player-only", Mission);
            run("game-chart-axis-leaves-room-above-and-below", ChartAxis);
            run("room-publishes-news-moods-and-mission-and-skips-mission-on-swap-turn", Room);
            return names.ToArray();
        }

        private static MarketService Live(int seed, bool news = true)
        {
            var m = MarketService.CreateStockSystem(seed, marketNews: news);
            m.BeginTurn(Ctx(1, Protocol.Info), Players);
            m.BeginLiveTrading();
            return m;
        }
        private static void Hold(MarketService m, string player, string company, int shares)
            => Check(m.SetPosition(Ctx(1, Protocol.Investment, player), new ShareOrder { companyId = company, targetShares = shares }).Success, "position " + player + " " + company);
        private static int[] Prices(MarketService m, string company) => m.GetWindow(1, company, 0, 31);
        private static void Finish(MarketService m) { while (m.ProcessedDay < 30) m.SimulateDay(); m.LockPortfolios(); }

        private static void Deterministic()
        {
            Func<MarketService> play = () => { var m = Live(21); Hold(m, "P1", "AI", 10); Hold(m, "P2", "BIO", 10);
                for (int d = 0; d < 12; d++) m.SimulateDay(); Hold(m, "P1", "AI", 0); Finish(m); return m; };
            var a = play(); var b = play();
            Check(InvestmentRules.CompanyIds.All(c => Prices(a, c).SequenceEqual(Prices(b, c))), "same prices");
            Check(a.GetNews().Select(n => n.text).SequenceEqual(b.GetNews().Select(n => n.text)), "same news");
            bool changed = false;
            for (int seed = 1; seed <= 20 && !changed; seed++)
            {
                var alone = Live(seed); Hold(alone, "P1", "AI", 5); Finish(alone);
                var crowd = Live(seed); foreach (var p in Players) Hold(crowd, p, "AI", 5); Finish(crowd);
                changed = !Prices(alone, "AI").SequenceEqual(Prices(crowd, "AI"));
            }
            Check(changed, "different actions can change the crowd-driven market");
        }

        private static void AutoPattern()
        {
            int shocked = 0;
            for (int seed = 1; seed <= 60; seed++)
            {
                var news = Live(seed); var plain = Live(seed, false);
                var before = Prices(news, "AUTO");
                Check(before.SequenceEqual(Prices(plain, "AUTO")), "AUTO starts on the same pattern");
                Finish(news); Finish(plain);
                var a = Prices(news, "AUTO"); var b = Prices(plain, "AUTO");
                var diff = Enumerable.Range(0, 31).Select(d => a[d] - b[d]).ToArray();
                var breaking = news.GetNews().Where(n => n.companyId == "AUTO" && n.kind == NewsRules.Breaking).ToArray();
                if (breaking.Length == 0) { Check(diff.All(v => v == 0), "AUTO without news is exactly its pattern"); continue; }
                shocked++;
                int day = breaking[0].day;
                Check(day >= NewsRules.FirstShockDay && day <= NewsRules.LastShockDay, "news day before the hidden prelude");
                Check(diff.Take(day).All(v => v == 0) && diff.Skip(day).All(v => v == breaking[0].moveBps), "AUTO news is one step");
                Check(Math.Abs(breaking[0].moveBps) >= new StockVolatilitySettings().DailyLimitBps("AUTO") * NewsRules.ShockMinTenths / 10, "news is bigger than a normal day");
                Check(Prices(news, "BANK").SequenceEqual(Prices(plain, "BANK")), "BANK unchanged");
            }
            Check(shocked > 10, "AUTO gets news on some turns");
        }

        private static void KnownValues()
        {
            var m = Live(11); Hold(m, "P1", "AI", 10);
            var start = Prices(m, "AI");
            for (int d = 0; d < 10; d++) m.SimulateDay();
            var mid = Prices(m, "AI");
            int offset = mid[10] - start[10];
            Check(Enumerable.Range(11, 20).All(d => mid[d] - start[d] == offset), "future known points = pattern + everything that happened so far");
            Finish(m);
            var end = Prices(m, "AI");
            Check(end.Take(11).SequenceEqual(mid.Take(11)), "past days never change");
            Check(m.GetOpenWindow(1, "AI", 1, 30).SequenceEqual(end.Take(30)), "opens follow the same picture");
        }

        private static int Events(MarketService m, string company) => m.GetNews().Count(n => n.companyId == company && n.kind == NewsRules.Event);

        private static void Crowd()
        {
            int crowdedCrashes = 0, emptyCrashes = 0, heldSpikes = 0, emptySpikes = 0;
            for (int seed = 1; seed <= 150; seed++)
            {
                var crowded = Live(seed); foreach (var p in Players) Hold(crowded, p, "AI", 5);
                foreach (var p in Players) Hold(crowded, p, "BIO", 5);
                Finish(crowded);
                var empty = Live(seed); Finish(empty);
                crowdedCrashes += Events(crowded, "AI"); emptyCrashes += Events(empty, "AI");
                heldSpikes += Events(crowded, "BIO"); emptySpikes += Events(empty, "BIO");
            }
            Check(crowdedCrashes > emptyCrashes * 2, "four players in AI crash it much more often (" + crowdedCrashes + " vs " + emptyCrashes + ")");
            Check(heldSpikes > emptySpikes * 2, "holding BIO builds up to spikes (" + heldSpikes + " vs " + emptySpikes + ")");
            var probe = Live(3);
            Check(probe.GetMoods().Single(x => x.companyId == "AI").percent == probe.AiCrashPercent(), "AI mood shows the real crash chance");
            foreach (var p in Players) Hold(probe, p, "AI", 5);
            Check(probe.AiCrashPercent() >= NewsRules.AiBaseCrashPercent + NewsRules.AiCrowdCrashPercent, "three or more holders add the crowd risk");
            Check(Live(3, false).GetMoods().Length == 0, "no moods when live market is off");
        }

        private static void NoCeiling()
        {
            foreach (string company in new[] { "AI", "BIO", "SPACE", "FOOD" })
            {
                int broke = 0, total = 0;
                for (int seed = 1; seed <= 80; seed++)
                {
                    var m = Live(seed); Hold(m, "P1", company, 10); Hold(m, "P2", company, 5);
                    var seen = Prices(m, company);
                    int hi = seen.Max(), lo = seen.Min();
                    Finish(m);
                    var end = Prices(m, company);
                    total++; if (end.Max() > hi || end.Min() < lo) broke++;
                }
                Check(broke * 100 >= total * 70, company + " leaves the range you could see at the start in most turns (" + broke + "/" + total + ")");
            }
        }

        private static void Feed()
        {
            int right = 0, total = 0;
            for (int seed = 1; seed <= 300; seed++)
            {
                var m = Live(seed);
                var seen = 0;
                for (int day = 1; day <= 30; day++)
                {
                    m.SimulateDay();
                    var feed = m.GetNews();
                    Check(feed.Length >= seen && feed.Skip(seen).All(n => n.day == day), "news only grows and appears on its own day");
                    seen = feed.Length;
                }
                var all = m.GetNews();
                var breaking = all.Where(n => n.kind == NewsRules.Breaking).ToArray();
                Check(breaking.Length == NewsRules.ShocksPerTurn && breaking.All(b => NewsRules.ShockCompanies.Contains(b.companyId)), "two breaking stories on pattern companies");
                foreach (var b in breaking)
                {
                    var r = all.Single(n => n.kind == NewsRules.Rumor && n.companyId == b.companyId);
                    Check(r.day == b.day - NewsRules.RumorLeadDays && r.eventDay == b.day && r.accuracyPercent == NewsRules.RumorAccuracyPercent, "rumor comes three days early and names the day");
                    Check(Math.Sign(b.moveBps) == b.direction && b.text.Contains(b.companyId), "breaking news states the real move");
                    total++; if (r.direction == b.direction) right++;
                }
            }
            double rate = 100.0 * right / total;
            Check(rate > NewsRules.RumorAccuracyPercent - 8 && rate < NewsRules.RumorAccuracyPercent + 8, "rumor accuracy is close to the stated " + NewsRules.RumorAccuracyPercent + "% (got " + rate.ToString("0.0") + "%)");
            var off = Live(5, false); Finish(off);
            Check(!off.GetNews().Any() && off.GetMission() == null, "news and missions stay off unless enabled");
        }

        private static void ChartAxis()
        {
            var values = new[] { 0, 1200, -800, 300 };
            var r = BlackWhale.UI.Presentation.ChartVerticalRange.OpenEnded(values);
            double top = r.Fraction(values.Max()), bottom = r.Fraction(values.Min());
            Check(top < .8 && bottom > .2 && top - bottom < .56, "visible points use only the middle of the chart, so their high and low do not look like a ceiling and floor");
            var grown = BlackWhale.UI.Presentation.ChartVerticalRange.OpenEnded(values.Concat(new[] { -5000 }));
            Check(grown.MinBps < -5000 && grown.Fraction(-5000) > .2, "a live breakout widens the axis instead of sticking to the edge");
            var ticks = r.AxisTicks(3);
            Check(ticks.Contains(0) && ticks.Length <= 3, "few axis numbers, always including 0%");
        }

        private static void Mission()
        {
            var seen = new HashSet<string>();
            for (int seed = 1; seed <= 60; seed++)
            {
                var m = Live(seed);
                var mission = m.GetMission();
                Check(mission != null && mission.rewardBps == NewsRules.MissionRewardBps && !string.IsNullOrEmpty(mission.title), "normal turn has a public mission");
                seen.Add(mission.id);
                // P1: 속보가 날 수 있는 FOOD·AUTO·SPACE + 미션 기업(겹치면 AI)을 30일 내내 → 네 미션 모두 성공하는 보유
                var hold = new List<string>(NewsRules.ShockCompanies);
                hold.Add(mission.companyId != null && !hold.Contains(mission.companyId) ? mission.companyId : "AI");
                int[] sizes = { 8, 8, 7, 7 };
                for (int i = 0; i < hold.Count; i++) Hold(m, "P1", hold[i], sizes[i]);
                Finish(m);
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
            while (room.Snapshot().phase != Protocol.Info) deadline();
            Check(room.Snapshot().liveMarket && room.Snapshot().moods.Any(x => x.companyId == "AI") && DtoCopy.Copy(room.Snapshot()).liveMarket, "live market flag and moods are visible from the information phase");
            while (room.Snapshot().phase != Protocol.Investment) deadline();
            double start = room.Snapshot().phaseEndsAtServerSeconds - 30 * StockRules.DaySeconds;
            clock.Seconds = start + 30 * StockRules.DaySeconds - 0.01; room.Tick();
            var s1 = room.Snapshot();
            Check(s1.news.Count(n => n.kind == NewsRules.Breaking) == NewsRules.ShocksPerTurn, "live trading publishes the breaking news");
            Check(s1.moods.Any(x => x.companyId == "AI") && DtoCopy.Copy(s1).moods.Length == s1.moods.Length, "live trading publishes moods");
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
