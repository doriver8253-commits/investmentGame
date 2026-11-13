using System;
using System.Linq;

namespace BlackWhale.Shared
{
    public static class StockRules
    {
        public const int Turns = 6, Supply = 20, Slots = 30, PersonalLimit = 10;
        public const int BankSupply = 120, DraftSeconds = 7, DraftRounds = 2, SharesPerPick = 15, InvestmentSeconds = 23;
        public const string AugmentPhase = "AUGMENT_SELECTION";
        public const string Trade = "DRAFT_SHARES", Pass = "PASS_DRAFT", Choose = "CHOOSE_AUGMENT", Priority = "USE_DRAFT_PRIORITY";
        public static readonly string[] Augments = { "PRIORITY", "FLEXIBLE", "OPPORTUNITY", "ANALYST", "TREND", "BIO_RESEARCH", "SPACE_PIONEER", "CASH_INTEREST", "DIVERSIFY", "LONG_TERM", "CONTRARIAN", "CONCENTRATION" };
        public static string Name(string id)
        {
            string[] names = { "선점 투자자", "유연한 투자자", "기회 포착", "내부 분석가", "추세 추종", "바이오 연구원", "우주 개척자", "복리의 마법", "위험 분산", "장기 투자의 힘", "하락장 사냥꾼", "집중 투자자" };
            int i = Array.IndexOf(Augments, id); return i < 0 ? id ?? "" : names[i];
        }
        public static string Description(string id)
        {
            switch (id)
            {
                case "PRIORITY": return "게임당 1회, 아직 시작하지 않은 내 차례를 한 자리 앞당깁니다.";
                case "FLEXIBLE": return "여러 기업 동시 주문은 기본 규칙으로 통합되었습니다. 이 증강은 새 후보로 등장하지 않습니다.";
                case "OPPORTUNITY": return "유효 매수가 잔여 물량 부족으로 실패하면 턴당 1회, 다음 정보 구매에 쓸 2일 할인권을 받습니다.";
                case "ANALYST": return "선택 즉시 새 정보 2일 할인을 받고, 이후 매 턴 시작에도 2일 할인을 받습니다.";
                case "TREND": return "직전 턴 마지막 3일 연속 상승 기업의 이번 턴 1~3일 정보를 추가 확인합니다.";
                case "BIO_RESEARCH": return "BIO 연구 이벤트의 성공 확률을 확인합니다. 최종 수익은 보장되지 않습니다.";
                case "SPACE_PIONEER": return "매 턴 SPACE를 다시 매수해 하루 이상 보유한 턴이 2턴 이상 이어지면 해당 배분금에 +2% 보너스를 받습니다. 예약 청산으로 일찍 팔아도 이어집니다.";
                case "CASH_INTEREST": return "현금 배분금에 턴당 +2% 보너스, 전체 자산 기준 최대 +1%를 받습니다.";
                case "DIVERSIFY": return "하루라도 3개 이상 기업을 각각 3주 이상 동시에 보유하면 각 기업 손실의 20%를 완화합니다.";
                case "LONG_TERM": return "매 턴 같은 기업을 다시 매수해 하루 이상 보유한 턴이 3턴 이상 이어지면 해당 배분금에 +2% 보너스를 받습니다. 예약 청산으로 일찍 팔아도 이어집니다.";
                case "CONTRARIAN": return "직전 턴 하락 기업의 이번 턴 1~3일 정보를 추가 확인합니다.";
                case "CONCENTRATION": return "선택한 기업 한 곳의 개인 보유 한도를 12주로 높입니다. 총 20주·30칸은 유지합니다.";
                default: return "";
            }
        }
    }
    [Serializable] public sealed class ShareOrder { public string companyId; public int targetShares; }
    [Serializable] public sealed class ShareOwner { public string playerId; public int shares, reserved; }
    [Serializable] public sealed class CompanyShareState
    {
        public string companyId;
        public int totalShares = StockRules.Supply;
        public ShareOwner[] owners;
    }
    [Serializable] public sealed class PortfolioHolding { public string companyId; public int shares, consecutiveTurns; }
    [Serializable] public sealed class StockInsight { public string companyId, message; public int probabilityBps; }
    [Serializable] public sealed class PortfolioState
    {
        public int capacity = StockRules.Slots, usedSlots, informationDiscount;
        public int draftSharesRemaining = StockRules.SharesPerPick;
        public PortfolioHolding[] holdings;
        public string[] augments, candidates;
        public bool augmentChosen, priorityUsed;
        public string concentrationCompany;
        public StockInsight[] insights;
        public int CashSlots => capacity - usedSlots;
        public int LimitFor(string company) => concentrationCompany == company ? 12 : StockRules.PersonalLimit;
    }
    public interface IStockMarketSystem
    {
        bool StockEnabled { get; }
        CompanyShareState[] GetShareMarket();
        PortfolioState GetPortfolio(string playerId);
        void BeginDraftPick(string playerId);
        RuleResult TradeShares(RequestContext context, ShareOrder[] orders);
        void BeginAugmentSelection();
        RuleResult ChooseAugment(RequestContext context, string id, string company);
        bool AllAugmentsChosen { get; }
        void CompleteAugmentSelection();
        RuleResult ConsumePriority(string playerId);
        int ProcessedDay { get; }
        void SimulateDay();
        void LockPortfolios();
        int InformationDiscount(string playerId);
        void ConsumeInformationDiscount(string playerId, int days);
        VisibleInformationWindow[] AnalysisWindows(string playerId);
    }
}
