using System;
using System.Collections.Generic;
using System.Linq;
using BlackWhale.Shared;

namespace BlackWhale.Rules
{
    /// <summary>
    /// 실시간 시장 + 뉴스 + 턴 미션. 조정값은 <see cref="NewsRules"/>.
    ///
    /// 미리 정한 패턴(기본 시세) 위에, 실시간 매매 중 하루가 지날 때마다 '오늘의 추가 등락'을 더한다.
    /// 추가 등락은 그날부터 이후 날짜 전체로 이어지므로(계단), 미리 본 정보 점들도 함께 움직이고
    /// 그래프가 처음 보였던 바닥·천장을 계속 벗어난다.
    ///
    /// 기업 성격(13주차 발표 '주가 그래프' 슬라이드)
    /// - AUTO : 기본 패턴대로만 움직인다 → 정보가 가장 중요. (속보는 가끔)
    /// - FOOD : 기본 패턴 + 시장 반응. 투자한 사람이 많을수록 변동폭이 커진다.
    /// - AI   : 시장 반응이 주가를 정한다(패턴은 40%만). 추세가 이어지고, 3명 이상 몰리거나 상승이 길어질수록 붕괴 확률↑.
    /// - BIO  : 시장 반응이 주가를 정한다(패턴은 40%만). 보유가 쌓일수록 '대박' 급등 확률↑, 기다리는 동안은 조금씩 샌다.
    /// - SPACE: 기본 패턴 + 며칠씩 이어지는 상승/하락 추세.
    /// - BANK : 그대로.
    /// AI 붕괴 확률과 BIO 급등 확률은 화면에 그대로 공개한다(PublicState.moods).
    /// </summary>
    public sealed partial class MarketService : IMarketNews
    {
        private sealed class Shock { public string Company; public int Day, Bps, RumorDirection; }
        private readonly Dictionary<int, Shock[]> shocks = new Dictionary<int, Shock[]>();
        private readonly Dictionary<int, TurnMission> missions = new Dictionary<int, TurnMission>();
        private readonly Dictionary<int, Dictionary<string, long>> missionBonuses = new Dictionary<int, Dictionary<string, long>>();
        private readonly List<MarketNews> liveNews = new List<MarketNews>();
        private int aiMomentum, aiUpStreak, bioPressure, spaceRegime, spaceDaysLeft;
        private bool aiWarned, bioWarned;

        /// <summary>실시간 시장·뉴스·미션 사용 여부(CreateStockSystem의 marketNews).</summary>
        public bool NewsEnabled { get; private set; }
        public bool LiveMarket => StockEnabled && NewsEnabled;
        private static readonly StockVolatilitySettings NewsVolatility = new StockVolatilitySettings();
        private static int Limit(string company) => NewsVolatility.DailyLimitBps(company);

        /// <summary>실시간 시장의 기본 시세: AI·BIO는 패턴을 40%로 줄인다(나머지는 시장 반응이 정함).</summary>
        private static MarketData LiveBase(MarketData data)
        {
            foreach (var c in data.companies.Where(c => c.companyId == "AI" || c.companyId == "BIO"))
                for (int day = 1; day < c.dailyChangesBps.Length; day++) c.dailyChangesBps[day] = c.dailyChangesBps[day] * NewsRules.LiveOnlyPatternPercent / 100;
            return data;
        }

        /// <summary>seed·턴이 같으면 항상 같은 속보(패턴 기업 FOOD·AUTO·SPACE 중). 서로 4일 이상 떨어진다.</summary>
        private static Shock[] PlanShocks(int seed, int turn)
        {
            var random = new Random(unchecked(seed * 486187739 ^ turn * 7368787 ^ 0x5EED));
            var companies = NewsRules.ShockCompanies.OrderBy(id => random.Next()).Take(NewsRules.ShocksPerTurn).ToArray();
            var result = new List<Shock>();
            foreach (string company in companies)
            {
                int day, guard = 0;
                do day = random.Next(NewsRules.FirstShockDay, NewsRules.LastShockDay + 1);
                while (result.Any(s => Math.Abs(s.Day - day) < 4) && ++guard < 50);
                int sign = random.Next(2) == 0 ? -1 : 1;
                int size = Limit(company) * random.Next(NewsRules.ShockMinTenths, NewsRules.ShockMaxTenths + 1) / 10;
                bool honest = random.Next(100) < NewsRules.RumorAccuracyPercent;
                result.Add(new Shock { Company = company, Day = day, Bps = sign * size, RumorDirection = honest ? sign : -sign });
            }
            return result.OrderBy(s => s.Day).ToArray();
        }

        private void BeginNewsTurn()
        {
            liveNews.Clear();
            aiMomentum = 0; aiUpStreak = 0; bioPressure = 0; spaceRegime = 0; spaceDaysLeft = 0; aiWarned = bioWarned = false;
            shocks[turn] = StockEnabled && NewsEnabled ? PlanShocks(stockSeed, turn) : Array.Empty<Shock>();
            missions[turn] = StockEnabled && NewsEnabled && !SwapRules.IsSwapTurn(turn) ? PlanMission(stockSeed, turn) : null;
        }

        /// <summary>시세 책은 항상 '지금 알 수 있는 그림'이다: 지난날 = 실제, 앞으로 = 기본 패턴 + 지금까지 쌓인 추가 등락.</summary>
        private int KnownBps(int requestedTurn, string company, int day) => markets[requestedTurn].ChangeBps(company, day);

        // ---------------- 실시간 등락 ----------------

        // 하루·기업마다 독립된 난수(되돌리기·재실행해도 같은 행동이면 같은 결과).
        private Random DayRandom(string company, int day)
            => new Random(unchecked(stockSeed * 1000003 ^ turn * 92821 ^ day * 7919 ^ (Array.IndexOf(InvestmentRules.CompanyIds, company) + 1) * 104729));
        private static int Noise(Random r, int amplitude) => amplitude <= 0 ? 0 : r.Next(-amplitude, amplitude + 1);
        private int Holders(string company) => players.Count(id => stockPlayers.TryGetValue(id, out var p) && ActiveShares(p, company) > 0);
        private int HeldShares(string company) => players.Sum(id => stockPlayers.TryGetValue(id, out var p) ? ActiveShares(p, company) : 0);

        /// <summary>오늘 AI가 무너질 확률(%). 상승이 길어질수록, 사람이 몰릴수록 오른다. 화면에 그대로 보여 준다.</summary>
        public int AiCrashPercent()
            => Math.Min(70, NewsRules.AiBaseCrashPercent + NewsRules.AiStreakCrashPercent * Math.Max(0, aiUpStreak - 1)
                + (Holders("AI") >= 3 ? NewsRules.AiCrowdCrashPercent : 0) + (HeldShares("AI") >= 20 ? NewsRules.AiCrowdCrashPercent / 2 : 0));
        // 경고는 턴마다 기업별 한 번만(같은 경고가 계속 뜨지 않게)
        /// <summary>오늘 BIO가 급등할 확률(%). 모두의 BIO 보유가 쌓일수록 오른다.</summary>
        public int BioSpikePercent() => Math.Min(60, NewsRules.BioBaseSpikePercent + bioPressure / NewsRules.BioPressurePerPercent);

        private void ApplyLiveMoves(int day)
        {
            foreach (string company in InvestmentRules.CompanyIds)
            {
                if (company == "BANK") continue;
                int L = Limit(company);
                int baseMove = market.ChangeBps(company, day) - market.ChangeBps(company, day - 1);
                var r = DayRandom(company, day);
                int extra = 0;
                string headline = null;
                switch (company)
                {
                    case "FOOD":
                        extra = baseMove * NewsRules.FoodAmplifyPercentPerHolder * Holders(company) / 100 + Noise(r, L * 20 / 100);
                        break;
                    case "AI":
                    {
                        int crash = AiCrashPercent();
                        if (r.Next(100) < crash)
                        {
                            extra = -L * r.Next(20, 36) / 10;
                            headline = "[속보] AI 과열 붕괴! " + (extra / 100) + "%p · 붕괴 확률이 " + crash + "%였습니다";
                            aiMomentum = -1;
                        }
                        else extra = L * NewsRules.AiDriftPercent / 100 + aiMomentum * L * 30 / 100 + Noise(r, L * 50 / 100); // 무너지기 전까지는 오르는 쪽
                        break;
                    }
                    case "BIO":
                    {
                        int spike = BioSpikePercent();
                        if (r.Next(100) < spike)
                        {
                            extra = L * r.Next(NewsRules.BioSpikeMinTenths, NewsRules.BioSpikeMaxTenths + 1) / 10;
                            headline = "[속보] BIO 임상 대성공! +" + (extra / 100) + "%p · 급등 확률이 " + spike + "%였습니다";
                            bioPressure = 0;
                        }
                        else extra = (HeldShares(company) > 0 ? -L * NewsRules.BioBleedPercent / 100 : 0) + Noise(r, L * 30 / 100);
                        break;
                    }
                    case "SPACE":
                        if (spaceDaysLeft <= 0) { spaceRegime = r.Next(2) == 0 ? -1 : 1; spaceDaysLeft = r.Next(3, 8); }
                        extra = spaceRegime * L * 30 / 100 + Noise(r, L * 25 / 100);
                        spaceDaysLeft--;
                        break;
                }
                var shock = shocks.TryGetValue(turn, out var list) ? list.FirstOrDefault(s => s.Company == company && s.Day == day) : null;
                if (shock != null) extra += shock.Bps;
                int floor = NewsRules.LowestBps - market.ChangeBps(company, day);
                if (extra < floor) extra = floor; // 주가가 0 밑으로는 가지 않는다
                if (extra != 0) market.Shift(company, day, extra);

                int move = market.ChangeBps(company, day) - market.ChangeBps(company, day - 1);
                if (company == "AI")
                {
                    aiUpStreak = move > 0 ? aiUpStreak + 1 : 0;
                    if (headline == null) aiMomentum = r.Next(100) < 65 ? Math.Sign(move) : 0;
                }
                if (company == "BIO") bioPressure += HeldShares(company);
                if (headline != null) AddNews(day, day, company, NewsRules.Event, Math.Sign(extra), extra, headline);
                if (shock != null)
                    AddNews(day, day, company, NewsRules.Breaking, Math.Sign(shock.Bps), shock.Bps,
                        "[속보] " + company + (shock.Bps > 0 ? " 깜짝 호재! " : " 충격 악재! ") + (shock.Bps > 0 ? "+" : "") + (shock.Bps / 100) + "%p");
            }
            // 경고: 확률이 문턱을 넘는 순간 한 번
            int aiNext = AiCrashPercent(), bioNext = BioSpikePercent();
            if (!aiWarned && aiNext >= NewsRules.WarnPercent)
            { aiWarned = true; AddNews(day, 0, "AI", NewsRules.Warning, -1, 0, "[경고] AI 과열 · 내일 붕괴 확률 " + aiNext + "%"); }
            if (!bioWarned && bioNext >= NewsRules.WarnPercent)
            { bioWarned = true; AddNews(day, 0, "BIO", NewsRules.Warning, 1, 0, "[찌라시] BIO 발표 임박 · 내일 급등 확률 " + bioNext + "%"); }
            // 속보 루머(며칠 전)
            if (shocks.TryGetValue(turn, out var planned))
                foreach (var s in planned.Where(s => Math.Max(1, s.Day - NewsRules.RumorLeadDays) == day && s.Day > day))
                    AddNews(day, s.Day, s.Company, NewsRules.Rumor, s.RumorDirection, 0,
                        "[루머] " + s.Company + (s.RumorDirection > 0 ? " 대형 호재" : " 대형 악재") + " 소문 · " + s.Day + "일 발표 예정 (적중률 " + NewsRules.RumorAccuracyPercent + "%)",
                        NewsRules.RumorAccuracyPercent);
        }

        private void AddNews(int day, int eventDay, string company, string kind, int direction, int move, string text, int accuracy = 0)
            => liveNews.Add(new MarketNews { day = day, eventDay = eventDay, companyId = company, kind = kind, direction = direction,
                moveBps = move, text = text, accuracyPercent = accuracy });

        private Action CaptureLiveMarket()
        {
            var books = markets.ToDictionary(p => p.Key, p => p.Value.Snapshot());
            var news = liveNews.Select(CopyNews).ToList();
            int m = aiMomentum, up = aiUpStreak, pressure = bioPressure, regime = spaceRegime, left = spaceDaysLeft; bool aw = aiWarned, bw = bioWarned;
            return () =>
            {
                foreach (var restore in books.Values) restore();
                liveNews.Clear(); liveNews.AddRange(news.Select(CopyNews));
                aiMomentum = m; aiUpStreak = up; bioPressure = pressure; spaceRegime = regime; spaceDaysLeft = left; aiWarned = aw; bioWarned = bw;
            };
        }
        private static MarketNews CopyNews(MarketNews n) => new MarketNews { day = n.day, eventDay = n.eventDay, companyId = n.companyId, kind = n.kind,
            direction = n.direction, moveBps = n.moveBps, text = n.text, accuracyPercent = n.accuracyPercent };

        /// <summary>지금까지 나온 이번 턴 뉴스(오래된 것부터).</summary>
        public MarketNews[] GetNews() => StockEnabled ? liveNews.Select(CopyNews).ToArray() : Array.Empty<MarketNews>();

        /// <summary>기업별 지금 분위기(화면 표시용). 정보 탐색부터 주문 잠금 전까지.</summary>
        public CompanyMood[] GetMoods()
        {
            if (!StockEnabled || !NewsEnabled || allLocked || turn == 0) return Array.Empty<CompanyMood>();
            int holders = Holders("FOOD");
            return new[] {
                new CompanyMood { companyId = "AI", percent = AiCrashPercent(), text = "붕괴 확률 " + AiCrashPercent() + "%" },
                new CompanyMood { companyId = "BIO", percent = BioSpikePercent(), text = "급등 확률 " + BioSpikePercent() + "%" },
                new CompanyMood { companyId = "FOOD", percent = 100 + holders * NewsRules.FoodAmplifyPercentPerHolder, text = "변동폭 ×" + (100 + holders * NewsRules.FoodAmplifyPercentPerHolder) / 10 / 10.0 },
            };
        }

        // ---------------- 턴 미션 ----------------

        private static readonly string[] MissionIds = { "HOLD_10", "SPREAD_4", "NEWS_RIDE", "HIDDEN_BRAVE" };

        private static TurnMission PlanMission(int seed, int turn)
        {
            var random = new Random(unchecked(seed * 92821 ^ turn * 131071 ^ 0x3A11));
            string id = MissionIds[random.Next(MissionIds.Length)];
            string company = InvestmentRules.CompanyIds.Where(c => c != "BANK").ElementAt(random.Next(5));
            string reward = "성공하면 턴 시작 자산의 +" + NewsRules.MissionRewardBps / 100 + "%";
            switch (id)
            {
                case "HOLD_10": return new TurnMission { id = id, companyId = company, rewardBps = NewsRules.MissionRewardBps,
                    title = company + " 10일 보유", description = company + " 주식을 1주 이상 들고 있는 날이 10일 이상 · " + reward };
                case "SPREAD_4": return new TurnMission { id = id, rewardBps = NewsRules.MissionRewardBps,
                    title = "4개 기업 동시 보유", description = "하루라도 BANK 말고 4개 기업을 동시에 보유 · " + reward };
                case "NEWS_RIDE": return new TurnMission { id = id, rewardBps = NewsRules.MissionRewardBps,
                    title = "속보 올라타기", description = "[속보]가 터지는 날, 그 기업을 보유하고 있기(호재든 악재든) · " + reward };
                default: return new TurnMission { id = id, rewardBps = NewsRules.MissionRewardBps,
                    title = "hidden 버티기", description = "26~30일 내내 BANK 말고 기업 주식을 10주 이상 보유 · " + reward };
            }
        }

        public TurnMission GetMission() => missions.TryGetValue(turn, out var m) && m != null
            ? new TurnMission { id = m.id, companyId = m.companyId, title = m.title, description = m.description, rewardBps = m.rewardBps } : null;

        private bool MissionCleared(string player, TurnMission mission)
        {
            var q = stockPlayers[player].DailyQuantity;
            switch (mission.id)
            {
                case "HOLD_10": return q[mission.companyId].Skip(1).Count(v => v > 0) >= 10;
                case "SPREAD_4": return Enumerable.Range(1, 30).Any(d => InvestmentRules.CompanyIds.Count(c => c != "BANK" && q[c][d] > 0) >= 4);
                case "NEWS_RIDE": return liveNews.Any(n => (n.kind == NewsRules.Breaking || n.kind == NewsRules.Event) && q[n.companyId][n.day] > 0);
                case "HIDDEN_BRAVE": return Enumerable.Range(26, 5).All(d => InvestmentRules.CompanyIds.Where(c => c != "BANK").Sum(c => q[c][d]) >= 10);
                default: return false;
            }
        }

        private void ApplyMissionBonus(PlayerSettlement[] results)
        {
            missionBonuses.Remove(turn);
            if (!missions.TryGetValue(turn, out var mission) || mission == null) return;
            var bonuses = new Dictionary<string, long>();
            foreach (var r in results)
            {
                long bonus = MissionCleared(r.playerId, mission) ? r.startingAssets * mission.rewardBps / 10000 : 0;
                bonuses[r.playerId] = bonus;
                if (bonus == 0) continue;
                r.endingAssets += bonus;
                r.profit = r.endingAssets - r.startingAssets;
                r.turnReturnBps = Settlement.ReturnBps(r.startingAssets, r.endingAssets);
            }
            missionBonuses[turn] = bonuses;
        }

        private long MissionBonusOf(string player)
            => missionBonuses.TryGetValue(turn, out var map) && map.TryGetValue(player, out var b) ? b : 0;
    }
}
