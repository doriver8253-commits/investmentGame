using System;
using System.Linq;
using BlackWhale.Shared;
using BlackWhale.UI.Presentation;
using BlackWhale.UI.State;
using UnityEngine;
using UnityEngine.UI;

namespace BlackWhale.UI.View
{
    // The chart supplies the range. Buttons are the only purchase/reservation actions.
    public sealed class InformationWindowSelector
    {
        private readonly GameClientController controller;
        private readonly Func<string> companyId;
        private readonly Text status;
        private readonly Button confirm, reserve;
        public RectTransform Root { get; }
        public ChartRangeSelection Selection { get; set; } = new ChartRangeSelection();
        public int StartDay => Selection.Start;
        public int EndDay => Selection.End;
        public Action<int> Changed;
        public Action Reserve;
        public Func<bool> CanReserve;
        /// <summary>고른 구간이 이미 정한 예약일 때 취소할 수 있는지.</summary>
        public Func<bool> CanCancel;
        /// <summary>고른 예약 구간 취소.</summary>
        public Action CancelReservation;
        public InformationWindowSelector(RectTransform parent, GameClientController controller, Func<string> companyId)
        {
            this.controller = controller; this.companyId = companyId;
            Root = UiKit.Rect(parent, "InformationWindowSelector"); UiKit.Stretch(Root);
            status = UiKit.Label(Root, "", 10, Palette.SubText, TextAnchor.UpperLeft);
            status.name = "InformationRangeCost"; UiKit.Stretch(status.rectTransform, 0, 0, 0, 26);
            confirm = UiKit.Button(Root, "정보 사기", 11, Palette.Accent, Color.white, () => {
                if (Selection.HasSelection && EndDay >= Math.Max(1, StartDay)) controller.SelectInformationWindow(companyId(), Math.Max(1, StartDay), EndDay);
            });
            confirm.name = "ConfirmInformationWindow";
            var rt = (RectTransform)confirm.transform; rt.anchorMin = Vector2.zero; rt.anchorMax = new Vector2(.5f, 0);
            rt.offsetMin = Vector2.zero; rt.offsetMax = new Vector2(-3, 24);
            reserve = UiKit.Button(Root, "예약하기", 11, Palette.AccentSoft, Palette.Accent, () =>
            {
                if (Selection.SelectedIndex >= 0) CancelReservation?.Invoke(); else Reserve?.Invoke();
            });
            reserve.name = "ReserveRange";
            rt = (RectTransform)reserve.transform; rt.anchorMin = new Vector2(.5f, 0); rt.anchorMax = new Vector2(1, 0);
            rt.offsetMin = new Vector2(3, 0); rt.offsetMax = new Vector2(0, 24);
        }
        public void SetStart(int start) { Selection.SetRange(start, Math.Min(30, start + 4)); Changed?.Invoke(start); Refresh(); }
        public void NotifyChanged() { Changed?.Invoke(StartDay); Refresh(); }
        public void Refresh()
        {
            var state = controller.Store.Public;
            Root.gameObject.SetActive(state?.windowInformation == true && (state.phase == Protocol.Info || state.phase == Protocol.Selection || state.stockSystem && state.phase == Protocol.Investment));
            if (state?.windowInformation != true) return;
            bool planning = state.stockSystem && state.phase == Protocol.Investment;
            confirm.gameObject.SetActive(!planning);
            reserve.gameObject.SetActive(true);
            ((RectTransform)reserve.transform).anchorMin = new Vector2(planning ? 0 : .5f, 0);
            ((RectTransform)confirm.transform).anchorMax = new Vector2(.5f, 0);
            // 정보 탐색 단계에서는 공용·개인 정보 모두 5일 고정이다.
            // 투자 계획 단계로 넘어가면 다시 자유롭게 진입·청산 구간을 편집한다.
            Selection.FixedPublicWindow = state.phase == Protocol.Info;
            string id = companyId();
            var company = controller.Store.View?.companies?.FirstOrDefault(c => c.companyId == id);
            int start = Math.Max(1, StartDay), count = Selection.HasSelection ? InformationWindowRules.NewDays(company, start, EndDay) : 0;
            bool choosing = state.publicSelectionActive, mine = choosing && state.publicSelectionPlayerId == controller.LocalPlayerId;
            int free = Math.Min(count, (controller.Store.View?.ability?.freeInformationCredits ?? 0) + (controller.Store.View?.portfolio?.informationDiscount ?? 0)), cost = count - free;
            bool overlap = choosing && InformationWindowRules.OverlapsPublic(state, id, StartDay);
            bool tooClose = choosing
                ? InformationWindowRules.TooCloseToPublic(state, id, StartDay)
                : InformationWindowRules.TooCloseToKnown(company, StartDay);
            bool pending = controller.Store.HasPending(choosing ? CommandTypes.RevealInformationWindow : CommandTypes.BuyInformationWindow);
            bool active = !state.paused && string.IsNullOrEmpty(controller.Store.EndReason);
            bool validPublic = StartDay >= 1 && EndDay - StartDay == 4 && !overlap && !tooClose;
            bool validFiveDayWindow = Selection.HasSelection && EndDay - StartDay == 4;
            bool can = Selection.HasSelection && state.phase == Protocol.Info && active && !pending &&
                validFiveDayWindow && !tooClose && (choosing ? mine && validPublic : count > 0 && cost <= (controller.Store.View?.coins ?? 0));
            status.text = !Selection.HasSelection ? "차트에서 클릭 또는 드래그로 구간 선택" : StartDay + "~" + EndDay + "일 · 새 " + count + "일 · "
                + (tooClose ? "같은 기업 정보와 공백 필요" : choosing ? overlap ? "공개 겹침" : mine ? "무료 공개" : "차례 대기" : cost + "코인" + (free > 0 ? " / 무료 " + free + "일" : ""));
            if (planning && Selection.HasSelection) status.text = StartDay + "일 시가 → " + EndDay + "일 종가";
            UiKit.SetButton(confirm, pending ? "요청 중…" : choosing ? "공용 공개" : "정보 사기", can,
                can ? Palette.Accent : Palette.Disabled, can ? Color.white : Palette.DisabledText);
            // 이미 정한 구간(예약)을 고른 상태면 '예약 취소' = 그 매수·청산 구간을 지운다
            bool existing = Selection.SelectedIndex >= 0;
            bool reserveCan = active && !choosing && Selection.HasSelection && (existing ? CanCancel?.Invoke() == true : CanReserve?.Invoke() == true);
            UiKit.SetButton(reserve, existing ? "예약 취소" : "예약하기", reserveCan,
                reserveCan ? (existing ? Palette.Card : Palette.AccentSoft) : Palette.Disabled, reserveCan ? (existing ? Palette.Up : Palette.Accent) : Palette.DisabledText);
        }
    }
}
