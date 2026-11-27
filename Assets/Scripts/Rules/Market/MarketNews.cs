using System;
using System.Collections.Generic;
using System.Linq;
using BlackWhale.Shared;

namespace BlackWhale.Rules
{
    /// <summary>
    /// 실시간 뉴스(루머 → 속보)와 턴 미션. 규칙 값은 <see cref="NewsRules"/>.
    ///
    /// 속보는 그 날짜부터 주가 전체를 한 번에 옮기는 '계단'이다(모양은 그대로, 높이만 이동).
    /// - 실제 시세(정산·시장 공개)는 속보가 모두 들어간 값.
    /// - 정보(산 날짜·공개 날짜)는 '지금까지 일어난 속보'만 들어간 값. 그래서 속보가 터지면
    ///   미리 본 미래 점들도 같이 움직이고, 그래프가 예전 바닥·천장을 뚫고 나간다.
    /// </summary>
    public sealed partial class MarketService : IMarketNews
    {
        private sealed class Shock { public string Company; public int Day, Bps, RumorDirection; }
        private readonly Dictionary<int, Shock[]> shocks = new Dictionary<int, Shock[]>();
        private readonly Dictionary<int, TurnMission> missions = new Dictionary<int, TurnMission>();
        private readonly Dictionary<int, Dictionary<string, long>> missionBonuses = new Dictionary<int, Dictionary<string, long>>();

        /// <summary>뉴스·미션 사용 여부(CreateStockSystem의 marketNews).</summary>
        public bool NewsEnabled { get; private set; }
        private static readonly StockVolatilitySettings NewsVolatility = new StockVolatilitySettings();

        /// <summary>seed·턴이 같으면 항상 같은 속보. 기업이 겹치지 않고 서로 4일 이상 떨어진다.</summary>
        private static Shock[] PlanShocks(int seed, int turn)
        {
            var random = new Random(unchecked(seed * 486187739 ^ turn * 7368787 ^ 0x5EED));
            var companies = InvestmentRules.CompanyIds.Where(id => id != "BANK").OrderBy(id => random.Next()).Take(NewsRules.ShocksPerTurn).ToArray();
            var result = new List<Shock>();
            foreach (string company in companies)
            {
                int day;
                int guard = 0;
                do day = random.Next(NewsRules.FirstShockDay, NewsRules.LastShockDay + 1);
                while (result.Any(s => Math.Abs(s.Day - day) < 4) && ++guard < 50);
                int sign = random.Next(2) == 0 ? -1 : 1;
                int size = NewsVolatility.DailyLimitBps(company) * random.Next(NewsRules.ShockMinTenths, NewsRules.ShockMaxTenths + 1) / 10;
                bool honest = random.Next(100) < NewsRules.RumorAccuracyPercent;
                result.Add(new Shock { Company = company, Day = day, Bps = sign * size, RumorDirection = honest ? sign : -sign });
            }
            return result.OrderBy(s => s.Day).ToArray();
        }

        /// <summary>패턴 시세에 속보 계단을 더한다(실제 시세).</summary>
        private static MarketData ApplyShocks(MarketData data, int seed, int turn)
        {
            foreach (var shock in PlanShocks(seed, turn))
            {
                var values = data.companies.First(c => c.companyId == shock.Company).dailyChangesBps;
                for (int day = shock.Day; day < values.Length; day++) values[day] += shock.Bps;
            }
            return data;
        }

        private void BeginNewsTurn()
        {
            shocks[turn] = StockEnabled && NewsEnabled ? PlanShocks(stockSeed, turn) : Array.Empty<Shock>();
            missions[turn] = StockEnabled && NewsEnabled && !SwapRules.IsSwapTurn(turn) ? PlanMission(stockSeed, turn) : null;
        }

        /// <summary>플레이어가 '지금' 알 수 있는 값: 아직 안 일어난 속보는 빼고 보여 준다.</summary>
        private int KnownBps(int requestedTurn, string company, int day)
        {
            int actual = markets[requestedTurn].ChangeBps(company, day);
            if (requestedTurn != turn || allLocked || !shocks.TryGetValue(requestedTurn, out var list)) return actual;
            foreach (var s in list)
                if (s.Company == company && s.Day > ProcessedDay && s.Day <= day) actual -= s.Bps;
            return actual;
        }

        /// <summary>지금까지 공개된 이번 턴 뉴스(오래된 것부터).</summary>
        public MarketNews[] GetNews()
        {
            if (!StockEnabled || !shocks.TryGetValue(turn, out var list)) return Array.Empty<MarketNews>();
            var news = new List<MarketNews>();
            foreach (var s in list)
            {
                int rumorDay = Math.Max(1, s.Day - NewsRules.RumorLeadDays);
                if (ProcessedDay >= rumorDay)
                    news.Add(new MarketNews { day = rumorDay, eventDay = s.Day, companyId = s.Company, kind = NewsRules.Rumor,
                        direction = s.RumorDirection, accuracyPercent = NewsRules.RumorAccuracyPercent,
                        text = "[루머] " + s.Company + (s.RumorDirection > 0 ? " 대형 호재" : " 대형 악재") + " 소문 · " + s.Day + "일 발표 예정 (적중률 " + NewsRules.RumorAccuracyPercent + "%)" });
                if (ProcessedDay >= s.Day)
                    news.Add(new MarketNews { day = s.Day, eventDay = s.Day, companyId = s.Company, kind = NewsRules.Breaking,
                        direction = Math.Sign(s.Bps), moveBps = s.Bps,
                        text = "[속보] " + s.Company + (s.Bps > 0 ? " 깜짝 호재! 천장 돌파 " : " 충격 악재! 바닥 붕괴 ") + (s.Bps > 0 ? "+" : "") + (s.Bps / 100) + "%p" });
            }
            return news.OrderBy(n => n.day).ThenBy(n => n.kind == NewsRules.Breaking ? 1 : 0).ToArray();
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
                    title = "속보 올라타기", description = "속보가 터지는 날, 그 기업을 보유하고 있기(호재든 악재든) · " + reward };
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
                case "NEWS_RIDE": return shocks.TryGetValue(turn, out var list) && list.Any(s => q[s.Company][s.Day] > 0);
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
