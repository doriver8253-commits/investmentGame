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
                if (controller.Store.Public?.stockSystem == true) controller.BuyRandomInformation(companyId());
                else if (Selection.HasSelection && EndDay >= Math.Max(1, StartDay)) controller.SelectInformationWindow(companyId(), Math.Max(1, StartDay), EndDay);
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
        /// <summary>값이 실제로 바뀔 때만 버튼을 건드린다(같은 값을 다시 넣어 상태 전환 효과가 다시 도는 것 방지).</summary>
        private static void SetButtonStable(Button button, string label, bool interactable, Color background, Color textColor)
        {
            var text = button.GetComponentInChildren<Text>();
            var image = (Image)button.targetGraphic;
            if (button.interactable == interactable && image.color == background && text != null && text.text == label && text.color == textColor) return;
            UiKit.SetButton(button, label, interactable, background, textColor);
        }
        public void SetStart(int start) { Selection.SetRange(start, Math.Min(30, start + 4)); Changed?.Invoke(start); Refresh(); }
        public void NotifyChanged() { Changed?.Invoke(StartDay); Refresh(); }
        public void Refresh()
        {
            var state = controller.Store.Public;
            Root.gameObject.SetActive(state?.windowInformation == true && (state.phase == Protocol.Info || !state.stockSystem && state.phase == Protocol.Selection));
            if (state?.windowInformation != true) return;
            bool planning = state.stockSystem && state.phase == Protocol.Investment;
            confirm.gameObject.SetActive(!planning);
            reserve.gameObject.SetActive(!state.stockSystem || !state.publicSelectionActive);
            ((RectTransform)reserve.transform).anchorMin = new Vector2(planning ? 0 : .5f, 0);
            ((RectTransform)confirm.transform).anchorMax = new Vector2(state.stockSystem && state.publicSelectionActive ? 1 : .5f, 0);
            Selection.FixedPublicWindow = state.publicSelectionActive;
            string id = companyId();
            var company = controller.Store.View?.companies?.FirstOrDefault(c => c.companyId == id);
            if (state.stockSystem)
            {
                var options = state.lobbySettings ?? new LobbySettings();
                var known = InformationWindowRules.KnownDays(company);
                int first = Math.Max(1, options.informationStartDay);
                int remaining = Enumerable.Range(first, Math.Max(0, options.informationEndDay - first + 1)).Count(day => !known[day].HasValue);
                bool activeRandom = !state.paused && string.IsNullOrEmpty(controller.Store.EndReason);
                // 요청 중(pending)이라고 버튼을 회색 '요청 중…'으로 바꿨다가 응답이 오면 되돌리면, 서버 응답이 빨라
                // 누를 때마다 파랑→회색→파랑으로 깜빡인다. 겉모습은 그대로 두고, 중복 요청은 BuyRandomInformation이 막는다.
                int price = InformationWindowRules.NextRandomDayCost(company);
                bool canRandom = id != "BANK" && state.phase == Protocol.Info && activeRandom && remaining > 0 && (controller.Store.View?.coins ?? 0) >= price;
                int interest = InformationWindowRules.Interest(state, id);
                status.text = id == "BANK" ? "BANK 0~30일 전체 공개" : $"{first}~{options.informationEndDay}일 중 미공개 {remaining}일 · 무작위 · 이번 턴 전체 구매 {interest}일";
                SetButtonStable(confirm, id == "BANK" ? "전체 공개" : "랜덤 1일 · " + price + "코인", canRandom,
                    canRandom ? Palette.Accent : Palette.Disabled, canRandom ? Color.white : Palette.DisabledText);
                bool existingRandom = Selection.SelectedIndex >= 0;
                bool canReserveRandom = activeRandom && Selection.HasSelection && (existingRandom ? CanCancel?.Invoke() == true : CanReserve?.Invoke() == true);
                UiKit.SetButton(reserve, existingRandom ? "예약 취소" : "예약하기", canReserveRandom,
                    canReserveRandom ? Palette.AccentSoft : Palette.Disabled, canReserveRandom ? Palette.Accent : Palette.DisabledText);
                return;
            }
            int start = Math.Max(1, StartDay), count = Selection.HasSelection ? InformationWindowRules.NewDays(company, start, EndDay) : 0;
            bool choosing = state.publicSelectionActive, mine = choosing && state.publicSelectionPlayerId == controller.LocalPlayerId;
            int free = Math.Min(count, (controller.Store.View?.ability?.freeInformationCredits ?? 0) + (controller.Store.View?.portfolio?.informationDiscount ?? 0)), cost = count - free;
            bool overlap = choosing && InformationWindowRules.OverlapsPublic(state, id, StartDay);
            bool pending = controller.Store.HasPending(choosing ? CommandTypes.RevealInformationWindow : CommandTypes.BuyInformationWindow);
            bool active = !state.paused && string.IsNullOrEmpty(controller.Store.EndReason);
            bool validPublic = StartDay >= 1 && EndDay - StartDay == 4 && !overlap;
            bool can = Selection.HasSelection && state.phase == Protocol.Info && active && !pending &&
                (choosing ? mine && validPublic : count > 0 && cost <= (controller.Store.View?.coins ?? 0));
            status.text = !Selection.HasSelection ? "차트에서 클릭 또는 드래그로 구간 선택" : StartDay + "~" + EndDay + "일 · 새 " + count + "일 · "
                + (choosing ? overlap ? "공개 겹침" : mine ? "무료 공개" : "차례 대기" : cost + "코인" + (free > 0 ? " / 무료 " + free + "일" : ""));
            if (choosing && state.stockSystem) {
                var options = state.lobbySettings ?? new LobbySettings();
                bool available = InformationWindowRules.CanRevealPublic(state, id);
                can = mine && active && !pending && available;
                status.text = !available ? "공개할 날짜 부족 · 다른 기업을 선택하세요" : mine ? "미공개 " + options.publicRevealDays
                    + "일 · " + (options.scatteredPublicDays ? "1~30일 중 무작위 추첨" : "연속 구간 추첨") : state.publicSelectionPlayerId + " 선택 중";
            }
            if (planning && Selection.HasSelection) status.text = StartDay + "일 시가 → " + EndDay + "일 종가";
            UiKit.SetButton(confirm, pending ? "요청 중…" : choosing ? state.stockSystem ? "이 기업 선택" : "공용 공개" : "정보 사기", can,
                can ? Palette.Accent : Palette.Disabled, can ? Color.white : Palette.DisabledText);
            // 이미 정한 구간(예약)을 고른 상태면 '예약 취소' = 그 매수·청산 구간을 지운다
            bool existing = Selection.SelectedIndex >= 0;
            bool reserveCan = active && !choosing && Selection.HasSelection && (existing ? CanCancel?.Invoke() == true : CanReserve?.Invoke() == true);
            UiKit.SetButton(reserve, existing ? "예약 취소" : "예약하기", reserveCan,
                reserveCan ? (existing ? Palette.Card : Palette.AccentSoft) : Palette.Disabled, reserveCan ? (existing ? Palette.Up : Palette.Accent) : Palette.DisabledText);
        }
    }
}
