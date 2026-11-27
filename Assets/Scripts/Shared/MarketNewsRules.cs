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

    public static class NewsRules
    {
        public const string Rumor = "RUMOR", Breaking = "BREAKING";
        /// <summary>한 턴에 일어나는 속보 수.</summary>
        public const int ShocksPerTurn = 2;
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

    }

    /// <summary>뉴스·미션을 제공하는 시장(B). GameRoom(A)이 PublicState에 실어 보낸다.</summary>
    public interface IMarketNews
    {
        MarketNews[] GetNews();
        TurnMission GetMission();
    }
}
