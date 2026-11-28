using System;
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
    public sealed class StockSystemPage
    {
        private sealed class Card
        {
            public Text Heading, Owners, Position;
            public int Quantity = 1;
            public Button[] Actions;
            public BarChartGraphic Chart;
            public RectTransform Inventory, SliderRoot;
            public Slider Slider;
            public Image[] Units;
            public Text Count, Stamp;          // 손잡이 위 '내 n주', 매진 도장
            public Color[] LastColors;         // 직전 칸 색(새로 채워진 칸만 튀어 오르게)
            public bool WasSoldOut;
            public Image[] BankUnits;          // BANK: 내 30칸 중 자동 예금 칸
            public Text BankLabel;
            public Outline Frame;              // (사용 안 함) 예전 매매 가능 테두리
            public Image TradableDot;          // 실시간 매매: 매매 가능하면 카드 왼쪽 위 흰 점
            public RectTransform MineBadge;    // '내 n주 · 매매 가능'
            public Text MineBadgeText;
            public Image Shade;                // 미보유 기업 흐린 덮개
        }
        // 연출(D): 현재 차례 시간 막대·긴박감·내 차례 알림
        private Image currentBar;
        private UrgencyEdges urgency;
        private RectTransform currentSlot;
        private Text currentSlotText;
        private int lastPulseSecond = -1;
        private bool myTurnNow;
        /// <summary>현재 차례 남은 시간(소수). TabletView의 서버 시계에서 받는다. 표시 연출에만 쓴다.</summary>
        public Func<double> RemainingExact;
        private readonly RectTransform root;
        private readonly GameClientController controller;
        private readonly Dictionary<string, Card> cards = new Dictionary<string, Card>();
        private readonly Dictionary<string, int> targets = new Dictionary<string, int>();
        private string builtKey, draggingCompany, focus = "AI";
        private Text summary;
        private Button finish;
        public Action<string> OpenPlan;
        public StockSystemPage(RectTransform parent, GameClientController controller) { root = parent; this.controller = controller; }
        private PublicState State => controller.Store.Public;
        private PortfolioState Portfolio => controller.Store.View?.portfolio;
        /// <summary>내가 매매하는 계좌. [실험] 교환 턴에는 조종 중인 다음 자리 사람의 계좌.</summary>
        private string Me => SwapRules.ActingAccount(State, controller.LocalPlayerId);
        private string BuildKey => State.turn + ":" + State.phase + ":" + State.selectionIndex + ":" + State.currentPlayerId + ":" + State.developerRevision;
        private bool Editable => controller.CanDraftShares();
        private int Holding(string id) => State.shareMarket?.FirstOrDefault(c => c.companyId == id)?.owners?.Where(v => v.playerId == Me)
            .Sum(v => v.shares + v.reserved)
            ?? Portfolio?.holdings?.FirstOrDefault(v => v.companyId == id)?.shares ?? 0;
        public bool TryRefresh()
        {
            if (!controller.HasCurrentPlayerView || Portfolio == null || State.phase == StockRules.AugmentPhase || builtKey != BuildKey || cards.Count == 0) return false;
            RefreshCards(); return true;
        }
        public void Build()
        {
            cards.Clear(); targets.Clear(); draggingCompany = null; builtKey = BuildKey;
            if (!controller.HasCurrentPlayerView || Portfolio == null) { Label(root, "이번 턴의 주식·증강 정보를 기다리고 있습니다.", 0, 80); return; }
            if (State.phase == StockRules.AugmentPhase) { BuildAugments(); return; }
            foreach (string id in UiRules.CompanyIds) targets[id] = Holding(id);
            if (State.phase == Protocol.Selection) BuildOrderStrip();
            summary = Label(root, "", State.phase == Protocol.Investment ? 2 : 49, 29, 13); summary.name = "StockPurchaseSummary";
            var grid = UiKit.Rect(root, "ShareMarketGrid"); UiKit.Stretch(grid, 0, State.phase == Protocol.Investment ? 34 : 82, 0, State.phase == Protocol.Investment ? 2 : 44);
            for (int i = 0; i < UiRules.CompanyIds.Length; i++)
            {
                string id = UiRules.CompanyIds[i];
                var cell = UiKit.Rect(grid, "ShareCard_" + id);
                UiKit.Anchor(cell, i % 3 / 3f, i < 3 ? .5f : 0, (i % 3 + 1) / 3f, i < 3 ? 1 : .5f, 4, 4);
                BuildCompany(cell, id);
            }
            if (State.phase == Protocol.Selection)
            {
                finish = UiKit.Button(root, "매수 완료 · 다음 차례", 14, Palette.Accent, Color.white, () => controller.PassStockDraft());
                finish.name = "PassShareDraft";
                UiKit.Place((RectTransform)finish.transform, new Vector2(1, 0), new Vector2(1, 0), new Vector2(-4, 3), new Vector2(190, 34));
                if (Portfolio.augments.Contains("PRIORITY") && !Portfolio.priorityUsed)
                {
                    var priority = UiKit.Button(root, "선점 사용", 12, Palette.AccentSoft, Palette.Accent, () => controller.UseDraftPriority());
                    priority.name = "UseDraftPriority";
                    UiKit.Place((RectTransform)priority.transform, new Vector2(0, 0), new Vector2(0, 0), new Vector2(4, 3), new Vector2(110, 34));
                    int next = Array.FindIndex(State.draftOrder ?? Array.Empty<string>(), Math.Min(State.selectionIndex + 1, State.draftOrder?.Length ?? 0), id => id == controller.LocalPlayerId);
                    priority.interactable = !State.paused && next > State.selectionIndex + 1 && !controller.Store.HasPending(StockRules.Priority);
                }
            }
            else finish = null;
            RefreshCards();
            BuildEffects();
        }

        /// <summary>내 차례가 되면 배너가 올라왔다 사라지고, 마지막 3초에는 매수 화면 가장자리만 붉게 깜박인다.</summary>
        private void BuildEffects()
        {
            myTurnNow = State.phase == Protocol.Selection && State.currentPlayerId == controller.LocalPlayerId;
            lastPulseSecond = -1;
            urgency = null;
            if (State.phase != Protocol.Selection) return;

            // 주변 테두리 효과(마지막 3초 붉은 가장자리)는 쓰지 않음

            // 내 차례 알림도 가운데 큰 네모 배너 대신, 차례 줄 왼쪽 위의 주황 원으로만 표시한다.
        }

        /// <summary>매 프레임(선택 단계). 현재 차례 시간 막대를 줄이고, 3초 이하면 깜박임·초 단위 강조.</summary>
        public void Tick()
        {
            if (currentBar == null && urgency == null) return;
            if (State == null || State.phase != Protocol.Selection || State.developerMode || State.paused)
            {
                if (urgency != null) { urgency.SetLevel(0f); urgency.Update(); }
                return;
            }
            double remain = RemainingExact?.Invoke() ?? 0;
            float ratio = Mathf.Clamp01((float)(remain / StockRules.DraftSeconds));
            if (currentBar != null)
            {
                currentBar.rectTransform.anchorMax = new Vector2(ratio, 0);
                currentBar.color = ratio > .5f ? new Color32(0x10, 0xB9, 0x81, 0xFF) : ratio > .25f ? new Color32(0xBF, 0xBF, 0xBF, 0xFF) : new Color32(0xFF, 0xFF, 0xFF, 0xFF); // 초록 → 회색 → 흰색
            }
            int sec = Mathf.CeilToInt((float)remain);
            bool hurry = remain > 0 && sec <= 3;
            if (hurry && sec != lastPulseSecond)
            {
                lastPulseSecond = sec;
                if (currentSlot != null) UiAnim.Pulse(currentSlot, 0f, 1.06f);
                if (myTurnNow && summary != null) UiAnim.Pulse(summary.rectTransform, 0f, 1.12f);
            }
            if (currentSlotText != null && currentSlotText.text.Contains("·"))
            {
                string head = currentSlotText.text.Split('<')[0];
                currentSlotText.text = head + "<size=12>" + (State.selectionIndex / 4 + 1) + "차 · " + (hurry ? "<color=#FFFFFF><b>" + sec + "초</b></color>" : sec + "초") + " · 지금</size>";
            }
            if (urgency != null)
            {
                urgency.SetLevel(hurry && myTurnNow ? 1f : 0f);
                urgency.Update();
            }
        }
        private void BuildOrderStrip()
        {
            var ids = State.draftOrder ?? Array.Empty<string>();
            currentBar = null; currentSlot = null; currentSlotText = null;
            int first = Math.Min(StockRules.DraftRounds - 1, State.selectionIndex / 4) * 4;
            for (int i = first; i < Math.Min(ids.Length, first + 4); i++)
            {
                bool current = i == State.selectionIndex && State.phase == Protocol.Selection;
                var panel = UiKit.Panel(root, "StockDraftSlot_" + i, Palette.Card, rounded: true);
                panel.rectTransform.anchorMin = new Vector2((i - first) / 4f, 1); panel.rectTransform.anchorMax = new Vector2((i - first + 1) / 4f, 1);
                panel.rectTransform.offsetMin = new Vector2(3, -45); panel.rectTransform.offsetMax = new Vector2(-3, 0);
                var text = UiKit.Label(panel.transform, ids[i] + "  <size=12>" + (i / 4 + 1) + (State.developerMode ? "차 · 완료 시 진행" : "차 · 7초") + (current ? " · 지금" : "") + "</size>", 22, Palette.Player(ids[i]), TextAnchor.MiddleCenter, FontStyle.Bold);
                text.name = "StockDraftPlayer_" + i; UiKit.Stretch(text.rectTransform, 4, 1, 4, 1);
                if (current)
                {
                    // 현재 차례: 큰 네모 테두리 대신 왼쪽 위 주황 원 + 남은 시간 막대(초록→주황→빨강)
                    var dot = UiKit.Panel(panel.transform, "CurrentTurnDot", Palette.Accent);
                    dot.sprite = UiKit.Pill; dot.type = Image.Type.Simple; dot.preserveAspect = true;
                    UiKit.Place(dot.rectTransform, new Vector2(0, 1), new Vector2(0, 1), new Vector2(8, -7), new Vector2(12, 12));
                    var track = UiKit.Panel(panel.transform, "DraftTimerTrack", Palette.Disabled, rounded: true);
                    track.rectTransform.anchorMin = Vector2.zero; track.rectTransform.anchorMax = new Vector2(1, 0);
                    track.rectTransform.offsetMin = new Vector2(8, 3); track.rectTransform.offsetMax = new Vector2(-8, 7);
                    currentBar = UiKit.Panel(track.transform, "DraftTimerBar", new Color32(0x10, 0xB9, 0x81, 0xFF), rounded: true);
                    currentBar.rectTransform.anchorMin = Vector2.zero; currentBar.rectTransform.anchorMax = Vector2.one;
                    currentBar.rectTransform.offsetMin = Vector2.zero; currentBar.rectTransform.offsetMax = Vector2.zero;
                    track.gameObject.SetActive(!State.developerMode);
                    currentSlot = dot.rectTransform; currentSlotText = text; // 마지막 몇 초 깜박임도 주황 원만
                    UiAnim.Pulse(dot.rectTransform, 0f, 1.6f);
                }
            }
        }
        private void BuildCompany(RectTransform cell, string id)
        {
            var panel = UiKit.Panel(cell, "ShareCardBackground", Palette.Card, rounded: true); UiKit.Stretch(panel.rectTransform);
            var r = panel.rectTransform;
            var card = new Card(); cards[id] = card;
            // 1줄: 기업명·수량(왼쪽) | 내 보유 배지 · 확대(오른쪽)   2줄: 플레이어별 보유   3줄~: 차트
            // (예전에는 제목이 가운데 버튼 안에 있어 아래 줄 보유 표시와 겹쳐 보였음)
            card.Heading = Label(r, "", 5, 22, 14); card.Heading.name = "ShareHeading_" + id;
            card.Heading.rectTransform.offsetMin = new Vector2(10, card.Heading.rectTransform.offsetMin.y);
            card.Heading.rectTransform.offsetMax = new Vector2(-150, card.Heading.rectTransform.offsetMax.y);
            card.Heading.horizontalOverflow = HorizontalWrapMode.Overflow; // 리치 텍스트는 best-fit이 작게 잡혀 글자가 줄어들어서 고정 크기
            var expand = UiKit.Button(r, "확대 ↗", 11, Palette.AccentSoft, Palette.Accent, () => OpenPlan?.Invoke(id));
            expand.name = "ExpandShare_" + id;
            UiKit.Place((RectTransform)expand.transform, Vector2.one, Vector2.one, new Vector2(-6, -5), new Vector2(58, 22));
            var badge = UiKit.Panel(r, "MineBadge_" + id, Palette.Accent, rounded: true);
            card.MineBadge = badge.rectTransform;
            UiKit.Place(card.MineBadge, Vector2.one, Vector2.one, new Vector2(-70, -5), new Vector2(74, 22));
            card.MineBadgeText = UiKit.Label(badge.transform, "", 11, Palette.OnAccent, TextAnchor.MiddleCenter, FontStyle.Bold);
            UiKit.Stretch(card.MineBadgeText.rectTransform);
            card.MineBadge.gameObject.SetActive(false);
            card.Owners = Label(r, "", 30, 18, 12); card.Owners.name = "ShareOwners_" + id;
            card.Owners.rectTransform.offsetMin = new Vector2(10, card.Owners.rectTransform.offsetMin.y);
            card.Owners.horizontalOverflow = HorizontalWrapMode.Wrap; card.Owners.verticalOverflow = VerticalWrapMode.Truncate;
            card.Owners.resizeTextForBestFit = true; card.Owners.resizeTextMinSize = 8; card.Owners.resizeTextMaxSize = 12;
            card.Frame = panel.gameObject.AddComponent<Outline>();
            card.Frame.effectColor = Palette.Accent; card.Frame.effectDistance = new Vector2(3, -3); card.Frame.enabled = false;
            card.TradableDot = UiKit.Panel(r, "TradableDot_" + id, Color.white);
            card.TradableDot.sprite = UiKit.Pill; card.TradableDot.type = Image.Type.Simple; card.TradableDot.preserveAspect = true;
            UiKit.Place(card.TradableDot.rectTransform, new Vector2(0, 1), new Vector2(0, 1), new Vector2(8, -11), new Vector2(9, 9));
            card.TradableDot.gameObject.SetActive(false);
            var chartRoot = UiKit.Rect(r, "TradeChart_" + id); UiKit.Stretch(chartRoot, 8, 53, 8, State.phase == Protocol.Investment ? (id == "BANK" ? 66 : 110) : State.phase != Protocol.Selection ? 8 : 54);
            card.Chart = chartRoot.gameObject.AddComponent<BarChartGraphic>(); card.Chart.SetInteractive(true);
            UiKit.Click(chartRoot.gameObject, () => OpenPlan?.Invoke(id));
            card.Shade = UiKit.Panel(r, "NotOwnedShade_" + id, new Color(0f, 0f, 0f, 0.66f), rounded: true);
            UiKit.Stretch(card.Shade.rectTransform, 0, 50, 0, 0);
            var shadeText = UiKit.Label(card.Shade.transform, "미보유 · 이번 턴 매매 불가", 14, Palette.SubText, TextAnchor.MiddleCenter, FontStyle.Bold);
            UiKit.Stretch(shadeText.rectTransform);
            card.Shade.gameObject.SetActive(false);
            if (State.phase == Protocol.Investment) BuildLiveControls(r, card, id);
            if (State.phase != Protocol.Selection && State.phase != Protocol.Investment) return;
            if (id == "BANK") { if (State.phase == Protocol.Selection) BuildBankSlots(r, card); return; }
            card.Inventory = UiKit.Rect(r, "ShareInventory_" + id);
            card.Inventory.anchorMin = Vector2.zero; card.Inventory.anchorMax = new Vector2(1, 0);
            float inventoryBottom = State.phase == Protocol.Investment ? 82 : 22;
            card.Inventory.offsetMin = new Vector2(8, inventoryBottom); card.Inventory.offsetMax = new Vector2(-8, inventoryBottom + 22);
            card.Units = new Image[StockRules.Supply];
            for (int i = 0; i < card.Units.Length; i++)
            {
                card.Units[i] = UiKit.Panel(card.Inventory, "ShareUnit_" + id + "_" + i, Palette.Disabled);
                UiKit.Anchor(card.Units[i].rectTransform, i / (float)StockRules.Supply, 0, (i + 1) / (float)StockRules.Supply, 1, 1, 1);
                card.Units[i].raycastTarget = false;
            }
            // 5칸마다 구분선과 숫자(5·10·15·20)
            for (int k = 5; k <= StockRules.Supply; k += 5)
            {
                float x = k / (float)StockRules.Supply;
                if (k < StockRules.Supply)
                {
                    var sep = UiKit.Panel(card.Inventory, "ShareMark_" + k, Palette.Text);
                    sep.rectTransform.anchorMin = new Vector2(x, 0); sep.rectTransform.anchorMax = new Vector2(x, 1);
                    sep.rectTransform.offsetMin = new Vector2(-1, -3); sep.rectTransform.offsetMax = new Vector2(1, 3);
                    sep.raycastTarget = false;
                }
                var mark = UiKit.Label(card.Inventory, k.ToString(), 11, Palette.SubText, TextAnchor.UpperCenter, FontStyle.Bold);
                mark.name = "ShareMarkLabel_" + k;
                UiKit.Place(mark.rectTransform, new Vector2(x, 0), new Vector2(k == StockRules.Supply ? 1f : .5f, 1), new Vector2(0, -3), new Vector2(26, 14));
            }
            // 투자 결정에서는 실시간 현황만 보여 주고 매수 슬라이더는 만들지 않는다.
            if (State.phase == Protocol.Investment) return;
            card.Stamp = UiKit.Label(r, "매진", 30, new Color32(0xFF, 0xFF, 0xFF, 0xD0), TextAnchor.MiddleCenter, FontStyle.Bold);
            card.Stamp.name = "SoldOutStamp_" + id;
            UiKit.Place(card.Stamp.rectTransform, new Vector2(.5f, .5f), new Vector2(.5f, .5f), new Vector2(0, 6), new Vector2(140, 50));
            card.Stamp.rectTransform.localRotation = Quaternion.Euler(0, 0, 12);
            card.Stamp.gameObject.SetActive(false);
            card.SliderRoot = UiKit.Rect(card.Inventory, "ShareSlider_" + id);
            var hit = UiKit.Panel(card.SliderRoot, "HitArea", Color.clear, raycast: true); UiKit.Stretch(hit.rectTransform, 0, -4, 0, -4);
            var handle = UiKit.Panel(card.SliderRoot, "Handle", Palette.Text, rounded: true, raycast: true);
            handle.rectTransform.sizeDelta = new Vector2(5, 4);
            card.Count = UiKit.Label(handle.transform, "", 11, Palette.Text, TextAnchor.LowerCenter, FontStyle.Bold);
            card.Count.name = "ShareCount_" + id;
            UiKit.Place(card.Count.rectTransform, new Vector2(.5f, 1), new Vector2(.5f, 0), new Vector2(0, 14), new Vector2(54, 14));
            var trigger = card.SliderRoot.gameObject.AddComponent<EventTrigger>();
            card.Slider = card.SliderRoot.gameObject.AddComponent<Slider>(); card.Slider.minValue = 0; card.Slider.wholeNumbers = true;
            card.Slider.handleRect = handle.rectTransform; card.Slider.targetGraphic = handle; card.Slider.transition = Selectable.Transition.None;
            var down = new EventTrigger.Entry { eventID = EventTriggerType.PointerDown };
            down.callback.AddListener(_ => { if (Editable) draggingCompany = id; }); trigger.triggers.Add(down);
            var up = new EventTrigger.Entry { eventID = EventTriggerType.PointerUp };
            up.callback.AddListener(_ => { draggingCompany = null; Commit(id); }); trigger.triggers.Add(up);
            card.Slider.onValueChanged.AddListener(value => {
                if (!Editable) return;
                int own = Holding(id), other = OtherShares(id);
                int cap = Math.Min(Portfolio.LimitFor(id), Math.Min(20 - other, own + Math.Min(Portfolio.draftSharesRemaining, Portfolio.CashSlots)));
                targets[id] = Math.Max(0, Math.Min(Mathf.RoundToInt(value), cap));
                card.Slider.SetValueWithoutNotify(targets[id]); RefreshCards(false);
                if (draggingCompany != id) Commit(id);
            });
        }
        /// <summary>BANK는 직접 사지 않는다(자동 예금). 내 30칸 중 기업에 쓰지 않은 칸이 BANK로 들어가는 모습을 보여 준다.</summary>
        private void BuildBankSlots(RectTransform r, Card card)
        {
            int total = Math.Max(1, (Portfolio?.usedSlots ?? 0) + (Portfolio?.CashSlots ?? StockRules.Slots));
            var bar = UiKit.Rect(r, "BankSlots");
            bar.anchorMin = Vector2.zero; bar.anchorMax = new Vector2(1, 0);
            bar.offsetMin = new Vector2(8, 22); bar.offsetMax = new Vector2(-8, 40);
            card.BankUnits = new Image[total];
            for (int i = 0; i < total; i++)
            {
                card.BankUnits[i] = UiKit.Panel(bar, "BankUnit_" + i, Palette.Disabled);
                UiKit.Anchor(card.BankUnits[i].rectTransform, i / (float)total, 0, (i + 1) / (float)total, 1, 1, 1);
                card.BankUnits[i].raycastTarget = false;
            }
            card.BankLabel = UiKit.Label(r, "", 12, Palette.Text, TextAnchor.MiddleLeft);
            card.BankLabel.name = "BankAutoDeposit";
            card.BankLabel.rectTransform.anchorMin = Vector2.zero; card.BankLabel.rectTransform.anchorMax = new Vector2(1, 0);
            card.BankLabel.rectTransform.offsetMin = new Vector2(10, 3); card.BankLabel.rectTransform.offsetMax = new Vector2(-8, 21);
        }

        private void RefreshBank(Card card)
        {
            if (card.BankUnits == null || Portfolio == null) return;
            int total = card.BankUnits.Length;
            // 드래그 중이면 고르는 수량을 바로 반영(서버 확정 전 미리보기)
            int used = 0;
            foreach (var pair in targets) if (pair.Key != "BANK") used += pair.Value;
            used = Math.Max(0, Math.Min(total, used));
            int cash = total - used;
            Color gold = new Color32(0xBF, 0xBF, 0xBF, 0xFF);
            Color invested = Palette.Player(Me);
            invested.a = 0.35f;
            for (int i = 0; i < total; i++) card.BankUnits[i].color = i < used ? invested : gold;
            card.BankLabel.text = "<color=#D0D0D0><b>자동 예금</b></color> · " + (State.swapTurn ? Me + " 계좌" : "내") + " BANK <b>" + cash + "</b>/" + total + "칸  <size=11><color=#8C8C8C>기업을 덜 살수록 늘어납니다</color></size>";
        }

        private int OtherShares(string id) => State.shareMarket?.FirstOrDefault(c => c.companyId == id)?.owners?.Where(v => v.playerId != Me)
            .Sum(v => v.shares + v.reserved) ?? 0;
        private void BuildLiveControls(RectTransform parent, Card card, string id)
        {
            var controls = UiKit.Rect(parent, "LiveOrders_" + id);
            controls.anchorMin = Vector2.zero; controls.anchorMax = new Vector2(1, 0);
            controls.offsetMin = new Vector2(4, 2); controls.offsetMax = new Vector2(-4, 62);
            card.Position = UiKit.Label(controls, "", 12, Palette.Text, TextAnchor.MiddleCenter);
            UiKit.Anchor(card.Position.rectTransform, .12f, .5f, .88f, 1, 1, 1);
            card.Actions = new Button[6];
            string[] labels = { "모두 진입", "진입", "청산", "모두 청산", "−", "+" };
            for (int i = 0; i < labels.Length; i++)
            {
                int action = i;
                var button = UiKit.Button(controls, labels[i], 12, Palette.AccentSoft, Palette.Accent, () =>
                {
                    var h = Portfolio.holdings.First(v => v.companyId == id);
                    int max = h.activeShares + Portfolio.AvailableToBuy(State.shareMarket.First(c => c.companyId == id));
                    if (action >= 4) {
                        card.Quantity = Mathf.Clamp(card.Quantity + (action == 4 ? -1 : 1), 1, Math.Max(1, max));
                        RefreshCards(); return;
                    }
                    int target = action == 0 ? max : action == 1 ? h.activeShares + card.Quantity :
                        action == 2 ? h.activeShares - card.Quantity : 0;
                    controller.SetStockPosition(id, target);
                });
                button.name = "LiveOrder_" + id + "_" + i;
                card.Actions[i] = button;
                float x = i == 4 ? 0 : i == 5 ? .88f : i * .25f;
                UiKit.Anchor((RectTransform)button.transform, x, i < 4 ? 0 : .5f, x + (i < 4 ? .25f : .12f), i < 4 ? .5f : 1, 1, 1);
            }
        }

        private void Commit(string id)
        {
            if (Editable && targets[id] != Holding(id)) controller.TradeShares(new[] { new ShareOrder { companyId = id, targetShares = targets[id] } });
            else RefreshCards();
        }
        private void RefreshCards(bool synchronize = true)
        {
            bool pending = controller.Store.HasPending(StockRules.Trade);
            if (synchronize && !pending && draggingCompany == null)
                foreach (var id in UiRules.CompanyIds) targets[id] = Holding(id);
            if (summary != null) summary.text = State.phase == Protocol.Selection
                ? (Editable ? "내 차례 · 남은 매수 " + Portfolio.draftSharesRemaining + "/15주" : pending ? "매수 처리 중" : State.currentPlayerId + " 차례") + " · 보유 " + Portfolio.usedSlots + "/30주 · BANK " + Portfolio.CashSlots + "주"
                : State.phase == Protocol.Info ? "정보 탐색 · 차트를 누르면 크게 볼 수 있습니다."
                : State.phase == Protocol.Investment && State.swapTurn ? "<color=#FF6B6B><b>교환 턴</b></color> · <b>" + Me + " 계좌</b>를 조종 중 · 내릴 기업을 사서 최대한 떨어뜨리세요 · 현재 " + StockRules.CurrentDay(State, Portfolio) + "/30일차"
                : State.phase == Protocol.Investment ? "실시간 매매 · 현재 " + StockRules.CurrentDay(State, Portfolio) + "/30일차 · "
                    + (State.lobbySettings?.daySeconds ?? StockRules.DaySeconds) + "초/일 · 실선 매수 · 점선 매도 · 색상은 참가자별"
                : "확정된 기업별 주식 현황 · 차트를 눌러 확인하세요.";
            foreach (var pair in cards)
            {
                string id = pair.Key; var card = pair.Value;
                var company = State.shareMarket?.FirstOrDefault(c => c.companyId == id);
                var owners = company?.owners ?? Array.Empty<ShareOwner>();
                int used = owners.Sum(o => o.shares + o.reserved), total = company?.totalShares ?? (id == "BANK" ? StockRules.BankSupply : StockRules.Supply);
                string tag = id == "BANK" ? "" : used >= total ? "  <color=#FFFFFF><b>매진</b></color>" : total - used <= 4 ? "  <color=#FFFFFF>품절 임박</color>" : "";
                // 실시간 시장 분위기: AI 붕괴 확률(빨강일수록 위험), BIO 급등 확률, FOOD 변동폭
                var mood = State.moods?.FirstOrDefault(m => m != null && m.companyId == id);
                string moodTag = mood == null ? "" : "  <color=" + (id == "AI" ? (mood.percent >= 25 ? "#FF6B6B" : mood.percent >= 12 ? "#FFD166" : "#BBBBBB")
                    : id == "BIO" ? (mood.percent >= 25 ? "#7CFFB2" : "#BBBBBB") : "#BBBBBB") + ">" + mood.text + "</color>";
                card.Heading.text = "<b>" + id + "</b> · " + used + "/" + total + "주" + tag + moodTag;
                // 진입·청산 단계: 내가 보유한(매매 가능한) 기업 강조, 미보유는 흐리게
                bool investing = State.phase == Protocol.Investment && !controller.Store.OrderLocked;
                int mine = Portfolio?.holdings?.Where(h => h.companyId == id).Sum(h => h.shares) ?? 0;
                int availableToBuy = Portfolio.AvailableToBuy(company);
                bool tradable = investing && (mine > 0 || availableToBuy > 0);
                if (card.Actions != null) {
                    int active = Portfolio.holdings.First(h => h.companyId == id).activeShares;
                    int free = availableToBuy;
                    int limit = active + free;
                    card.Quantity = Mathf.Clamp(card.Quantity, 1, Math.Max(1, limit));
                    card.Position.text = "보유 " + active + " · 매수 가능 " + free + " · 주문 " + card.Quantity + "주";
                    bool enabled = tradable && !State.paused && !controller.Store.HasPending(StockRules.Position);
                    bool[] available = { free > 0, free >= card.Quantity, active >= card.Quantity, active > 0, card.Quantity > 1, card.Quantity < limit };
                    for (int i = 0; i < available.Length; i++) card.Actions[i].interactable = enabled && available[i];
                }
                // 매매 가능: 네모 테두리·보유 주 네모 배지 대신 카드 왼쪽 위 흰 점만(보유 수는 아래 '보유 N' 줄에 있음)
                card.Frame.enabled = false;
                card.MineBadge.gameObject.SetActive(false);
                card.TradableDot.gameObject.SetActive(tradable);
                card.Shade.gameObject.SetActive(false);
                card.Heading.rectTransform.offsetMin = new Vector2(tradable ? 22 : 10, card.Heading.rectTransform.offsetMin.y);
                card.Heading.rectTransform.offsetMax = new Vector2(-70, card.Heading.rectTransform.offsetMax.y);
                card.Owners.text = string.Join("  ", owners.Select(o => "<color=#" + ColorUtility.ToHtmlStringRGB(Palette.Player(o.playerId)) + ">" + o.playerId + " ■" + (o.shares + o.reserved) + "</color>"));
                if (State.phase == Protocol.Investment && id != "BANK")
                    card.Owners.text = "잔여 " + Math.Max(0, total - used) + "/" + total + "주  ·  " + card.Owners.text;
                var visible = controller.Store.View?.companies?.FirstOrDefault(c => c.companyId == id);
                var revealed = controller.Store.RevealedTurn == State.turn ? controller.Store.Revealed?.FirstOrDefault(c => c.companyId == id) : null;
                card.Chart.SetData(DailyBarModel.Build(visible, revealed));
                card.Chart.SetTrendThroughDay(revealed != null ? 30 : State.phase == Protocol.Investment ? Portfolio?.processedDay : (int?)null);
                card.Chart.SetCurrentDay(StockRules.CurrentDay(State, Portfolio));
                // 체결 표시는 확대 차트에서만 보여 주어 작은 카드 차트가 복잡해지지 않게 합니다.
                card.Chart.SetFills(null);
                card.Chart.ShowTradeLabels = false; // 예약 구간은 띠만(시가·종가 글자 없음)
                if (investing) {
                    int? close = InformationWindowRules.KnownDays(visible)[Portfolio.processedDay];
                    card.Heading.text = "<b>" + id + "</b>  " + (close.HasValue ? (10000 + close.Value).ToString("N0") : "—");
                }
                if (revealed != null) card.Chart.ClearTradeOverlays(); // 시장 공개·투자 결과: 진입·청산·수익률 표시 없이 주가만
                else card.Chart.SetTradeIntervals(controller.Store.TradePlans.ChartIntervals(controller.Store.View, State.turn, id, controller.Store.OrderLocked), !controller.Store.OrderLocked);
                if (card.BankUnits != null) RefreshBank(card);
                if (card.Units == null) continue;
                var colors = owners.Where(o => o.playerId != Me).OrderBy(o => o.playerId)
                    .SelectMany(o => Enumerable.Repeat(Palette.Player(o.playerId), Math.Max(0, o.shares + o.reserved))).ToList();
                int other = Math.Min(20, colors.Count);
                int ownUnits = card.Slider != null ? targets[id] : owners.Where(o => o.playerId == Me).Sum(o => o.shares + o.reserved);
                colors.AddRange(Enumerable.Repeat(Palette.Player(Me), Math.Max(0, ownUnits)));
                Color empty = new Color32(0x3A, 0x3A, 0x3A, 0xFF);
                bool first = card.LastColors == null;
                if (first) card.LastColors = new Color[card.Units.Length];
                for (int i = 0; i < card.Units.Length; i++)
                {
                    var c = i < colors.Count ? colors[i] : empty;
                    card.Units[i].color = c; card.LastColors[i] = c;
                }
                if (card.Slider == null) continue;
                bool soldOut = used >= total;
                card.Stamp.gameObject.SetActive(soldOut);
                if (soldOut && !card.WasSoldOut && !first) UiAnim.Pulse(card.Stamp.rectTransform, 0.1f, 1.35f);
                card.WasSoldOut = soldOut;
                card.Count.text = targets[id] > 0 ? (State.swapTurn ? Me + " " : "내 ") + targets[id] + "주" : "";
                card.Count.color = Palette.Player(Me);
                card.SliderRoot.anchorMin = new Vector2(other / 20f, 0); card.SliderRoot.anchorMax = Vector2.one;
                card.SliderRoot.offsetMin = Vector2.zero; card.SliderRoot.offsetMax = Vector2.zero;
                card.Slider.maxValue = Math.Max(1, 20 - other); card.Slider.SetValueWithoutNotify(targets[id]);
                card.Slider.interactable = Editable && other < 20 && (Holding(id) > 0 || Portfolio.draftSharesRemaining > 0 && Portfolio.CashSlots > 0);
            }
            if (finish != null) finish.interactable = Editable;
        }
        private void BuildAugments()
        {
            Label(root, "증강 선택 · 1·4턴에 하나씩, 최대 2개 선택합니다", 0, 40, 20);
            Label(root, "선택한 증강: " + string.Join(" / ", Portfolio.augments.Select(StockRules.Name)), 42, 35, 13);
            if (Portfolio.augmentChosen) { Label(root, "선택 완료 · 다른 플레이어를 기다리는 중입니다.", 100, 100, 18); return; }
            var choices = (Portfolio.candidates ?? new string[0]).Where(c => !string.IsNullOrEmpty(c)).ToArray();
            // 후보 수만큼 칸을 나눈다(3칸 고정이면 후보가 2개일 때 한 칸이 비어 보임)
            float columns = Mathf.Max(1, choices.Length);
            for (int i = 0; i < choices.Length; i++)
            {
                string id = choices[i];
                // 카드의 위쪽 위치는 유지하고 아래쪽 여백만 줄여 한 줄 정도 확장합니다.
                // 따라서 상단 안내 문구는 가리지 않으면서 설명·이미지·버튼에 아래 공간을 제공합니다.
                var cell = UiKit.Rect(root, "Augment_" + id); UiKit.Anchor(cell, i / columns, 0, (i + 1) / columns, 1, 6, 90);
                cell.offsetMin = new Vector2(cell.offsetMin.x, 30);
                var panel = UiKit.Panel(cell, "Card", Palette.Card, rounded: true); UiKit.Stretch(panel.rectTransform);
                // 증강 이름은 규칙 카탈로그의 원래 표기를 그대로 사용합니다.
                // HIDDEN 손실 제어도 다른 증강 이름과 마찬가지로 한 줄로 표시합니다.
                var name = UiKit.Label(panel.transform, StockRules.Name(id), 20, Palette.Accent, TextAnchor.UpperCenter, FontStyle.Bold); UiKit.Stretch(name.rectTransform, 12, 20, 12, 0);
                var description = UiKit.Label(panel.transform, StockRules.Description(id), 15, Palette.Text, TextAnchor.UpperLeft);
                // 아래쪽으로 확장된 카드 안에서 설명 영역의 아래쪽을 이미지 바로 위까지 확장합니다.
                // 특히 HIDDEN 설명은 문장이 길기 때문에 이미지와의 여백을 더 줄여 세로 공간을 확보합니다.
                // 글자 크기는 다른 설명과 크게 차이 나지 않도록 13~15 범위에서만 자동 조정합니다.
                // 문장을 강제로 잘라내지 않고 모두 표시하여 설명의 의미가 사라지지 않게 합니다.
                float descriptionBottom = id == "HIDDEN_LOSS_CONTROL" ? 205 : 220;
                UiKit.Stretch(description.rectTransform, 16, 64, 16, descriptionBottom);
                description.resizeTextForBestFit = true;
                description.resizeTextMinSize = 13;
                description.resizeTextMaxSize = 15;
                description.verticalOverflow = VerticalWrapMode.Overflow;
                var artwork = AugmentArtwork.Create(panel.transform, id, 150);
                UiKit.Place(artwork, new Vector2(.5f, 0), new Vector2(.5f, 0), new Vector2(0, 60), new Vector2(150, 150));
                var button = UiKit.Button(panel.transform, "이 증강 선택", 14, Palette.Accent, Color.white, () => controller.ChooseAugment(id, focus));
                button.name = "ChooseAugment_" + id;
                UiKit.Place((RectTransform)button.transform, new Vector2(.5f, 0), new Vector2(.5f, 0), new Vector2(0, 14), new Vector2(160, 36));
                button.interactable = !State.paused && !controller.Store.HasPending(StockRules.Choose);
                // 증강 후보가 처음 뜰 때 카드가 한 장씩 뒤집히며 공개
                UiAnim.FlipIn(cell, "augment:" + State.turn + ":" + string.Join(",", choices) + ":" + i, 0.05f + i * 0.1f, 0.28f); // 빠르게 뒤집기(0.55초→0.28초, 카드 간격 0.22초→0.1초)
            }
        }
        private static Text Label(RectTransform parent, string text, float top, float height, int size = 14)
        {
            var t = UiKit.Label(parent, text, size, Palette.Text, TextAnchor.MiddleLeft);
            t.rectTransform.anchorMin = new Vector2(0, 1); t.rectTransform.anchorMax = Vector2.one;
            t.rectTransform.offsetMin = new Vector2(4, -top - height); t.rectTransform.offsetMax = new Vector2(-4, -top);
            return t;
        }
    }
}
