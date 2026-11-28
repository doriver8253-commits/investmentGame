using System;

namespace BlackWhale.Shared
{
    /// <summary>
    /// 실시간 매매(30일) 중에 나오는 뉴스. 서버가 정해진 날짜가 되면 PublicState.news 로 모두에게 공개한다.
    /// - RUMOR(루머): 속보 NewsRules.RumorLeadDays 일 전에 뜬다. 방향(호재/악재)은 RumorAccuracyPercent% 확률로만 맞다.
    /// - BREAKING(속보): 그날 주가가 한 번에 크게 뛰거나 떨어진다. 그 뒤 날짜 전체가 같은 만큼 옮겨 간다(바닥·천장 돌파).
    /// 미리 산 정보(그래프 점)는 '지금까지 난 뉴스'만 반영한 값이라, 속보가 나면 앞으로의 점들도 같이 움직인다.
    /// </summary>
    [Serializable] public sealed class MarketNews
    {
        public int day;               // 공개되는 날(이 날 종가가 처리되면 보인다)
        public int eventDay;          // 속보가 실제로 일어나는 날(루머·속보 공통)
        public string companyId, kind, text;
        public int direction;         // 루머: 귀띔 방향(+1 호재 / -1 악재), 속보: 실제 방향
        public int moveBps;           // 속보만: 그날 추가로 움직인 %p(bps)
        public int accuracyPercent;   // 루머만: 방향 적중률
    }

    /// <summary>턴마다 하나씩 주어지는 공개 미션. 성공하면 정산 때 턴 시작 자산의 rewardBps 만큼 보너스.</summary>
    [Serializable] public sealed class TurnMission
    {
        public string id, companyId, title, description;
        public int rewardBps;
    }

    /// <summary>실시간 매매 중 기업 분위기(화면 표시용): AI 붕괴 확률, BIO 급등 확률, FOOD 변동폭.</summary>
    [Serializable] public sealed class CompanyMood { public string companyId, text; public int percent; }

    public static class NewsRules
    {
        public const string Rumor = "RUMOR", Breaking = "BREAKING", Event = "EVENT", Warning = "WARNING";
        /// <summary>한 턴에 일어나는 속보 수(패턴 기업 중).</summary>
        public const int ShocksPerTurn = 2;
        public static readonly string[] ShockCompanies = { "FOOD", "AUTO", "SPACE" };
        /// <summary>속보가 일어날 수 있는 날(포함). hidden 전조(22일~)보다 앞이다.</summary>
        public const int FirstShockDay = 6, LastShockDay = 20;
        /// <summary>루머가 속보보다 며칠 먼저 뜨는지. 실시간 하루 2초 → 약 6초 대응할 시간.</summary>
        public const int RumorLeadDays = 3;
        /// <summary>루머 방향이 맞을 확률(%). 화면에도 그대로 보여 준다.</summary>
        public const int RumorAccuracyPercent = 70;
        /// <summary>속보 크기 = 기업 하루 변동 한도 × (Min~Max)/10.</summary>
        public const int ShockMinTenths = 15, ShockMaxTenths = 25;
        /// <summary>미션 성공 보너스: 턴 시작 자산의 3%.</summary>
        public const int MissionRewardBps = 300;

        // 실시간 시장(시장 반응)
        /// <summary>AI·BIO는 미리 정한 패턴을 이 비율(%)만 쓰고 나머지는 시장 반응으로 정한다.</summary>
        public const int LiveOnlyPatternPercent = 40;
        /// <summary>AI 붕괴 확률 = 기본 + 연속 상승일마다 + (3명 이상 보유 시) 몰림 + (합계 20주 이상이면 몰림의 절반)</summary>
        public const int AiBaseCrashPercent = 3, AiStreakCrashPercent = 4, AiCrowdCrashPercent = 12;
        /// <summary>AI가 무너지지 않는 날의 기본 상승 쪽 힘(하루 한도의 %).</summary>
        public const int AiDriftPercent = 25;
        /// <summary>BIO 급등 확률 = 기본 + 누적 보유량(주·일) ÷ 이 값</summary>
        public const int BioBaseSpikePercent = 2, BioPressurePerPercent = 10;
        /// <summary>BIO 급등 크기(하루 한도 × 값/10)와, 기다리는 동안 매일 새는 정도(하루 한도의 %).</summary>
        public const int BioSpikeMinTenths = 15, BioSpikeMaxTenths = 30, BioBleedPercent = 15;
        /// <summary>FOOD: 보유한 사람 1명당 패턴 등락이 이만큼(%) 커진다.</summary>
        public const int FoodAmplifyPercentPerHolder = 30;
        /// <summary>이 확률(%)을 넘으면 경고 뉴스를 한 번 띄운다.</summary>
        public const int WarnPercent = 25;
        /// <summary>주가 바닥(0일 대비 -90%).</summary>
        public const int LowestBps = -9000;

        /// <summary>실시간 시장의 기업 성격(카드 안내 줄).</summary>
        public static string Personality(string companyId)
        {
            switch (companyId)
            {
                case "AUTO": return "패턴대로 움직임 · 정보가 중요";
                case "FOOD": return "보유한 사람이 많을수록 출렁임";
                case "AI": return "오르는 추세 · 몰리면 붕괴";
                case "BIO": return "들고 버티면 언젠가 급등";
                case "SPACE": return "며칠씩 이어지는 추세";
                case "BANK": return "안전 · 거의 안 움직임";
                default: return "";
            }
        }
    }

    /// <summary>뉴스·미션을 제공하는 시장(B). GameRoom(A)이 PublicState에 실어 보낸다.</summary>
    public interface IMarketNews
    {
        MarketNews[] GetNews();
        TurnMission GetMission();
        CompanyMood[] GetMoods();
        /// <summary>실시간 시장(군중 반응·뉴스·미션)이 켜져 있는지.</summary>
        bool LiveMarket { get; }
    }
}
