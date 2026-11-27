using System;
using System.Linq;

namespace BlackWhale.Shared
{
    /// <summary>
    /// [실험] 자산 교환(견제) 턴.
    /// 교환 턴에는 각 플레이어가 처음 자리 순서(initialPlayerOrder)에서 바로 다음 사람의 계좌를 대신 조종한다.
    /// (A→B, B→C, C→D, D→A) 조종하는 사람의 목표는 그 계좌를 최대한 떨어뜨리는 것.
    ///
    /// 점수 규칙
    /// - 계좌의 손익은 원래 주인에게 그대로 들어간다. (B 계좌가 떨어지면 B의 자산이 준다)
    /// - 조종한 계좌를 가장 많이 떨어뜨린 1명(이번 턴 수익률 기준)만 '견제 보너스'로
    ///   그 계좌 하락률의 BonusPercent% 를 자기 수익률로 받는다(내 턴 시작 자산 × 하락률 × 50%). 동률이면 동률인 사람 모두 받는다.
    ///   계좌가 오른 사람은 1등이 될 수 없다. 나머지는 보너스 0.
    /// 그래서 남의 계좌를 많이 떨어뜨릴수록 (1) 그 사람을 끌어내리고 (2) 1등이면 내 자산도 오른다.
    ///
    /// 조정하려면 아래 두 값만 바꾸면 된다.
    /// </summary>
    public static class SwapRules
    {
        /// <summary>교환 턴 번호(기본값). 비우면 교환 모드가 꺼진다. 예: { 5 } 또는 { 3, 5 }</summary>
        public static readonly int[] DefaultTurns = { 5 };
        /// <summary>실제로 쓰는 교환 턴. 평소에는 DefaultTurns와 같다.</summary>
        public static int[] Turns = (int[])DefaultTurns.Clone();
        /// <summary>게임 시작 턴. 평소 1. 녹화용 메뉴 'BlackWhale/교환 턴 바로 보기'를 켜면 개발용 씬이 교환 턴(5)으로 바꾼다.
        /// 앞 턴들은 서버가 자동으로 넘긴다(증강은 첫 후보 자동 선택, 매매 없음 = 전부 BANK).</summary>
        public static int StartTurn = 1;
        /// <summary>녹화용 메뉴가 켜고 끄는 PlayerPrefs 키(1 = 첫 턴부터 교환).</summary>
        public const string PreviewPrefKey = "BlackWhale.SwapPreview";
        public static void UsePreview(bool on) { Turns = (int[])DefaultTurns.Clone(); StartTurn = on && DefaultTurns.Length > 0 ? DefaultTurns.Min() : 1; }
        /// <summary>견제왕이 받는 보너스 수익률 = 조종한 계좌 하락률의 이 비율(%).</summary>
        public const int BonusPercent = 50;

        public static bool IsSwapTurn(int turn) => Turns.Contains(turn);

        /// <summary>seatOrder에서 player 다음 자리 사람의 id. (마지막 사람은 첫 사람)</summary>
        public static string AccountFor(string[] seatOrder, string player)
        {
            if (seatOrder == null || seatOrder.Length < 2) return player;
            int i = Array.IndexOf(seatOrder, player);
            return i < 0 ? player : seatOrder[(i + 1) % seatOrder.Length];
        }

        /// <summary>이 화면에서 지금 조종 중인 계좌 id. 교환 턴이 아니면 자기 자신.</summary>
        public static string ActingAccount(PublicState state, string localPlayer)
        {
            if (state?.swapTurn != true || state.swapControllerIds == null || state.swapAccountIds == null) return localPlayer;
            int i = Array.IndexOf(state.swapControllerIds, localPlayer);
            return i < 0 || i >= state.swapAccountIds.Length ? localPlayer : state.swapAccountIds[i];
        }

        /// <summary>내 계좌를 조종하는 사람 id. 교환 턴이 아니면 null.</summary>
        public static string ControllerOf(PublicState state, string accountId)
        {
            if (state?.swapTurn != true || state.swapControllerIds == null || state.swapAccountIds == null) return null;
            int i = Array.IndexOf(state.swapAccountIds, accountId);
            return i < 0 || i >= state.swapControllerIds.Length ? null : state.swapControllerIds[i];
        }

        /// <summary>견제왕 보너스 금액: 내 턴 시작 자산 × (조종한 계좌 하락률 × BonusPercent%).</summary>
        public static long Bonus(long ownStartingAssets, int accountReturnBps)
            => accountReturnBps >= 0 || ownStartingAssets <= 0 ? 0 : ownStartingAssets * (-(long)accountReturnBps) * BonusPercent / 100 / 10000;
        /// <summary>교환 턴 시작 안내 카드가 떠 있는 시간(초). 그만큼 정보 탐색 시간을 더 준다.</summary>
        public const double IntroSeconds = 7;
        /// <summary>교환 턴 정산(견제왕 발표) 단계 길이(초).</summary>
        public const double ResultSeconds = 16;
    }
}

namespace BlackWhale.Shared
{
    /// <summary>[실험] 교환 턴을 지원하는 시장(B). GameRoom(A)이 턴 시작 때 자리 순서를 넘긴다.</summary>
    public interface ISwapMarket
    {
        bool SwapActive { get; }
        void ConfigureSwap(string[] seatOrder);
        /// <summary>controller가 이번 턴에 조종하는 계좌. 교환 턴이 아니면 자기 자신.</summary>
        string SwapAccount(string controller);
        string[] SwapControllers { get; }
    }
}
