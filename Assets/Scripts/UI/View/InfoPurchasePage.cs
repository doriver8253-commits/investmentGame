// 정보 구매 창: 기업 카드 6개(위 3개·아래 3개, REQ-005).
// 카드마다 일별 그래프와 이동형 5일 창. 고정 묶음 버튼은 v2 fixture 비교용이다.
// 코인·열람 상태는 서버 응답으로만 바뀐다. 버튼은 요청 의도만 보낸다.
using System.Collections.Generic;
using System;
using System.Linq;
using BlackWhale.Shared;
using BlackWhale.UI.Presentation;
using BlackWhale.UI.State;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;

namespace BlackWhale.UI.View
{
    public static class CompanyInfo
    {
        public static string RevealedSummary(RevealedCompany company)
            => string.Join("   ", (company?.investments ?? Array.Empty<RevealedInvestment>()).Select(p =>
                "<color=#" + ColorUtility.ToHtmlStringRGB(Palette.Player(p.playerId)) + ">■ " + p.playerId + " " + DisplayFormat.Percent(p.returnBps) + "</color>"));

        public static string Volatility(string companyId)
        {
            switch (companyId)
            {
                case "AI":
                case "SPACE": return "변동성 높음";
                case "FOOD":
                case "BANK": return "변동성 낮음";
                case "BIO": return "변동성 극단적";
                case "AUTO": return "변동성 중간";
                default: return "";
            }
        }

        public static string BundleLabel(int bundleIndex)
            => GraphSeriesBuilder.BundleStartDay(bundleIndex) + "~" + GraphSeriesBuilder.BundleEndDay(bundleIndex) + "일";

        public static void RefreshPurchaseButton(Button button, GameClientController controller, string companyId, int bundleIndex, bool detailed = false)
        {
            var store = controller.Store;
            var company = store.View?.companies?.FirstOrDefault(c => c.companyId == companyId);
            string label = BundleLabel(bundleIndex);
            if (GraphSeriesBuilder.TryGetSource(company, bundleIndex, out var source))
            {
                bool purchased = source == BundleSources.Purchased;
                UiKit.SetButton(button, label + (detailed ? (purchased ? " · 구매함" : " · 열람 중") : ""), false,
                    purchased ? Palette.AccentSoft : Palette.Disabled, purchased ? Palette.Accent : Palette.SubText);
            }
            else if (store.IsPurchasePending(companyId, bundleIndex))
                UiKit.SetButton(button, label + (detailed ? " · 요청 중" : ""), false, Palette.AccentSoft, Palette.Accent);
            else
            {
                bool free = store.View?.ability?.freeInformationCredits > 0;
                bool can = store.Public?.phase == Protocol.Info && !store.Public.paused && string.IsNullOrEmpty(store.EndReason)
                    && ((store.View?.coins ?? 0) > 0 || free);
                UiKit.SetButton(button, label + (detailed ? (free ? " · 무료" : " · 1코인") : ""), can,
                    can ? Palette.Accent : Palette.Disabled, can ? Color.white : Palette.DisabledText);
            }
        }
    }

    public sealed class InfoPurchasePage
    {
        private readonly GameClientController _c;
        private readonly Text _header;
        private readonly Text _title, _publicHistory;
        private readonly List<CompanyCard> _cards = new List<CompanyCard>();
        private readonly RectTransform _grid, _zoom, _planRows;
        private readonly BarChartGraphic _zoomChart;
        private readonly ChartPointerLayer _zoomPointer;
        private readonly Text _zoomTitle, _zoomTip, _zoomMessage;
        private readonly Button _favorite, _cancelEntry;
        private readonly Button[] _rankButtons = new Button[7];
        private readonly Button[] _zoomBundleButtons = new Button[UiRules.BundleCount];
        private readonly Text _zoomPurchaseStatus;
        private string _zoomCompany, _zoomPhase, _rowKey, _note = "";
        private int _zoomTurn;
        private int? _entryDay, _hoverDay;
        private DayBar[] _zoomBars = new DayBar[0];
        private readonly InformationWindowSelector _zoomWindow;
        private readonly RectTransform _zoomLegacyPurchases;
        private readonly Button _interactionMode, _blackbox;
        private readonly RectTransform _informationItems;
        private string _itemsKey;
        private bool _selectInformation;
        private bool _showInformationItems;

        public InfoPurchasePage(RectTransform page, GameClientController controller, RectTransform overlayParent = null)
        {
            _c = controller;

            var title = _title = UiKit.Label(page, "정보 구매 창", 18, Palette.Text, TextAnchor.MiddleLeft, FontStyle.Bold);
            UiKit.Place(title.rectTransform, new Vector2(0, 1), new Vector2(0, 1), new Vector2(6, -2), new Vector2(300, 28));

            _header = UiKit.Label(page, "", 14, Palette.SubText, TextAnchor.MiddleRight);
            UiKit.Place(_header.rectTransform, new Vector2(1, 1), new Vector2(1, 1), new Vector2(-6, -2), new Vector2(600, 28));
            _publicHistory = UiKit.Label(page, "", 11, Palette.SubText, TextAnchor.MiddleLeft);
            _publicHistory.name = "PublicInformationHistory";
            _publicHistory.rectTransform.anchorMin = new Vector2(0, 1); _publicHistory.rectTransform.anchorMax = Vector2.one;
            _publicHistory.rectTransform.offsetMin = new Vector2(6, -72); _publicHistory.rectTransform.offsetMax = new Vector2(-6, -32);

            var grid = _grid = UiKit.Rect(page, "CompanyGrid");
            UiKit.Stretch(grid, 0, 34, 0, 0);

            var ids = UiRules.CompanyIds;
            for (int i = 0; i < ids.Length; i++)
            {
                int col = i % 3;
                int row = i / 3; // 0 = 위, 1 = 아래
                var cell = UiKit.Rect(grid, "Card_" + ids[i]);
                UiKit.Anchor(cell, col / 3f, row == 0 ? 0.5f : 0f, (col + 1) / 3f, row == 0 ? 1f : 0.5f, 5, 5);
                _cards.Add(new CompanyCard(cell, ids[i], controller, Enlarge));
            }

            _zoom = UiKit.Panel(overlayParent ?? page, "ZoomCompanyPanel", Palette.Screen, rounded: true, raycast: true).rectTransform;
            // Reserve the top HUD and right-side controls, including the timer above the chart.
            UiKit.Stretch(_zoom, 4, 76, 168, 4);
            _zoomTitle = UiKit.Label(_zoom, "", 22, Palette.Text, TextAnchor.MiddleLeft, FontStyle.Bold);
            _zoomTitle.rectTransform.anchorMin = new Vector2(0, 1); _zoomTitle.rectTransform.anchorMax = Vector2.one;
            _zoomTitle.rectTransform.offsetMin = new Vector2(20, -48); _zoomTitle.rectTransform.offsetMax = new Vector2(-340, -12);
            var shrink = UiKit.Button(_zoom, "축소하기", 14, Palette.Card, Palette.Text, Collapse);
            shrink.name = "ShrinkChart";
            UiKit.Place((RectTransform)shrink.transform, Vector2.one, Vector2.one, new Vector2(-16, -12), new Vector2(110, 34));
            _interactionMode = UiKit.Button(_zoom, "", 12, Palette.AccentSoft, Palette.Accent, () => {
                _showInformationItems = !_showInformationItems; RefreshZoom(); });
            _interactionMode.name = "ToggleChartMode";
            UiKit.Place((RectTransform)_interactionMode.transform, Vector2.one, Vector2.one, new Vector2(-136, -12), new Vector2(188, 34));
            _zoomTip = UiKit.Label(_zoom, "", 13, Palette.SubText, TextAnchor.MiddleLeft);
            _zoomTip.rectTransform.anchorMin = new Vector2(0, 1); _zoomTip.rectTransform.anchorMax = Vector2.one;
            _zoomTip.rectTransform.offsetMin = new Vector2(24, -80); _zoomTip.rectTransform.offsetMax = new Vector2(-20, -50);
            var chart = UiKit.Rect(_zoom, "ZoomChart"); UiKit.Stretch(chart, 24, 88, 28, 228);
            _zoomChart = chart.gameObject.AddComponent<BarChartGraphic>();
            var pointer = _zoomPointer = chart.gameObject.AddComponent<ChartPointerLayer>();
            pointer.Clicked = PlanDay;
            pointer.Hovered = day => { _hoverDay = day; RefreshZoomHover(); };
            foreach (int day in new[] { 0, 5, 10, 15, 20, 25, 30 })
            {
                var tick = UiKit.Label(chart, day + "일", 11, Palette.SubText, TextAnchor.UpperCenter);
                UiKit.Place(tick.rectTransform, new Vector2((day + .5f) / DayAxis.Count, 0), new Vector2(.5f, 1), new Vector2(0, -2), new Vector2(44, 20));
            }
            _favorite = UiKit.Button(_zoom, "찜하기", 13, Palette.Card, Palette.Text,
                () => { if (CanPlan) _c.Store.TradePlans.SetFavorite(_zoomCompany, !_c.Store.TradePlans.Get(_zoomCompany).Favorite); });
            _favorite.name = "FavoriteCompany";
            UiKit.Place((RectTransform)_favorite.transform, Vector2.zero, Vector2.zero, new Vector2(20, 172), new Vector2(120, 32));
            var rankLabel = UiKit.Label(_zoom, "우선순위", 13, Palette.SubText, TextAnchor.MiddleLeft);
            UiKit.Place(rankLabel.rectTransform, Vector2.zero, Vector2.zero, new Vector2(156, 172), new Vector2(64, 32));
            for (int i = 0; i < _rankButtons.Length; i++)
            {
                int rank = i;
                var button = UiKit.Button(_zoom, i == 0 ? "미정" : i + "순위", 12, Palette.Card, Palette.Text,
                    () => { if (CanPlan) _c.Store.TradePlans.SetRank(_zoomCompany, rank); });
                button.name = "PlanRank_" + rank;
                UiKit.Place((RectTransform)button.transform, Vector2.zero, Vector2.zero, new Vector2(224 + i * 66, 172), new Vector2(60, 32));
                _rankButtons[i] = button;
            }
            _cancelEntry = UiKit.Button(_zoom, "진입 후보 취소", 12, Palette.Card, Palette.Text,
                () => { _entryDay = null; _note = "진입 후보를 취소했습니다."; RefreshZoom(); });
            _cancelEntry.name = "CancelPlanEntry";
            UiKit.Place((RectTransform)_cancelEntry.transform, new Vector2(1, 0), new Vector2(1, 0), new Vector2(-20, 172), new Vector2(140, 32));
            var planViewport = UiKit.Rect(_zoom, "PlannedIntervalScroll");
            planViewport.anchorMin = Vector2.zero; planViewport.anchorMax = new Vector2(1, 0);
            planViewport.offsetMin = new Vector2(20, 92); planViewport.offsetMax = new Vector2(-20, 164);
            _planRows = UiKit.ScrollContent(planViewport, "PlannedIntervals");
            _zoomMessage = UiKit.Label(_zoom, "", 12, Palette.SubText, TextAnchor.MiddleLeft);
            _zoomMessage.rectTransform.anchorMin = Vector2.zero; _zoomMessage.rectTransform.anchorMax = new Vector2(1, 0);
            _zoomMessage.rectTransform.offsetMin = new Vector2(20, 54); _zoomMessage.rectTransform.offsetMax = new Vector2(-20, 86);
            _zoomPurchaseStatus = UiKit.Label(_zoom, "", 13, Palette.SubText, TextAnchor.MiddleLeft);
            _zoomPurchaseStatus.name = "ZoomPurchaseStatus";
            UiKit.Place(_zoomPurchaseStatus.rectTransform, Vector2.zero, Vector2.zero, new Vector2(20, 10), new Vector2(260, 36));
            var purchaseRow = _zoomLegacyPurchases = UiKit.Rect(_zoom, "ZoomInformationBundles");
            purchaseRow.anchorMin = Vector2.zero; purchaseRow.anchorMax = new Vector2(1, 0);
            purchaseRow.offsetMin = new Vector2(286, 10); purchaseRow.offsetMax = new Vector2(-20, 46);
            for (int i = 0; i < _zoomBundleButtons.Length; i++)
            {
                int bundle = i;
                var button = UiKit.Button(purchaseRow, "", 11, Palette.Accent, Color.white, () => _c.BuyInformation(_zoomCompany, bundle));
                button.name = "ZoomBuyBundle_" + i;
                UiKit.Anchor((RectTransform)button.transform, i / 6f, 0, (i + 1) / 6f, 1, 3, 0);
                _zoomBundleButtons[i] = button;
            }
            var windowRow = UiKit.Rect(_zoom, "ZoomInformationWindow");
            windowRow.anchorMin = Vector2.zero; windowRow.anchorMax = new Vector2(1, 0);
            windowRow.offsetMin = new Vector2(286, 8); windowRow.offsetMax = new Vector2(-20, 50);
            _zoomWindow = new InformationWindowSelector(windowRow, _c, () => _zoomCompany);
            _zoomWindow.Changed = start => RefreshMouseRange();
            _zoomWindow.CanReserve = () => CanPlan && _c.Store.TradePlans.RangeError(_zoomCompany,
                _zoomWindow.Selection.SelectedIndex, _zoomWindow.StartDay, _zoomWindow.EndDay) == "";
            _zoomWindow.Reserve = () => {
                if (!CanPlan) return;
                _note = _cards.First(c => c.CompanyId == _zoomCompany).SaveReservation();
                EventSystem.current?.SetSelectedGameObject(_zoomPointer.gameObject); RefreshZoom();
            };
            _blackbox = UiKit.Button(_zoom, "블랙박스 · 최대 5일", 11, Palette.AccentSoft, Palette.Accent,
                () => _c.UseInformationItem("BLACKBOX", _zoomCompany, _zoomWindow.StartDay));
            _blackbox.name = "UseBlackboxWindow";
            UiKit.Place((RectTransform)_blackbox.transform, new Vector2(1, 0), new Vector2(1, 0), new Vector2(-20, 172), new Vector2(160, 32));
            _informationItems = UiKit.Rect(_zoom, "InformationItems");
            _informationItems.anchorMin = Vector2.zero; _informationItems.anchorMax = new Vector2(1, 0);
            _informationItems.offsetMin = new Vector2(20, 92); _informationItems.offsetMax = new Vector2(-20, 164);
            _zoom.gameObject.SetActive(false);
        }

        private bool CanPlan => _zoomCompany != null && _c.CanPlanTrade(_zoomCompany);
        public void OpenCompany(string company) => Enlarge(company);
        private void Enlarge(string company)
        {
            _zoomCompany = company; _zoomPhase = _c.Store.Public?.phase; _zoomTurn = _c.Store.CurrentTurn;
            _entryDay = _hoverDay = null; _note = ""; _rowKey = null;
            _selectInformation = _c.Store.Public?.windowInformation == true && _c.Store.Public?.phase == Protocol.Info;
            _zoomWindow.Selection = _cards.First(c => c.CompanyId == company).Selection;
            _zoomPointer.RangeSelection = _c.Store.Public?.windowInformation == true ? _zoomWindow.Selection : null;
            _zoomPointer.CanEditRange = () => string.IsNullOrEmpty(_c.Store.EndReason) && _c.Store.Public?.paused != true &&
                (_c.Store.Public?.phase == Protocol.Info || CanPlan);
            _zoomPointer.Intervals = () => CanPlan ? _c.Store.TradePlans.Get(_zoomCompany).Intervals : Array.Empty<Interval>();
            _zoomPointer.EditInterval = (index, entry, exit) => CanPlan ? _c.Store.TradePlans.TryReplaceInterval(_zoomCompany, index, entry, exit) : ErrorCodes.OrderLocked;
            _zoomPointer.DeleteInterval = index => { if (CanPlan) _c.Store.TradePlans.RemoveInterval(_zoomCompany, index); };
            _zoomPointer.RangeChanged = RefreshMouseRange;
            _grid.gameObject.SetActive(false); _zoom.gameObject.SetActive(true); _zoom.SetAsLastSibling();
            _zoomChart.name = "ZoomChart_" + company; RefreshZoom();
        }
        public void Collapse()
        { _zoomCompany = null; _entryDay = _hoverDay = null; _grid.gameObject.SetActive(true); _zoom.gameObject.SetActive(false); }
        private void PlanDay(int day, bool right)
        {
            if (_selectInformation && _c.Store.Public?.windowInformation == true)
            { if (!right) _zoomWindow.SetStart(day); return; }
            if (!CanPlan) return;
            if (right) { _entryDay = null; _note = "진입 후보를 취소했습니다."; RefreshZoom(); return; }
            var plan = _c.Store.TradePlans.Get(_zoomCompany);
            if (!_entryDay.HasValue)
            {
                if (plan.Intervals.Length >= UiRules.MaxIntervalsPerCompany) _note = "최대 2구간입니다. 기존 구간을 삭제한 뒤 다시 선택하세요.";
                else if (day > UiRules.MaxDay - UiRules.StockMinHoldingDays) _note = "진입일은 0~23일 중에서 선택하세요.";
                else { _entryDay = day; _note = day + "일 진입 후보 · 최소 7일 뒤의 청산일을 클릭하세요."; }
            }
            else
            {
                string error = _c.Store.TradePlans.TryAddInterval(_zoomCompany, _entryDay.Value, day);
                if (error.Length > 0) _note = ErrorMessages.ToKorean(error);
                else { _entryDay = null; _note = "개인 계획을 저장했습니다. 선택한 기업의 투자 초안에 적용됩니다."; }
            }
            RefreshZoom();
        }
        private void RefreshZoom()
        {
            if (_zoomCompany == null) return;
            var store = _c.Store; var plan = store.TradePlans.Get(_zoomCompany);
            bool stockSystem = store.Public?.stockSystem == true;
            _favorite.gameObject.SetActive(!stockSystem);
            foreach (var rankButton in _rankButtons) rankButton.gameObject.SetActive(!stockSystem);
            bool windows = store.Public?.windowInformation == true;
            if (store.Public?.publicSelectionActive == true) _selectInformation = true;
            _interactionMode.gameObject.SetActive(windows && (store.View?.inventory?.Length ?? 0) > 0 && store.Public?.publicSelectionActive != true);
            UiKit.SetButton(_interactionMode, _showInformationItems ? "예약 목록 보기" : "정보 아이템 보기", true, Palette.AccentSoft, Palette.Accent);
            _zoomLegacyPurchases.gameObject.SetActive(!windows);
            _zoomWindow.Refresh();
            _zoomChart.SetInformationRange(windows && _zoomWindow.Selection.HasSelection ? new Vector2Int(_zoomWindow.StartDay, _zoomWindow.EndDay) : (Vector2Int?)null);
            _planRows.gameObject.SetActive(!_showInformationItems);
            _informationItems.gameObject.SetActive(windows && _showInformationItems);
            RefreshInformationItems();
            _blackbox.gameObject.SetActive(windows && (store.View?.inventory?.Contains("BLACKBOX") ?? false));
            _cancelEntry.gameObject.SetActive(!windows);
            _blackbox.interactable = store.Public?.phase == Protocol.Info && store.Public?.publicSelectionActive != true && store.Public?.paused != true &&
                !store.HasPending(CommandTypes.UseInformationItem) && _zoomWindow.Selection.HasSelection && InformationWindowRules.NewDays(store.View?.companies?.FirstOrDefault(c => c.companyId == _zoomCompany), _zoomWindow.StartDay, Math.Min(30, _zoomWindow.StartDay + 4)) > 0;
            var company = store.View?.companies?.FirstOrDefault(c => c.companyId == _zoomCompany);
            var revealed = store.RevealedTurn == store.CurrentTurn ? store.Revealed?.FirstOrDefault(c => c.companyId == _zoomCompany) : null;
            _zoomBars = DailyBarModel.Build(company, revealed);
            _zoomChart.SetData(_zoomBars);
            var intervals = store.TradePlans.ChartIntervals(store.View, store.CurrentTurn, _zoomCompany, store.OrderLocked);
            if (revealed?.investments != null) _zoomChart.SetRevealedInvestments(revealed.investments);
            else _zoomChart.SetTradeIntervals(intervals, !store.OrderLocked);
            _zoomChart.SetCandidate(_entryDay);
            Vector2Int? allowed = null;
            if (CanPlan && !_selectInformation && (_entryDay.HasValue || plan.Intervals.Length < UiRules.MaxIntervalsPerCompany))
                allowed = _entryDay.HasValue ? new Vector2Int(_entryDay.Value + 5, Math.Min(30, _entryDay.Value + 10)) : new Vector2Int(0, 25);
            _zoomChart.SetAllowed(allowed);
            _zoomTitle.text = CompanyCatalog.Get(_zoomCompany).DisplayName + " · " + _zoomCompany + "  <size=13>"
                + (revealed != null ? "전체 플레이어 투자 구간" : stockSystem ? store.Public?.phase == Protocol.Investment ? "진입·청산 구간 결정" : "주식 분석 · 정보 구매" : store.OrderLocked ? "확정 주문 진입·청산" : windows ? store.Public?.publicSelectionActive == true ? "공용 정보 선택" : "정보 구매 · 구간 예약" : "이번 턴 개인 투자 계획") + "</size>";
            _zoomPurchaseStatus.text = store.Public?.publicSelectionActive == true
                ? "공용 공개 " + (store.Public.publicSelectionIndex + 1) + "/4 · " + store.Public.publicSelectionPlayerId + " 차례"
                : store.Public?.phase == Protocol.Info
                ? "정보 구매 · 보유 " + (store.View?.coins ?? 0) + "코인" + (store.View?.ability?.freeInformationCredits > 0 ? " · 무료 " + store.View.ability.freeInformationCredits + "일" : "")
                : revealed != null ? "공개 결과 · 구간별 수익률은 위 목록에서 확인" : "정보 구매는 정보 탐색 단계에서만";
            for (int i = 0; i < _zoomBundleButtons.Length; i++) CompanyInfo.RefreshPurchaseButton(_zoomBundleButtons[i], _c, _zoomCompany, i, true);
            UiKit.SetButton(_favorite, plan.Favorite ? "★ 찜 해제" : "☆ 찜하기", CanPlan, plan.Favorite ? Palette.AccentSoft : Palette.Card, Palette.Accent);
            for (int i = 0; i < _rankButtons.Length; i++)
                UiKit.SetButton(_rankButtons[i], i == 0 ? "미정" : i + "순위", CanPlan, plan.Rank == i ? Palette.Accent : Palette.Card, plan.Rank == i ? Color.white : Palette.Text);
            _cancelEntry.interactable = CanPlan && _entryDay.HasValue;
            string rowsKey = _zoomCompany + ":" + store.CurrentTurn + ":" + store.Public?.selectionIndex + ":" + (revealed != null) + ":" + store.OrderLocked + ":" + CanPlan + ":" + string.Join(";", intervals.Select(i => i.entryDay + "," + i.exitDay));
            if (_rowKey != rowsKey)
            {
                _rowKey = rowsKey; UiKit.Clear(_planRows);
                if (revealed?.investments != null) BuildRevealedRows(revealed);
                else
                {
                _planRows.sizeDelta = new Vector2(0, Math.Max(72, intervals.Length * 34));
                if (intervals.Length == 0)
                { var empty = UiKit.Label(_planRows, store.OrderLocked ? "이 기업에는 확정된 주문이 없습니다." : "진입일 시가 → 청산일 종가로 예약하세요. 보유 기간·구간 수 제한 없음", 14, Palette.SubText); UiKit.Stretch(empty.rectTransform); }
                for (int i = 0; i < intervals.Length; i++)
                {
                    int index = i; var interval = intervals[i];
                    var row = ScreenKit.TopStrip(_planRows, "Plan_" + i, i * 34, 30);
                    var label = UiKit.Label(row, (store.OrderLocked ? "확정 " : "예약 ") + (i + 1) + " · " + interval.entryDay + "일 시가 → " + interval.exitDay + "일 종가 (" + (interval.exitDay - interval.entryDay + (store.View?.entryAtOpen == true && interval.entryDay > 0 ? 1 : 0)) + "일 보유)", 14, Palette.Text, TextAnchor.MiddleLeft);
                    UiKit.Stretch(label.rectTransform, 0, 0, 90, 0);
                    if (CanPlan)
                    {
                        var remove = UiKit.Button(row, "삭제", 12, Palette.Card, Palette.SubText, () => { if (CanPlan) { _zoomWindow.Selection.Clear(); store.TradePlans.RemoveInterval(_zoomCompany, index); } });
                        remove.name = "RemovePlan_" + i;
                        UiKit.Place((RectTransform)remove.transform, new Vector2(1, .5f), new Vector2(1, .5f), Vector2.zero, new Vector2(76, 28));
                    }
                }
                }
            }
            _zoomMessage.text = !CanPlan ? (store.OrderLocked ? "확정 주문은 바꿀 수 없습니다. 시장 공개 후 플레이어별 구간과 수익률을 표시합니다."
                : "계획은 정보 탐색·기업 선택 단계에서 편집합니다. 투자 단계에서는 투자 화면에서 구간을 변경하고 주문을 확정하세요.")
                : _note.Length > 0 ? _note : "선택한 기업의 예약은 투자 마감 때 자동 적용됩니다. 투자 화면에서 비중과 구간을 수정할 수 있습니다.";
            if (windows) _zoomMessage.text = revealed != null ? "플레이어 색 띠 = 확정 구간 · 아래 목록을 스크롤해 구간별 수익률을 확인하세요. 종목 합산은 해당 기업의 투자금 기준입니다."
                : "진입일 시가 → 청산일 종가 · 예약은 투자 마감 때 자동 적용 · 양 끝 조절 / 몸통 이동 / Del 삭제";
            if (stockSystem && revealed == null) _zoomMessage.text = store.Public?.phase == Protocol.Investment
                ? "확보한 기업의 진입·청산 구간을 예약한 뒤 매수 창에서 구간 확정 버튼을 누르세요. 진입 전·청산 후 수량은 BANK 설정 구간에 예금됩니다. BANK 미설정 시 전체 기간 예금됩니다."
                : "정보 탐색 중에도 진입·청산 구간을 예약할 수 있습니다. 매수 화면과 확대 차트에 예약 구간이 표시되며, 주식을 확보한 기업에만 적용됩니다.";
            if (stockSystem && revealed != null) _zoomMessage.text = "그래프와 구간 수익률은 증강 적용 전 값입니다. 증강·현금을 포함한 최종 수익률은 전광판에서 확인하세요.";
            RefreshZoomHover();
        }
        private void BuildRevealedRows(RevealedCompany revealed)
        {
            float y = 0;
            foreach (var p in revealed.investments)
            {
                var header = UiKit.Label(_planRows, "■ " + p.playerId + " · 종목 합산 " + DisplayFormat.Percent(p.returnBps), 13,
                    Palette.Player(p.playerId), TextAnchor.MiddleLeft, FontStyle.Bold);
                header.name = "PlayerReturn_" + p.playerId;
                // Place directly in the scrolling content so all intervals remain reachable.
                header.rectTransform.anchorMin = new Vector2(0, 1); header.rectTransform.anchorMax = Vector2.one;
                header.rectTransform.offsetMin = new Vector2(0, -y - 24); header.rectTransform.offsetMax = new Vector2(0, -y);
                y += 24;
                foreach (var v in p.intervals)
                {
                    var text = UiKit.Label(_planRows, "    " + v.entryDay + "일 시가 → " + v.exitDay + "일 종가  ·  구간 " + DisplayFormat.Percent(v.returnBps),
                        12, Palette.Player(p.playerId), TextAnchor.MiddleLeft);
                    text.rectTransform.anchorMin = new Vector2(0, 1); text.rectTransform.anchorMax = Vector2.one;
                    text.rectTransform.offsetMin = new Vector2(0, -y - 24); text.rectTransform.offsetMax = new Vector2(0, -y);
                    y += 24;
                }
            }
            if (y == 0) { var label = UiKit.Label(_planRows, "이 기업에는 투자한 플레이어가 없습니다.", 13, Palette.SubText); UiKit.Stretch(label.rectTransform); }
            _planRows.sizeDelta = new Vector2(0, Math.Max(72, y));
        }
        private void RefreshMouseRange()
        {
            if (_zoomCompany == null) return;
            var selection = _zoomWindow.Selection;
            _zoomChart.SetInformationRange(selection.HasSelection ? new Vector2Int(selection.Start, selection.End) : (Vector2Int?)null);
            _zoomWindow.Refresh();
            if (!string.IsNullOrEmpty(_zoomPointer.LastEditError)) _zoomTip.text = ErrorMessages.ToKorean(_zoomPointer.LastEditError);
            else if (selection.HasSelection)
            {
                int? rate = DailyBarModel.IntervalReturnBps(_zoomBars, selection.Start, selection.End, _c.Store.View?.entryAtOpen == true);
                _zoomTip.text = selection.Start + "일 시가 → " + selection.End + "일 종가 · " + (rate.HasValue ? DisplayFormat.Percent(rate.Value) : "미열람 끝점") + " · 보유 기간·구간 수 제한 없음";
            }
        }
        private void RefreshInformationItems()
        {
            var state = _c.Store.Public; var items = _c.Store.View?.inventory ?? Array.Empty<string>();
            string steal = items.Contains("INFORMATION_STEAL") ? "INFORMATION_STEAL" : items.Contains("STEAL") ? "STEAL" : null;
            bool distort = items.Contains("DISTORT");
            bool enabled = state?.phase == Protocol.Info && state?.publicSelectionActive != true && state?.paused != true &&
                string.IsNullOrEmpty(_c.Store.EndReason) && !_c.Store.HasPending(CommandTypes.UseInformationItem);
            string key = steal + "|" + distort + "|" + enabled + "|" + state?.publicSelectionIndex + "|" + state?.publicSelectionActive + "|" + string.Join(",", state?.players?.Select(p => p.playerId) ?? Enumerable.Empty<string>());
            if (_itemsKey == key) return;
            _itemsKey = key; UiKit.Clear(_informationItems);
            var note = UiKit.Label(_informationItems, steal == null && !distort ? "공용 정보는 전원 공개 · 구매·무료·아이템 정보는 본인에게만 보입니다." : "정보 공격 · 상대의 유료 구매 기록에만 적용됩니다.", 12, Palette.SubText, TextAnchor.UpperLeft);
            if (state?.publicSelectionActive == true) note.text = "공용 공개: " + string.Join(" / ", (state.publicInformation ?? Array.Empty<PublicInformationChoice>()).Select(w => w.playerId + " " + w.companyId + " " + w.startDay + "~" + (w.startDay + 4) + "일" + (w.automatic ? "(자동)" : "")));
            UiKit.Stretch(note.rectTransform);
            int slot = 0;
            foreach (var player in state?.players ?? Array.Empty<Player>())
            {
                if (player.playerId == _c.LocalPlayerId) continue;
                string target = player.playerId;
                foreach (string item in new[] { steal, distort ? "DISTORT" : null })
                {
                    if (item == null) continue;
                    string id = item;
                    var button = UiKit.Button(_informationItems, target + (id == "DISTORT" ? " 왜곡" : " 훔치기"), 12,
                        Palette.AccentSoft, Palette.Accent, () => _c.UseInformationItem(id, targetPlayerId: target));
                    button.name = "UseInformationItem_" + id + "_" + target; button.interactable = enabled;
                    UiKit.Place((RectTransform)button.transform, Vector2.zero, Vector2.zero, new Vector2(slot++ * 132, 0), new Vector2(124, 32));
                }
            }
        }
        private void RefreshZoomHover()
        {
            _zoomChart.SetPreview(null, false);
            if (!_hoverDay.HasValue || _hoverDay.Value >= _zoomBars.Length)
            {
                _zoomTip.text = _c.Store.RevealedTurn == _c.Store.CurrentTurn && _c.Store.Revealed != null
                    ? "구간 수익률 = 청산일 종가 − 진입일 시가 · 마우스를 올리면 날짜별 값을 확인합니다."
                    : "미열람 구간은 주가를 표시하지 않습니다.";
                return;
            }
            int day = _hoverDay.Value; _zoomTip.text = DailyBarModel.Tooltip(_zoomBars[day]);
            if (CanPlan && _entryDay.HasValue && day > _entryDay.Value)
            {
                var intervals = _c.Store.TradePlans.Get(_zoomCompany).Intervals.Concat(new[] { new Interval { entryDay = _entryDay.Value, exitDay = day } }).ToArray();
                _zoomChart.SetPreview(new Vector2Int(_entryDay.Value, day), OrderInputValidator.ValidateIntervals(intervals).Length == 0);
                int? ret = DailyBarModel.IntervalReturnBps(_zoomBars, _entryDay.Value, day, _c.Store.View?.entryAtOpen == true);
                _zoomTip.text += " · 구간 " + (ret.HasValue ? DisplayFormat.Percent(ret.Value) : "수익률 미확인");
            }
        }

        public void Refresh()
        {
            var v = _c.Store.View;
            var phase = _c.Store.Public?.phase;
            var state = _c.Store.Public;
            bool windows = state?.windowInformation == true;
            bool showingResults = phase == Protocol.Reveal || phase == Protocol.Result;
            _title.text = showingResults ? "시장 공개 · 투자 결과" : phase == Protocol.Investment && state?.stockSystem == true ? "진입·청산 구간 결정" : state?.publicSelectionActive == true ? "공용 정보 선택" : "정보 구매 창";
            _publicHistory.gameObject.SetActive(windows);
            UiKit.Stretch(_grid, 0, windows ? 76 : 34, 0, 0);
            if (windows) _publicHistory.text = showingResults ? "플레이어 색 띠 = 확정 거래 구간 · 기업별 합산 수익률 표시 · 확대하면 구간별 수익률을 볼 수 있습니다."
                : (state.publicSelectionActive ? "각자 5일씩 무료 공개 · 겹치지 않게 선택 · " : "정보는 연속 5일 · 새 정보 1일 = 1코인 · 마우스로 선택 후 정보 사기 / 예약하기 · ")
                + string.Join(" / ", (state.publicInformation ?? Array.Empty<PublicInformationChoice>()).Select(c =>
                    c.playerId + " " + c.companyId + " " + c.startDay + "~" + (c.startDay + 4) + "일" + (c.automatic ? "(자동)" : "")));
            _header.text = v == null
                ? "개인 정보 수신 대기"
                : "보유 코인 <b>" + v.coins + "개</b>   ·   자산 " + DisplayFormat.Money(v.assets)
                  + (phase == Protocol.Info ? state?.publicSelectionActive == true ? " · " + state.publicSelectionPlayerId + " 공개 차례" : "   ·   5일 정보마다 최소 4일 공백" : "   ·   구매는 정보 탐색 단계에서만");
            if (state?.stockSystem == true && !showingResults && !state.publicSelectionActive)
                _publicHistory.text = (phase == Protocol.Investment ? "보유 기업의 차트에서 구간을 예약하세요. 구간을 지정하지 않으면 전체 기간 보유합니다.\n" : "1코인 = 새 날짜 1일 · 정보 할인 " + (v?.portfolio?.informationDiscount ?? 0) + "일 · 매수는 각 7초씩 2회, 차례마다 최대 15주입니다.\n")
                    + string.Join(" / ", (v?.portfolio?.insights ?? Array.Empty<StockInsight>()).Select(i => i.companyId + " 이벤트 성공 가능성 " + DisplayFormat.Percent(i.probabilityBps, false)));
            foreach (var card in _cards) card.Refresh();
            if (_zoomCompany != null)
            {
                if (_zoomTurn != _c.Store.CurrentTurn || _zoomPhase != phase) Collapse();
                else RefreshZoom();
            }
        }
    }

    internal sealed class CompanyCard
    {
        private readonly string _companyId;
        private readonly GameClientController _c;
        private readonly Text _seats;
        private readonly BarChartGraphic _chart;
        private readonly Text _tip;
        private readonly Text _planLabel;
        private readonly Button _expand;
        private DayBar[] _bars = new DayBar[0];
        private readonly Button[] _bundleButtons = new Button[UiRules.BundleCount];
        private readonly RectTransform _legacyBundles;
        private readonly InformationWindowSelector _window;
        private readonly ChartPointerLayer _pointer;
        private int _selectionTurn = -1;
        public string CompanyId => _companyId;
        public int StartDay => _window.StartDay;
        public ChartRangeSelection Selection => _window.Selection;
        public void SetStartDay(int start) => _window.SetStart(start);

        public CompanyCard(RectTransform cell, string companyId, GameClientController controller, Action<string> enlarge = null)
        {
            _companyId = companyId;
            _c = controller;

            var bg = UiKit.Panel(cell, "Bg", Palette.CardBorder, rounded: true);
            UiKit.Stretch(bg.rectTransform);
            var inner = UiKit.Panel(bg.transform, "Inner", Palette.Card, rounded: true);
            UiKit.Stretch(inner.rectTransform, 1, 1, 1, 1);
            var root = inner.rectTransform;

            // NEXT-D03 기업 이미지(없으면 배지)와 표시명. 통신 ID는 그대로.
            var entry = CompanyCatalog.Get(companyId);
            var logo = CompanyBadge.Create(root, companyId, 24);
            UiKit.Place(logo, new Vector2(0, 1), new Vector2(0, 1), new Vector2(8, -5), new Vector2(24, 24));
            var name = UiKit.Label(root, "<b>" + entry.DisplayName + "</b> <size=10>" + entry.Id + " · " + entry.Volatility + "</size>",
                14, Palette.Text, TextAnchor.UpperLeft);
            UiKit.Stretch(name.rectTransform, 38, 8, 84, 0);

            _expand = UiKit.Button(root, "확대하기", 11, Palette.AccentSoft, Palette.Accent, () => enlarge?.Invoke(_companyId));
            _expand.name = "Expand_" + companyId;
            UiKit.Place((RectTransform)_expand.transform, Vector2.one, Vector2.one, new Vector2(-6, -5), new Vector2(70, 24));

            _seats = UiKit.Label(root, "", 12, Palette.SubText, TextAnchor.UpperRight);
            UiKit.Place(_seats.rectTransform, Vector2.one, Vector2.one, new Vector2(-8, -31), new Vector2(90, 16));

            // 그래프 영역
            var chartRt = UiKit.Rect(root, "Chart");
            UiKit.Stretch(chartRt, 10, 54, 10, 82);
            _chart = chartRt.gameObject.AddComponent<BarChartGraphic>();
            var pointer = _pointer = chartRt.gameObject.AddComponent<ChartPointerLayer>(); // 마우스 위치 날짜의 값 툴팁
            pointer.Hovered = OnHover;
            pointer.Clicked = (day, right) => { if (!right && _c.Store.Public?.windowInformation == true) _window?.SetStart(day); };

            _tip = UiKit.Label(root, "", 10, Palette.Text, TextAnchor.MiddleRight);
            UiKit.Place(_tip.rectTransform, new Vector2(1, 1), new Vector2(1, 1), new Vector2(-8, -40), new Vector2(220, 14));

            _planLabel = UiKit.Label(root, "", 10, Palette.Accent, TextAnchor.MiddleLeft);
            _planLabel.name = "PlanBadge_" + companyId;
            _planLabel.rectTransform.anchorMin = Vector2.zero; _planLabel.rectTransform.anchorMax = new Vector2(1, 0);
            _planLabel.rectTransform.offsetMin = new Vector2(8, 40); _planLabel.rectTransform.offsetMax = new Vector2(-8, 68);

            foreach (int day in new[] { 0, 10, 20, 30 })
            {
                var tick = UiKit.Label(chartRt, day + "일", 9, Palette.SubText, TextAnchor.UpperCenter);
                UiKit.Place(tick.rectTransform, new Vector2((day + 0.5f) / DayAxis.Count, 0), new Vector2(0.5f, 1), new Vector2(0, -1), new Vector2(40, 14));
            }

            // 묶음 버튼 6개
            var row = _legacyBundles = UiKit.Rect(root, "Bundles");
            row.anchorMin = new Vector2(0, 0);
            row.anchorMax = new Vector2(1, 0);
            row.pivot = new Vector2(0.5f, 0);
            row.offsetMin = new Vector2(6, 6);
            row.offsetMax = new Vector2(-6, 38);
            for (int k = 0; k < UiRules.BundleCount; k++)
            {
                int bundleIndex = k;
                var b = UiKit.Button(row, "", 10, Palette.Disabled, Palette.Text, () => OnBundleClicked(bundleIndex));
                UiKit.Anchor((RectTransform)b.transform, k / 6f, 0, (k + 1) / 6f, 1, 2, 0);
                _bundleButtons[k] = b;
            }
            var windowRow = UiKit.Rect(root, "InformationWindow");
            windowRow.anchorMin = Vector2.zero; windowRow.anchorMax = new Vector2(1, 0);
            windowRow.offsetMin = new Vector2(6, 2); windowRow.offsetMax = new Vector2(-6, 42);
            _window = new InformationWindowSelector(windowRow, _c, () => _companyId);
            _window.Changed = start => DrawSelection();
            _window.CanReserve = () => CanPlan && _c.Store.TradePlans.RangeError(_companyId, Selection.SelectedIndex, Selection.Start, Selection.End) == "";
            _window.Reserve = () => { SaveReservation(); EventSystem.current?.SetSelectedGameObject(_pointer.gameObject); };
            pointer.CanEditRange = () => _c.Store.Public?.paused != true && string.IsNullOrEmpty(_c.Store.EndReason) &&
                (_c.Store.Public?.phase == Protocol.Info || CanPlan);
            pointer.Intervals = () => CanPlan ? _c.Store.TradePlans.Get(_companyId).Intervals : Array.Empty<Interval>();
            pointer.EditInterval = (index, entryDay, exitDay) => CanPlan ? _c.Store.TradePlans.TryReplaceInterval(_companyId, index, entryDay, exitDay) : ErrorCodes.OrderLocked;
            pointer.DeleteInterval = index => { if (CanPlan) _c.Store.TradePlans.RemoveInterval(_companyId, index); };
            pointer.RangeChanged = () => { DrawSelection(); _window.Refresh(); };
        }
        private bool CanPlan => _c.CanPlanTrade(_companyId);
        public string SaveReservation()
        {
            if (!CanPlan || !Selection.HasSelection) return "지금은 예약할 수 없습니다.";
            int index = Selection.SelectedIndex;
            if (index < 0) index = _c.Store.TradePlans.Get(_companyId).Intervals.Length;
            string error = Selection.SelectedIndex >= 0 ? _c.Store.TradePlans.TryReplaceInterval(_companyId, index, Selection.Start, Selection.End)
                : _c.Store.TradePlans.TryAddInterval(_companyId, Selection.Start, Selection.End);
            if (error == "") { Selection.SelectInterval(index, Selection.Start, Selection.End); DrawSelection(); _window.Refresh(); }
            return error == "" ? "예약했습니다. 선택한 기업이면 투자 마감 때 자동 적용됩니다." : ErrorMessages.ToKorean(error);
        }
        private void DrawSelection() => _chart.SetInformationRange(_c.Store.Public?.windowInformation == true && Selection.HasSelection
            ? new Vector2Int(Selection.Start, Selection.End) : (Vector2Int?)null);

        private void OnBundleClicked(int bundleIndex)
        {
            _c.BuyInformation(_companyId, bundleIndex);
        }

        public void Refresh()
        {
            // 남은 자리 수는 정보 구매 창에 표시하지 않는다(자리 선택 화면에서만 보임). 내가 고른 기업 표시만 남김.
            if (_selectionTurn != _c.Store.CurrentTurn) { _selectionTurn = _c.Store.CurrentTurn; Selection.Clear(); }
            _seats.text = _c.Store.OwnSelections.Contains(_companyId) ? "<color=#2563EB><b>내 선택</b></color>" : "";
            _expand.interactable = _c.Store.View != null;
            var intervals = _c.Store.TradePlans.ChartIntervals(_c.Store.View, _c.Store.CurrentTurn, _companyId, _c.Store.OrderLocked);
            _planLabel.text = _c.Store.OrderLocked ? (intervals.Length == 0 ? "" : "확정: " + string.Join(", ", intervals.Select(i => i.entryDay + "→" + i.exitDay + "일"))) : _c.Store.TradePlans.Summary(_companyId);

            var company = _c.Store.View?.companies?.FirstOrDefault(x => x.companyId == _companyId);
            var revealed = _c.Store.Revealed != null && _c.Store.RevealedTurn == _c.Store.CurrentTurn
                ? _c.Store.Revealed.FirstOrDefault(x => x.companyId == _companyId)
                : null;
            if (revealed != null) _planLabel.text = CompanyInfo.RevealedSummary(revealed);
            DrawChart(company, revealed);
            RefreshButtons();
        }

        private void DrawChart(VisibleCompany company, RevealedCompany revealed)
        {
            // 시장 공개(주문 잠금) 이후에만 31일 전체, 그 전에는 열람한 날짜만.
            _bars = DailyBarModel.Build(company, revealed);
            _chart.SetData(_bars);
            if (revealed?.investments != null) _chart.SetRevealedInvestments(revealed.investments);
            else _chart.SetTradeIntervals(_c.Store.TradePlans.ChartIntervals(_c.Store.View, _c.Store.CurrentTurn, _companyId, _c.Store.OrderLocked), !_c.Store.OrderLocked);
        }

        private void OnHover(int? day)
        {
            _tip.text = day.HasValue && day.Value < _bars.Length ? DailyBarModel.Tooltip(_bars[day.Value]) : "";
        }

        private void RefreshButtons()
        {
            bool windows = _c.Store.Public?.windowInformation == true;
            _legacyBundles.gameObject.SetActive(!windows); _window.Refresh();
            _pointer.RangeSelection = windows ? Selection : null;
            DrawSelection();
            for (int k = 0; k < _bundleButtons.Length; k++)
                CompanyInfo.RefreshPurchaseButton(_bundleButtons[k], _c, _companyId, k);
        }
    }
}
