// 매수 창: 단계에 따라 내용이 바뀐다.
// - 정보 탐색: 안내 + 기업별 남은 자리
// - 기업 선택: 기업 카드 클릭 즉시 서버에 선택 요청. 정원 2칸(내 확정/익명 선점/빈칸).
// - 투자 결정: 비율·순서·시가→종가 예약, 마감 자동 주문 또는 조기 확정. 일반 게임은 보유 기간/구간 수 제한 없음.
// - 시장 공개/결과: 확정 주문과 결과 표시
// 타인 구간은 주문 잠금 뒤 시장 공개 그래프에서만 공개한다. 개인 예약·배분 금액은 비공개다.
using System.Collections.Generic;
using System.Linq;
using BlackWhale.Shared;
using BlackWhale.UI.Presentation;
using BlackWhale.UI.State;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;

namespace BlackWhale.UI.View
{
    public sealed class InvestmentPage
    {
        private readonly GameClientController _c;
        private readonly LobbyActions _lobby;
        private readonly Text _status;
        private readonly Text _title;
        private readonly RectTransform _body;
        private readonly StockSystemPage _stockPage;
        public System.Action<string> OpenStockPlan;

        private InvestmentDraft _draft;
        private int _draftTurn = -1;

        // 날짜 입력 중에는 화면 전체를 다시 그리지 않고 아래 요소만 바로 갱신한다
        // (전체를 다시 그리면 입력 직후 '주문 확정' 첫 클릭이 사라질 수 있음).
        private Text _orderMessage;
        private Button _submitButton;
        private readonly Dictionary<string, BarChartGraphic> _orderCharts = new Dictionary<string, BarChartGraphic>();
        private readonly Dictionary<string, Text> _chartTips = new Dictionary<string, Text>();
        private readonly Dictionary<string, ChartRangeSelection> _mouseRanges = new Dictionary<string, ChartRangeSelection>();
        private readonly Dictionary<string, Button> _reserveButtons = new Dictionary<string, Button>();
        private readonly List<KeyValuePair<Interval, Text>> _holdLabels = new List<KeyValuePair<Interval, Text>>();

        // 날짜 구간 편집은 주문 확정 전까지 로컬 초안으로 유지한다.
        private readonly IntervalPicker _picker = new IntervalPicker(null);

        public InvestmentPage(RectTransform page, GameClientController controller, LobbyActions lobby = null)
        {
            _c = controller;
            _lobby = lobby;

            var title = UiKit.Label(page, "매수 창", 18, Palette.Text, TextAnchor.MiddleLeft, FontStyle.Bold);
            _title = title;
            UiKit.Place(title.rectTransform, new Vector2(0, 1), new Vector2(0, 1), new Vector2(6, -2), new Vector2(200, 28));

            _status = UiKit.Label(page, "", 14, Palette.SubText, TextAnchor.MiddleRight);
            UiKit.Place(_status.rectTransform, new Vector2(1, 1), new Vector2(1, 1), new Vector2(-6, -2), new Vector2(640, 28));

            _body = UiKit.Rect(page, "Body");
            UiKit.Stretch(_body, 0, 34, 0, 0);
            _stockPage = new StockSystemPage(_body, _c) { OpenPlan = id => OpenStockPlan?.Invoke(id) };
        }

        public void Refresh()
        {
            var current = _c.Store.Public;
            bool stock = current?.stockSystem == true && current.phase != Protocol.Lobby;
            _title.gameObject.SetActive(!stock); _status.gameObject.SetActive(!stock);
            UiKit.Stretch(_body, 0, stock ? 0 : 34, 0, 0);
            if (stock && _stockPage.TryRefresh()) return;
            UiKit.Clear(_body);
            var s = _c.Store.Public;
            if (s == null)
            {
                Message(_body, "게임 상태를 기다리는 중입니다.");
                return;
            }

            string mine = _c.Store.OwnSelections.Count == 0 ? "없음" : string.Join(", ", _c.Store.OwnSelections);
            _status.text = s.turn + "턴 · " + PhaseNames.Korean(s.phase) + "   ·   내 선택: " + mine;
            if (s.stockSystem && s.phase != Protocol.Lobby)
            {
                _status.text = s.turn + "/" + s.totalTurns + "턴 · " + PhaseNames.ForState(s) + " · 보유 " + (_c.Store.View?.portfolio?.usedSlots ?? 0) + "/30칸";
                _stockPage.Build(); return;
            }

            if (!string.IsNullOrEmpty(_c.Store.EndReason) || s.phase == Protocol.Ended)
            {
                BuildEnded();
                return;
            }

            switch (s.phase)
            {
                case Protocol.Lobby:
                    BuildLobby(s);
                    break;
                case Protocol.Info:
                    BuildSelectionGrid(false, "지금은 정보 탐색 단계입니다. 오른쪽 가장자리를 눌러 정보 구매 창에서 정보를 확인하세요. 다음 단계에서 기업 2개를 고릅니다.");
                    break;
                case Protocol.Selection:
                    BuildSelectionGrid(true, SelectionHint(s));
                    break;
                case Protocol.Investment:
                    if (_c.Store.OrderLocked) BuildOrderSummary("주문이 확정되었습니다. 4명이 모두 확정하면 바로 시장 공개로 넘어갑니다.");
                    else BuildOrderForm();
                    break;
                case Protocol.Reveal:
                    BuildOrderSummary("시장 공개 중입니다. 주문은 잠겨 있습니다. 정보 구매 창에서 전체 그래프를 확인할 수 있습니다.");
                    break;
                case Protocol.Result:
                    BuildResult();
                    break;
                default:
                    Message(_body, PhaseNames.Korean(s.phase) + " 단계입니다.");
                    break;
            }
        }

        // ------------------------------------------------------------------
        // 대기실·종료
        // ------------------------------------------------------------------

        private void BuildLobby(PublicState s)
        {
            _status.text = "대기실 · 4명이 모두 준비하면 방장이 시작합니다";
            var card = UiKit.Panel(_body, "Lobby", Palette.Card, rounded: true);
            UiKit.Stretch(card.rectTransform, 28, 12, 28, 12);
            var root = card.rectTransform;

            var title = UiKit.Label(root, "<b>참가자</b>  <size=12>" + (s.players?.Length ?? 0) + " / 4명</size>", 18, Palette.Text, TextAnchor.UpperLeft);
            UiKit.Stretch(title.rectTransform, 24, 18, 24, 0);

            var players = s.players ?? new Player[0];
            for (int i = 0; i < 4; i++)
            {
                var p = i < players.Length ? players[i] : null;
                var row = UiKit.Panel(root, "Player" + (i + 1), p == null ? Palette.Disabled : Palette.Screen, rounded: true);
                UiKit.Place(row.rectTransform, new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(0, -60 - i * 54), new Vector2(520, 46));
                string text = p == null
                    ? "<color=#9CA3AF>빈 자리 · 접속 대기</color>"
                    : "<color=#" + ColorUtility.ToHtmlStringRGB(Palette.Player(p.playerId)) + ">■</color>  <b>" + p.playerId + "</b>  "
                      + (p.displayName ?? "").Replace("<", "‹").Replace(">", "›") + (p.isBot ? "  <size=11>BOT</size>" : "") + (p.playerId == _c.LocalPlayerId ? " (나)" : "") + (p.playerId == "P1" ? "  <size=11>방장</size>" : "")
                      + "      " + (!p.connected ? "<color=#DC2626>연결 끊김</color>" : p.ready ? "<color=#047857><b>준비 완료</b></color>" : "<color=#6B7280>준비 전</color>");
                var label = UiKit.Label(row.transform, text, 15, Palette.Text, TextAnchor.MiddleLeft);
                label.resizeTextForBestFit = true;
                label.resizeTextMinSize = 10;
                label.resizeTextMaxSize = 15;
                UiKit.Stretch(label.rectTransform, 16, 0, 16, 0);
            }

            bool ready = _c.IsLocalReady;
            bool readyPending = _c.Store.HasPending(CommandTypes.SetReady);
            var readyButton = UiKit.Button(root, readyPending ? "처리 중…" : ready ? "준비 취소" : "준비", 16,
                ready ? Palette.Card : Palette.Accent, ready ? Palette.Text : Color.white, () => _c.SetReady(!ready));
            UiKit.Place((RectTransform)readyButton.transform, new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(_c.IsHost ? -100 : 0, 24), new Vector2(180, 46));
            readyButton.name = "Ready";
            readyButton.interactable = !readyPending && _lobby?.Busy != true;

            if (_c.IsHost)
            {
                bool can = _c.AllPlayersReady && !_c.Store.HasPending(CommandTypes.StartGame) && _lobby?.Busy != true;
                var start = UiKit.Button(root, "게임 시작", 16, can ? Palette.Success : Palette.Disabled, can ? Color.white : Palette.DisabledText,
                    () => _c.StartGame());
                UiKit.Place((RectTransform)start.transform, new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(100, 24), new Vector2(180, 46));
                start.interactable = can;
                start.name = "StartGame";
                if (_lobby != null)
                {
                    var fill = UiKit.Button(root, "빈자리를 BOT으로 채우기", 13, Palette.AccentSoft, Palette.Accent, _lobby.FillBots);
                    fill.name = "FillBots";
                    UiKit.Place((RectTransform)fill.transform, new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(-100, 80), new Vector2(220, 34));
                    fill.interactable = !_lobby.Busy && players.Count(p => p.connected) < 4;
                    var remove = UiKit.Button(root, "BOT 모두 내보내기", 13, Palette.Card, Palette.Text, _lobby.RemoveBots);
                    remove.name = "RemoveBots";
                    UiKit.Place((RectTransform)remove.transform, new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(130, 80), new Vector2(200, 34));
                    remove.interactable = !_lobby.Busy && players.Any(p => p.isBot);
                }
            }
            if (_lobby != null)
            {
                var status = UiKit.Label(root, _lobby.Status, 12, Palette.SubText);
                UiKit.Place(status.rectTransform, new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(0, 120), new Vector2(640, 54));
            }
        }

        private void BuildEnded()
        {
            string reason = _c.Store.EndReason;
            string text = "<b>게임이 종료되었습니다.</b>\n\n" + (string.IsNullOrEmpty(reason) ? "" : ErrorMessages.ToKorean(reason));
            if (_c.Store.Scoreboard != null) text += "\n\n왼쪽 순위 칸에 마우스를 올려 결과를 확인할 수 있습니다.";
            Message(_body, text);
        }

        // ------------------------------------------------------------------
        // 기업 선택: 카드 클릭 즉시 요청, 서버 응답으로 확정
        // ------------------------------------------------------------------

        private string SelectionHint(PublicState s)
        {
            if (_c.IsMyTurnToSelect())
                return "<color=#DC2626><b>내 차례입니다.</b></color> <b>기업 카드를 누르면 바로 선택됩니다.</b> ("
                       + (_c.Store.OwnSelections.Count + 1) + "번째 / 2개)";
            if (string.IsNullOrEmpty(s.currentPlayerId)) return "기업 선택 진행 중";
            return s.currentPlayerId + " 님이 고르는 중입니다. 차례 순서: A→B→C→D→D→C→B→A";
        }

        /// <summary>남은 차례 시간(초). TabletView가 서버 마감 기준 PhaseClock으로 채워 준다.</summary>
        public System.Func<int> RemainingSeconds { get; set; }

        private int SecondsLeft => RemainingSeconds?.Invoke() ?? 1;

        private void BuildSelectionGrid(bool active, string hint)
        {
            _orderTimer = null;
            var order = active ? _c.Store.SelectionOrder.Build(_c.Store.Public, _c.LocalPlayerId) : null;
            bool showOrder = order != null && order.Slots.Length > 0;
            if (showOrder) hint = order.Summary() + (order.IsMyTurn ? "  ·  <b>카드를 누르면 바로 선택</b>" : "");
            if (_c.Store.HasPending(CommandTypes.SelectCompany)) hint = "<b>기업 선택을 처리하고 있습니다…</b>";

            var hintText = UiKit.Label(_body, hint, 15, Palette.Text, TextAnchor.MiddleLeft);
            UiKit.Place(hintText.rectTransform, new Vector2(0, 1), new Vector2(0, 1), new Vector2(8, -4), new Vector2(860, 40));
            hintText.rectTransform.anchorMax = new Vector2(1, 1);
            hintText.rectTransform.sizeDelta = new Vector2(-16, 40);

            float top = 48;
            if (showOrder)
            {
                BuildOrderStrip(order);
                top = 106;
            }

            bool myTurn = active && _c.IsMyTurnToSelect();
            var grid = UiKit.Rect(_body, "SelectGrid");
            UiKit.Stretch(grid, 0, top, 0, 0);
            var ids = UiRules.CompanyIds;
            for (int i = 0; i < ids.Length; i++)
            {
                int col = i % 3, row = i / 3;
                var cell = UiKit.Rect(grid, "Pick_" + ids[i]);
                UiKit.Anchor(cell, col / 3f, row == 0 ? 0.5f : 0f, (col + 1) / 3f, row == 0 ? 1f : 0.5f, 6, 6);
                BuildCompanyPickCard(cell, ids[i], myTurn);
            }

        }

        /// <summary>선택 차례 줄: 1~8번째 칸에 누가 고르는지, 지금 차례(주황), 내 차례(파랑), 끝난 차례(회색).
        /// 아직 모르는 칸은 '?'(앞사람이 고르면 채워짐).</summary>
        private void BuildOrderStrip(SelectionOrderView order)
        {
            var strip = UiKit.Rect(_body, "SelectionOrder");
            strip.anchorMin = new Vector2(0, 1);
            strip.anchorMax = new Vector2(1, 1);
            strip.pivot = new Vector2(0.5f, 1);
            strip.offsetMin = new Vector2(6, -102);
            strip.offsetMax = new Vector2(-6, -46);

            int n = order.Slots.Length;
            int half = n / 2;
            const float gap = 18f; // 앞 절반 / 되돌아오는 절반 사이
            for (int i = 0; i < n; i++)
            {
                var slot = order.Slots[i];
                var cell = UiKit.Rect(strip, "Pick" + (i + 1));
                float a = i / (float)n, b = (i + 1) / (float)n;
                cell.anchorMin = new Vector2(a, 0);
                cell.anchorMax = new Vector2(b, 1);
                cell.offsetMin = new Vector2(i >= half ? gap * 0.5f + 2 : 2, 0);
                cell.offsetMax = new Vector2(i < half ? -gap * 0.5f - 2 : -2, 0);

                bool current = slot.State == PickSlotState.Current;
                bool done = slot.State == PickSlotState.Done;
                Color frameColor = current ? ScreenKit.Amber : slot.IsMine ? Palette.Accent : Palette.CardBorder;
                Color fill = current ? new Color32(0xFE, 0xF3, 0xC7, 0xFF) : done ? Palette.Disabled : slot.IsMine ? Palette.AccentSoft : Palette.Card;
                var frame = UiKit.Panel(cell, "Frame", frameColor, rounded: true);
                UiKit.Stretch(frame.rectTransform);
                var inner = UiKit.Panel(frame.transform, "Inner", fill, rounded: true);
                float border = current || slot.IsMine ? 2.5f : 1f;
                UiKit.Stretch(inner.rectTransform, border, border, border, border);

                // 플레이어 색 띠(누가 누군지 순위 칸과 같은 색으로)
                if (slot.PlayerId != null)
                {
                    var band = UiKit.Panel(inner.transform, "PlayerColor", Palette.Player(slot.PlayerId));
                    band.rectTransform.anchorMin = new Vector2(0, 0);
                    band.rectTransform.anchorMax = new Vector2(0, 1);
                    band.rectTransform.offsetMin = new Vector2(3, 6);
                    band.rectTransform.offsetMax = new Vector2(7, -6);
                }

                string name = slot.IsMine ? "나" : Short(slot.Name, 6);
                string status = current ? (slot.IsMine ? "내 차례!" : "선택 중") : done ? "완료" : slot.IsMine ? "내 차례" : "";
                Color textColor = done ? Palette.DisabledText : slot.IsMine ? Palette.Accent : Palette.Text;
                var label = UiKit.Label(inner.transform,
                    "<size=10>" + (i + 1) + "번째</size>\n<b>" + name + "</b>", 13, textColor, TextAnchor.MiddleLeft);
                UiKit.Stretch(label.rectTransform, 11, 2, 2, 2);

                if (status.Length > 0)
                {
                    var st = UiKit.Label(inner.transform, status, 10,
                        current ? Palette.Warning : done ? Palette.DisabledText : Palette.Accent, TextAnchor.UpperRight, FontStyle.Bold);
                    UiKit.Stretch(st.rectTransform, 2, 3, 5, 2);
                }
                if (current)
                {
                    _orderTimer = UiKit.Label(inner.transform, "", 10, Palette.Warning, TextAnchor.LowerRight);
                    UiKit.Stretch(_orderTimer.rectTransform, 2, 2, 5, 3);
                    UpdateOrderTimer();
                }
            }

            // 가운데 틈 = 반환점(뱀 순서: 앞 절반 다음 역순으로 되돌아옴)
        }

        private static string Short(string text, int max)
            => string.IsNullOrEmpty(text) || text.Length <= max ? text ?? "" : text.Substring(0, max - 1) + "…";

        private Text _orderTimer;

        private void UpdateOrderTimer()
        {
            if (_orderTimer == null) return;
            _orderTimer.text = DisplayFormat.Clock(Mathf.Max(0, SecondsLeft));
        }

        private void BuildCompanyPickCard(RectTransform cell, string id, bool myTurn)
        {
            var entry = CompanyCatalog.Get(id);
            int seats = _c.RemainingSeats(id);
            bool own = _c.Store.OwnSelections.Contains(id);
            bool selectable = myTurn && _c.CanSelect(id) && SecondsLeft > 0 && string.IsNullOrEmpty(_c.Store.EndReason);
            var company = _c.Store.View?.companies?.FirstOrDefault(c => c.companyId == id);
            int known = InformationWindowRules.KnownDays(company).Skip(1).Count(d => d.HasValue);
            var slots = SeatMath.Compute(seats, own);

            Color border = own || selectable ? Palette.Accent : Palette.CardBorder;
            var frame = UiKit.Panel(cell, "Frame", border, rounded: true);
            UiKit.Stretch(frame.rectTransform);
            Color bg = own ? Palette.AccentSoft : slots.Empty == 0 ? Palette.Disabled : Palette.Card;
            var button = UiKit.Button(frame.transform, "", 12, bg, Palette.Text, () => OnCardClicked(id));
            button.name = "SelectCompany_" + id;
            float inset = own || selectable ? 3 : 1;
            UiKit.Stretch((RectTransform)button.transform, inset, inset, inset, inset);
            button.interactable = selectable;
            var colors = button.colors;
            colors.disabledColor = Color.white; // 비활성이어도 상태 색 유지
            button.colors = colors;
            var root = (RectTransform)button.transform;

            var logo = CompanyBadge.Create(root, id, 52, dimmed: slots.Empty == 0 && !own);
            UiKit.Place(logo, new Vector2(0, 1), new Vector2(0, 1), new Vector2(12, -12), new Vector2(52, 52));

            var name = UiKit.Label(root, "<b>" + entry.DisplayName + "</b>\n<size=10>" + entry.Id + " · " + entry.Volatility + "</size>"
                + "\n<size=11><color=#2563EB>" + _c.Store.TradePlans.Summary(id) + "</color></size>",
                16, slots.Empty == 0 && !own ? Palette.DisabledText : Palette.Text, TextAnchor.UpperLeft);
            name.name = "SelectionPlan_" + id;
            UiKit.Place(name.rectTransform, new Vector2(0, 1), new Vector2(0, 1), new Vector2(72, -12), new Vector2(200, 52));

            var info = UiKit.Label(root, "열람 " + known + "/30일", 11, Palette.SubText, TextAnchor.UpperRight);
            UiKit.Place(info.rectTransform, new Vector2(1, 1), new Vector2(1, 1), new Vector2(-10, -12), new Vector2(90, 16));

            // 정원 2칸: 내 확정 / 익명 선점 / 빈칸 (타인 이름·주문은 표시하지 않음)
            var seatRow = UiKit.Rect(root, "Seats");
            UiKit.Place(seatRow, new Vector2(0, 0), new Vector2(0, 0), new Vector2(12, 34), new Vector2(170, 30));
            int k = 0;
            for (int m = 0; m < slots.Mine; m++) SeatBox(seatRow, k++, "나", Palette.Accent, Color.white);
            for (int a = 0; a < slots.OccupiedAnonymous; a++) SeatBox(seatRow, k++, "선점", new Color32(0x6B, 0x72, 0x80, 0xFF), Color.white);
            for (int e = 0; e < slots.Empty; e++) SeatBox(seatRow, k++, "빈칸", Color.white, Palette.SubText, outline: true);

            string state = own ? "<color=#2563EB><b>내 확정</b></color>"
                : slots.Empty == 0 ? "정원 마감"
                : selectable ? "<color=#2563EB><b>클릭하여 바로 선택</b></color>" : "남은 자리 " + slots.Empty;
            var stateText = UiKit.Label(root, state, 12, Palette.SubText, TextAnchor.LowerLeft);
            UiKit.Place(stateText.rectTransform, new Vector2(0, 0), new Vector2(0, 0), new Vector2(12, 8), new Vector2(240, 22));
        }

        private static void SeatBox(RectTransform row, int index, string text, Color fill, Color fg, bool outline = false)
        {
            var outer = UiKit.Panel(row, "Seat" + index, outline ? Palette.CardBorder : fill, rounded: true);
            UiKit.Place(outer.rectTransform, new Vector2(0, 0.5f), new Vector2(0, 0.5f), new Vector2(index * 80, 0), new Vector2(74, 28));
            var host = outer.transform;
            if (outline)
            {
                var inner = UiKit.Panel(outer.transform, "Inner", fill, rounded: true);
                UiKit.Stretch(inner.rectTransform, 1, 1, 1, 1);
                host = inner.transform;
            }
            var t = UiKit.Label(host, text, 12, fg, TextAnchor.MiddleCenter, FontStyle.Bold);
            UiKit.Stretch(t.rectTransform);
        }

        private void OnCardClicked(string id)
        {
            if (!_c.IsMyTurnToSelect() || !_c.CanSelect(id) || SecondsLeft <= 0 || !string.IsNullOrEmpty(_c.Store.EndReason)) return;
            _c.SelectCompany(id);
        }

        // 매 프레임 서버 기한의 남은 시간을 표시한다.
        /// <summary>주식 매수 단계 연출용 남은 시간(소수). TabletView가 서버 시계로 채운다.</summary>
        public System.Func<double> RemainingExact
        {
            get => _stockPage.RemainingExact;
            set => _stockPage.RemainingExact = value;
        }

        public void Tick()
        {
            _stockPage.Tick();
            UpdateOrderTimer();
            if (SecondsLeft <= 0)
                foreach (var button in _body.GetComponentsInChildren<Button>())
                    if (button.name.StartsWith("SelectCompany_")) button.interactable = false;
        }

        // ------------------------------------------------------------------
        // 주문 입력 (NEXT-D01 일봉 + NEXT-D02 클릭 초안)
        // ------------------------------------------------------------------

        private void EnsureDraft()
        {
            int turn = _c.Store.CurrentTurn;
            var sel = _c.Store.OwnSelections;
            bool same = _draft != null && _draftTurn == turn
                        && sel.Count == 2 && sel.Contains(_draft.FirstCompanyId) && sel.Contains(_draft.SecondCompanyId);
            if (same) return;

            // 턴·선택 기업이 바뀌면 이전 초안과 진입일 후보를 버린다(잘못된 기업/턴의 후보가 남지 않게).
            _draft = InvestmentDraft.FromSelections(sel, _c.Store.TradePlans);
            _draftTurn = turn;
            _mouseRanges.Clear(); _reserveButtons.Clear();
            _picker.Reset(_draft);
        }

        private bool OrderEditable
            => _c.Store.Public?.phase == Protocol.Investment && !_c.Store.Public.paused && !_c.Store.OrderLocked
               && !_c.Store.HasPending(CommandTypes.SubmitInvestment);

        private void BuildOrderForm()
        {
            _orderCharts.Clear();
            _chartTips.Clear();
            _holdLabels.Clear();
            EnsureDraft();
            if (_draft == null)
            {
                Message(_body, "선택한 기업이 2개가 아닙니다. 서버의 자동 선택 결과를 기다리는 중입니다.");
                return;
            }
            _picker.Editable = OrderEditable;

            SyncDraftReservations();

            // 비율·순서
            var top = UiKit.Rect(_body, "RatioRow");
            UiKit.Place(top, new Vector2(0, 1), new Vector2(0, 1), new Vector2(6, -2), new Vector2(880, 36));
            var ratioLabel = UiKit.Label(top, "투자 비율 (첫 기업 : 둘째 기업)", 14, Palette.Text, TextAnchor.MiddleLeft);
            UiKit.Place(ratioLabel.rectTransform, new Vector2(0, 0.5f), new Vector2(0, 0.5f), Vector2.zero, new Vector2(230, 32));
            int x = 236;
            foreach (int w in UiRules.FirstCompanyWeightOptionsBps)
            {
                int weight = w;
                bool on = _draft.FirstWeightBps == w;
                var b = UiKit.Button(top, (w / 1000) + ":" + ((10000 - w) / 1000), 15,
                    on ? Palette.Accent : Palette.Card, on ? Color.white : Palette.Text,
                    () => { if (OrderEditable) { _draft.SetFirstWeight(weight); Refresh(); } });
                UiKit.Place((RectTransform)b.transform, new Vector2(0, 0.5f), new Vector2(0, 0.5f), new Vector2(x, 0), new Vector2(64, 32));
                x += 70;
            }
            var swap = UiKit.Button(top, "순서 바꾸기", 13, Palette.Card, Palette.Text, () => { if (OrderEditable) { _draft.SwapCompanies(); Refresh(); } });
            UiKit.Place((RectTransform)swap.transform, new Vector2(0, 0.5f), new Vector2(0, 0.5f), new Vector2(x + 10, 0), new Vector2(120, 32));

            // 두 기업 열
            var cols = UiKit.Rect(_body, "Columns");
            UiKit.Stretch(cols, 0, 44, 0, 56);
            BuildCompanyColumn(cols, 0, _draft.FirstCompanyId, _draft.FirstWeightBps);
            BuildCompanyColumn(cols, 1, _draft.SecondCompanyId, _draft.SecondWeightBps);

            // 하단: 안내/오류 + 확정
            bool pending = _c.Store.HasPending(CommandTypes.SubmitInvestment);
            var bottom = UiKit.Rect(_body, "SubmitRow");
            bottom.anchorMin = new Vector2(0, 0);
            bottom.anchorMax = new Vector2(1, 0);
            bottom.pivot = new Vector2(0.5f, 0);
            bottom.offsetMin = new Vector2(6, 4);
            bottom.offsetMax = new Vector2(-6, 48);

            _orderMessage = UiKit.Label(bottom, "", 13, Palette.SubText, TextAnchor.MiddleLeft);
            UiKit.Stretch(_orderMessage.rectTransform, 0, 0, 200, 0);

            _submitButton = UiKit.Button(bottom, pending ? "확정 중…" : "주문 확정", 16, Palette.Disabled, Palette.DisabledText, SubmitDraft);
            _submitButton.name = "SubmitOrder";
            UiKit.Place((RectTransform)_submitButton.transform, new Vector2(1, 0.5f), new Vector2(1, 0.5f), Vector2.zero, new Vector2(180, 42));
            UpdateOrderLive();
        }

        private void SubmitDraft()
        {
            if (!OrderEditable || _draft == null || _draft.Validate().Length > 0) return;
            _c.SubmitInvestment(_draft.ToInvestments()); // 주문 확정 때만 기존 SUBMIT_INVESTMENT 1회
        }

        private void BuildCompanyColumn(RectTransform parent, int index, string companyId, int weightBps)
        {
            var entry = CompanyCatalog.Get(companyId);
            var cell = UiKit.Rect(parent, "Col_" + companyId);
            UiKit.Anchor(cell, index * 0.5f, 0, index * 0.5f + 0.5f, 1, 6, 0);
            var bg = UiKit.Panel(cell, "Bg", Palette.Card, rounded: true);
            UiKit.Stretch(bg.rectTransform);
            var root = bg.rectTransform;

            var logo = CompanyBadge.Create(root, companyId, 26);
            UiKit.Place(logo, new Vector2(0, 1), new Vector2(0, 1), new Vector2(10, -6), new Vector2(26, 26));
            var header = UiKit.Label(root, (index + 1) + ". <b>" + entry.DisplayName + "</b> <size=11>" + entry.Id + "</size>   <color=#2563EB><b>"
                + DisplayFormat.Weight(weightBps) + "</b></color>   <size=11>" + (_c.Store.TradePlans.Get(companyId).Favorite ? "★ 찜 · " : "")
                + (_c.Store.TradePlans.Get(companyId).Rank > 0 ? _c.Store.TradePlans.Get(companyId).Rank + "순위" : entry.Volatility) + "</size>", 16, Palette.Text, TextAnchor.MiddleLeft);
            UiKit.Place(header.rectTransform, new Vector2(0, 1), new Vector2(0, 1), new Vector2(42, -4), new Vector2(380, 30));

            // 일봉 그래프 = 날짜 클릭 영역(값이 없는 날짜도 클릭 가능)
            var chartRt = UiKit.Rect(root, "Chart");
            chartRt.anchorMin = new Vector2(0, 1);
            chartRt.anchorMax = new Vector2(1, 1);
            chartRt.pivot = new Vector2(0.5f, 1);
            chartRt.offsetMin = new Vector2(14, -150);
            chartRt.offsetMax = new Vector2(-14, -38);
            var chart = chartRt.gameObject.AddComponent<BarChartGraphic>();
            var pointer = chartRt.gameObject.AddComponent<ChartPointerLayer>();
            _orderCharts[companyId] = chart;
            foreach (int day in new[] { 0, 5, 10, 15, 20, 25, 30 })
            {
                var tick = UiKit.Label(chartRt, day.ToString(), 9, Palette.SubText, TextAnchor.UpperCenter);
                UiKit.Place(tick.rectTransform, new Vector2((day + 0.5f) / DayAxis.Count, 0), new Vector2(0.5f, 1), new Vector2(0, -1), new Vector2(30, 14));
            }

            var tip = UiKit.Label(root, "", 12, Palette.SubText, TextAnchor.MiddleLeft);
            tip.rectTransform.anchorMin = new Vector2(0, 1);
            tip.rectTransform.anchorMax = new Vector2(1, 1);
            tip.rectTransform.pivot = new Vector2(0.5f, 1);
            tip.rectTransform.offsetMin = new Vector2(14, -172);
            tip.rectTransform.offsetMax = new Vector2(-110, -154);
            _chartTips[companyId] = tip;

            pointer.Hovered = day => OnChartHover(companyId, day);
            pointer.Clicked = (day, right) => OnChartClick(companyId, day, right);
            if (_c.Store.Public?.windowInformation == true)
            {
                if (!_mouseRanges.TryGetValue(companyId, out var selection)) _mouseRanges.Add(companyId, selection = new ChartRangeSelection());
                pointer.RangeSelection = selection; pointer.CanEditRange = () => OrderEditable;
                pointer.Intervals = () => _draft.IntervalsOf(companyId).ToArray();
                pointer.EditInterval = (rowIndex, a, b) => {
                    string error = _draft.TryReplaceInterval(companyId, rowIndex, a, b);
                    if (error == "") { Refresh(); EventSystem.current?.SetSelectedGameObject(_orderCharts[companyId].gameObject); } return error;
                };
                pointer.DeleteInterval = rowIndex => { if (OrderEditable) { _draft.RemoveInterval(companyId, rowIndex); Refresh(); } };
                pointer.RangeChanged = () => UpdateMouseOrderRange(companyId);
                var reserve = UiKit.Button(root, "예약하기", 11, Palette.AccentSoft, Palette.Accent, () => SaveMouseOrderRange(companyId));
                reserve.name = "ReserveOrderRange_" + companyId; _reserveButtons[companyId] = reserve;
                UiKit.Place((RectTransform)reserve.transform, Vector2.one, Vector2.one, new Vector2(-14, -154), new Vector2(90, 22));
                UpdateMouseOrderRange(companyId);
            }

            // 진행 중 후보 취소 버튼(우클릭과 같은 기능)
            var candidateDay = _picker.CandidateOf(companyId);
            if (candidateDay.HasValue)
            {
                var cancel = UiKit.Button(root, "후보 취소", 11, Palette.Card, Palette.Text, () => { _picker.Cancel(companyId); Refresh(); });
                cancel.name = "CancelEntry_" + companyId;
                UiKit.Place((RectTransform)cancel.transform, new Vector2(1, 1), new Vector2(1, 1), new Vector2(-14, -154), new Vector2(90, 20));
            }

            // 완성 구간 행(숫자 입력은 보조 수단으로 유지, 삭제 후 다시 클릭 가능)
            var intervals = _draft.IntervalsOf(companyId);
            var viewport = UiKit.Rect(root, "TradeIntervalScroll");
            UiKit.Stretch(viewport, 0, 178, 0, 4);
            var rows = UiKit.ScrollContent(viewport, "TradeIntervalRows");
            float y = 0;
            for (int i = 0; i < intervals.Count; i++)
            {
                BuildIntervalRow(rows, companyId, i, intervals[i], y);
                y -= 40;
            }
            if (_draft.EntryAtOpen || intervals.Count < UiRules.MaxIntervalsPerCompany)
            {
                string guide = candidateDay.HasValue
                    ? "<color=#B45309>진입 " + candidateDay.Value + "일 선택됨 → 청산일을 클릭하세요</color>"
                    : _c.Store.Public?.windowInformation == true ? "드래그 → 예약하기 · 양 끝을 끌어 조절 / 몸통을 끌어 이동 · 선택 후 Del / Backspace로 삭제"
                    : "그래프에서 <b>진입일 → 청산일</b> 순서로 클릭 (보유 5~10일, 최대 2구간) · <color=#059669>초록</color> = 지금 고를 수 있는 날짜, 회색(미열람)도 선택 가능";
                var hint = UiKit.Label(rows, guide, 12, Palette.SubText, TextAnchor.MiddleLeft);
                UiKit.Place(hint.rectTransform, new Vector2(0, 1), new Vector2(0, 1), new Vector2(14, y - 4), new Vector2(400, 26));
            }
            rows.sizeDelta = new Vector2(0, -y + 66);
            DrawOrderChart(chart, companyId);
        }

        private void OnChartHover(string companyId, int? day)
        {
            if (!_chartTips.TryGetValue(companyId, out var tip) || tip == null) return;
            _orderCharts.TryGetValue(companyId, out var chart);
            var bars = DailyBarModel.Build(_c.Store.View?.companies?.FirstOrDefault(c => c.companyId == companyId));
            var entry = _picker.CandidateOf(companyId);
            if (!day.HasValue)
            {
                tip.text = "";
                if (chart != null) chart.SetPreview(null, false);
                return;
            }
            string text = DailyBarModel.Tooltip(bars[day.Value]);
            var allowed = _picker.AllowedRange(companyId);
            bool inAllowed = allowed.HasValue && day.Value >= allowed.Value.x && day.Value <= allowed.Value.y;
            if (!entry.HasValue && OrderEditable)
                text += inAllowed ? "  <color=#059669>· 클릭 = 진입일</color>" : "  <color=#9CA3AF>· 진입 불가(30일까지 5일 보유 안 됨)</color>";
            if (entry.HasValue && day.Value > entry.Value)
            {
                bool ok = _picker.PreviewIsValid(companyId, day.Value);
                var ret = DailyBarModel.IntervalReturnBps(bars, entry.Value, day.Value, _c.Store.View?.entryAtOpen == true);
                text = entry.Value + "일 → " + day.Value + "일 · 보유 " + (day.Value - entry.Value) + "일"
                       + (ret.HasValue ? " · 알려진 값 기준 구간 " + DisplayFormat.Percent(ret.Value) + "p" : " · 끝점 미열람")
                       + (ok ? "" : "  <color=#DC2626>(불가)</color>");
                if (chart != null) chart.SetPreview(new Vector2Int(entry.Value, day.Value), ok);
            }
            else if (chart != null) chart.SetPreview(null, false);
            tip.text = text;
        }

        private void OnChartClick(string companyId, int day, bool right)
        {
            _picker.Editable = OrderEditable;
            var result = right ? _picker.Cancel(companyId) : _picker.Click(companyId, day);
            if (result.Changed)
            {
                Refresh(); // 구간 행 수가 바뀌므로 다시 그림(초안은 이 객체에 남아 있음)
                if (_orderMessage != null && result.Outcome != PickOutcome.IntervalAdded) _orderMessage.text = result.Message;
                return;
            }
            if (_orderMessage != null && !string.IsNullOrEmpty(result.Message))
            {
                _orderMessage.text = result.Message;
                _orderMessage.color = Palette.Up;
            }
            // 거절 이유를 그래프 바로 아래에도 보여 준다(클릭이 무시된 것처럼 보이지 않게)
            if (!string.IsNullOrEmpty(result.Message) && _chartTips.TryGetValue(companyId, out var tip) && tip != null)
                tip.text = "<color=#DC2626>" + day + "일: " + result.Message + "</color>";
        }

        private void BuildIntervalRow(RectTransform root, string companyId, int index, Interval iv, float y)
        {
            var row = UiKit.Rect(root, "Interval" + index);
            row.anchorMin = new Vector2(0, 1);
            row.anchorMax = new Vector2(1, 1);
            row.pivot = new Vector2(0.5f, 1);
            row.offsetMin = new Vector2(14, y - 34);
            row.offsetMax = new Vector2(-14, y);
            if (_c.Store.Public?.windowInformation == true)
            {
                var select = UiKit.Button(row, (index + 1) + ". " + iv.entryDay + "일 시가 → " + iv.exitDay + "일 종가", 12,
                    Palette.AccentSoft, Palette.Accent, () => {
                        _mouseRanges[companyId].SelectInterval(index, iv.entryDay, iv.exitDay);
                        EventSystem.current?.SetSelectedGameObject(_orderCharts[companyId].gameObject); UpdateMouseOrderRange(companyId);
                    });
                UiKit.Stretch((RectTransform)select.transform, 0, 0, 78, 0); select.interactable = OrderEditable;
                var delete = UiKit.Button(row, "삭제", 12, Palette.Card, Palette.SubText, () => {
                    if (!OrderEditable) return; _mouseRanges[companyId].Clear(); _draft.RemoveInterval(companyId, index); Refresh(); });
                delete.name = "RemoveInterval_" + companyId + "_" + index;
                UiKit.Place((RectTransform)delete.transform, new Vector2(1,.5f), new Vector2(1,.5f), Vector2.zero, new Vector2(70,30));
                delete.interactable = OrderEditable; return;
            }

            var label = UiKit.Label(row, "구간 " + (index + 1), 13, Palette.SubText, TextAnchor.MiddleLeft);
            UiKit.Place(label.rectTransform, new Vector2(0, 0.5f), new Vector2(0, 0.5f), Vector2.zero, new Vector2(48, 34));

            DayInput(row, 56, "진입", () => iv.entryDay, v => iv.entryDay = v);
            DayInput(row, 190, "청산", () => iv.exitDay, v => iv.exitDay = v);

            var hold = UiKit.Label(row, "", 13, Palette.SubText, TextAnchor.MiddleLeft);
            UiKit.Place(hold.rectTransform, new Vector2(0, 0.5f), new Vector2(0, 0.5f), new Vector2(330, 0), new Vector2(90, 34));
            _holdLabels.Add(new KeyValuePair<Interval, Text>(iv, hold));
            UpdateHoldLabel(iv, hold);

            var remove = UiKit.Button(row, "삭제", 12, Palette.Card, Palette.SubText, () =>
            {
                if (_picker.Remove(companyId, index)) Refresh();
            });
            remove.name = "RemoveInterval_" + companyId + "_" + index;
            UiKit.Place((RectTransform)remove.transform, new Vector2(1, 0.5f), new Vector2(1, 0.5f), Vector2.zero, new Vector2(48, 30));
            remove.interactable = OrderEditable;
        }

        /// <summary>날짜 직접 입력 칸(보조 수단, 0~30 정수). 입력할 때마다 초안에 반영하고 검사 결과만 바로 갱신한다.</summary>
        private void DayInput(RectTransform row, float x, string name, System.Func<int> get, System.Action<int> set)
        {
            var label = UiKit.Label(row, name, 13, Palette.Text, TextAnchor.MiddleLeft);
            UiKit.Place(label.rectTransform, new Vector2(0, 0.5f), new Vector2(0, 0.5f), new Vector2(x, 0), new Vector2(34, 34));

            var field = UiKit.NumberInput(row, get().ToString(), 15);
            UiKit.Place((RectTransform)field.transform, new Vector2(0, 0.5f), new Vector2(0, 0.5f), new Vector2(x + 36, 0), new Vector2(54, 30));
            field.interactable = OrderEditable;
            field.onValueChanged.AddListener(text =>
            {
                if (!OrderEditable || !int.TryParse(text, out var value)) return;
                set(value);
                UpdateOrderLive();
            });
            field.onEndEdit.AddListener(_ => field.text = get().ToString()); // 비우거나 잘못 입력하면 마지막 값으로 되돌림

            var unit = UiKit.Label(row, "일", 13, Palette.SubText, TextAnchor.MiddleLeft);
            UiKit.Place(unit.rectTransform, new Vector2(0, 0.5f), new Vector2(0, 0.5f), new Vector2(x + 94, 0), new Vector2(20, 34));
        }

        private void UpdateOrderLive()
        {
            if (_draft == null || _orderMessage == null || _submitButton == null) return;
            _picker.Editable = OrderEditable;
            string error = _draft.Validate();
            SyncDraftReservations();
            bool pending = _c.Store.HasPending(CommandTypes.SubmitInvestment);
            bool can = error.Length == 0 && OrderEditable;

            bool empty = _draft.FirstIntervals.Count == 0 || _draft.SecondIntervals.Count == 0;
            _orderMessage.text = empty ? "예약 구간과 비중은 마감 때 자동 적용됩니다. 구간이 없는 기업만 0→5일로 투자합니다."
                : error.Length > 0 ? ErrorMessages.ToKorean(error)
                : "예약·비중은 마감 때 자동 적용됩니다. 지금 확정하면 편집이 잠깁니다.";
            _orderMessage.color = error.Length > 0 && !empty ? Palette.Up : Palette.SubText;
            UiKit.SetButton(_submitButton, pending ? "확정 중…" : "주문 확정", can,
                can ? Palette.Accent : Palette.Disabled, can ? Color.white : Palette.DisabledText);

            foreach (var pair in _holdLabels) UpdateHoldLabel(pair.Key, pair.Value);
            foreach (var pair in _orderCharts) DrawOrderChart(pair.Value, pair.Key);
        }

        private void SyncDraftReservations()
        {
            if (_draft == null || !OrderEditable || _c.Store.Public?.windowInformation != true) return;
            var investments = _draft.ToInvestments();
            if (investments.Any(i => i.intervals.Length > 0 && OrderInputValidator.ValidateIntervals(i.intervals, _draft.EntryAtOpen) != "")) return;
            _c.Store.TradePlans.SetDraft(investments);
        }

        private static void UpdateHoldLabel(Interval iv, Text hold)
        {
            int holding = iv.exitDay - iv.entryDay;
            bool ok = holding >= UiRules.MinHoldingDays && holding <= UiRules.MaxHoldingDays;
            hold.text = "보유 " + holding + "일";
            hold.color = ok ? Palette.SubText : Palette.Up;
        }

        private string MouseOrderRangeError(string company)
        {
            var range = _mouseRanges[company];
            var list = _draft.IntervalsOf(company).Select(DtoCopy.Copy).ToList();
            var candidate = new Interval { entryDay = range.Start, exitDay = range.End };
            if (range.SelectedIndex >= 0 && range.SelectedIndex < list.Count) list[range.SelectedIndex] = candidate; else list.Add(candidate);
            return OrderInputValidator.ValidateIntervals(list, _draft.EntryAtOpen);
        }
        private void SaveMouseOrderRange(string company)
        {
            if (!OrderEditable || !_mouseRanges.TryGetValue(company, out var range) || !range.HasSelection || MouseOrderRangeError(company) != "") return;
            int index = range.SelectedIndex;
            string error;
            if (index >= 0) error = _draft.TryReplaceInterval(company, index, range.Start, range.End);
            else { index = _draft.IntervalsOf(company).Count; error = _draft.TryAddInterval(company, range.Start, range.End); }
            if (error != "") return;
            range.SelectInterval(index, range.Start, range.End); Refresh();
            EventSystem.current?.SetSelectedGameObject(_orderCharts[company].gameObject);
        }
        private void UpdateMouseOrderRange(string company)
        {
            if (!_mouseRanges.TryGetValue(company, out var range) || !_orderCharts.TryGetValue(company, out var chart) || chart == null) return;
            chart.SetInformationRange(range.HasSelection ? new Vector2Int(range.Start, range.End) : (Vector2Int?)null);
            bool valid = range.HasSelection && MouseOrderRangeError(company) == "";
            if (_reserveButtons.TryGetValue(company, out var reserve) && reserve != null)
                reserve.interactable = OrderEditable && valid;
            if (_chartTips.TryGetValue(company, out var tip) && tip != null && range.HasSelection)
                tip.text = range.Start + "일 시가 → " + range.End + "일 종가 · " + (valid ? "예약 가능" : "날짜 / 겹침 확인");
        }
        private void DrawOrderChart(BarChartGraphic chart, string companyId)
        {
            if (chart == null || _draft == null) return;
            var company = _c.Store.View?.companies?.FirstOrDefault(c => c.companyId == companyId);
            chart.SetData(DailyBarModel.Build(company));
            chart.SetTradeIntervals(_draft.IntervalsOf(companyId));
            if (_c.Store.Public?.windowInformation == true) { UpdateMouseOrderRange(companyId); return; }
            chart.SetCandidate(_picker.CandidateOf(companyId));
            chart.SetAllowed(OrderEditable ? _picker.AllowedRange(companyId) : null);
        }

        // ------------------------------------------------------------------
        // 확정 주문·결과
        // ------------------------------------------------------------------

        private void BuildOrderSummary(string message)
        {
            var text = UiKit.Label(_body, message, 15, Palette.Text, TextAnchor.UpperLeft);
            UiKit.Stretch(text.rectTransform, 10, 6, 10, 0);

            var card = UiKit.Panel(_body, "Order", Palette.Card, rounded: true);
            UiKit.Stretch(card.rectTransform, 6, 44, 6, 20);
            var lines = OrderLines();
            var body = UiKit.Label(card.transform, lines, 17, Palette.Text, TextAnchor.UpperLeft);
            UiKit.Stretch(body.rectTransform, 20, 16, 20, 16);
        }

        private string OrderLines()
        {
            var inv = _c.Store.View?.ownInvestments;
            if (inv == null || inv.Length == 0) return "확정된 주문이 없습니다.";
            var sb = new System.Text.StringBuilder("<b>내 확정 주문</b>\n\n");
            for (int i = 0; i < inv.Length; i++)
            {
                var o = inv[i];
                sb.Append(i + 1).Append(". <b>").Append(o.companyId).Append("</b>  ").Append(DisplayFormat.Weight(o.weightBps)).Append("   ");
                sb.Append(string.Join(",  ", (o.intervals ?? new Interval[0]).Select(v => v.entryDay + "일 → " + v.exitDay + "일")));
                sb.Append('\n');
            }
            return sb.ToString();
        }

        private void BuildResult()
        {
            var board = _c.Store.Scoreboard;
            if (board == null || board.turn != _c.Store.CurrentTurn)
            {
                Message(_body, "정산 결과를 기다리는 중입니다.");
                return;
            }

            var card = UiKit.Panel(_body, "Result", Palette.Card, rounded: true);
            UiKit.Stretch(card.rectTransform, 6, 6, 6, 6);
            var sb = new System.Text.StringBuilder("<b>" + board.turn + "턴 결과</b>\n\n");
            sb.Append("<size=13><color=#6B7280>순위      플레이어      총자산          이번 턴          누적 손익</color></size>\n");
            foreach (var r in board.rows.OrderBy(r => r.rank).ThenBy(r => r.playerId))
            {
                bool me = r.playerId == _c.LocalPlayerId;
                string color = ColorUtility.ToHtmlStringRGB(Palette.ForBps(r.turnReturnBps));
                sb.Append(me ? "<b>" : "")
                  .Append(r.rank).Append("등        ")
                  .Append(r.playerId).Append(me ? " (나)" : "      ").Append("        ")
                  .Append(DisplayFormat.Money(r.totalAssets)).Append("        ")
                  .Append("<color=#").Append(color).Append(">").Append(DisplayFormat.Percent(r.turnReturnBps)).Append("</color>        ")
                  .Append(DisplayFormat.SignedMoney(r.cumulativeProfit))
                  .Append(me ? "</b>" : "").Append('\n');
            }
            sb.Append("\n").Append(OrderLines());
            var text = UiKit.Label(card.transform, sb.ToString(), 17, Palette.Text, TextAnchor.UpperLeft);
            UiKit.Stretch(text.rectTransform, 24, 18, 24, 18);
        }

        private static void Message(RectTransform parent, string message)
        {
            var t = UiKit.Label(parent, message, 16, Palette.SubText);
            UiKit.Stretch(t.rectTransform, 20, 20, 20, 20);
        }
    }

    public static class PhaseNames
    {
        public static string ForState(PublicState state) => state?.stockSystem == true && state.phase == Protocol.Selection ? "주식 경쟁" : Korean(state?.phase);
        public static string Korean(string phase)
        {
            switch (phase)
            {
                case Protocol.Lobby: return "대기실";
                case Protocol.Ended: return "종료";
                case "AUCTION": return "입찰";
                case Protocol.AbilitySelection: return "능력 선택";
                case StockRules.AugmentPhase: return "증강 선택";
                case Protocol.Info: return "정보 탐색";
                case Protocol.Selection: return "기업 선택";
                case Protocol.Investment: return "투자 결정";
                case Protocol.Reveal: return "시장 공개";
                case Protocol.Result: return "결과";
                default: return phase ?? "";
            }
        }
    }
}
