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
            public Text Heading, Owners;
            public BarChartGraphic Chart;
            public RectTransform Inventory, SliderRoot;
            public Slider Slider;
            public Image[] Units;
            public Text Count, Stamp;          // 손잡이 위 '내 n주', 매진 도장
            public Color[] LastColors;         // 직전 칸 색(새로 채워진 칸만 튀어 오르게)
            public bool WasSoldOut;
            public Image[] BankUnits;          // BANK: 내 30칸 중 자동 예금 칸
            public Text BankLabel;
            public Outline Frame;              // 진입·청산 단계: 매매 가능(보유) 기업 파란 테두리
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
        private string BuildKey => State.turn + ":" + State.phase + ":" + State.selectionIndex + ":" + State.currentPlayerId;
        private bool Editable => controller.CanDraftShares();
        private int Holding(string id) => State.shareMarket?.FirstOrDefault(c => c.companyId == id)?.owners?.Where(v => v.playerId == controller.LocalPlayerId)
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
            BuildOrderStrip();
            summary = Label(root, "", 49, 29, 13); summary.name = "StockPurchaseSummary";
            var grid = UiKit.Rect(root, "ShareMarketGrid"); UiKit.Stretch(grid, 0, 82, 0, 44);
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
            // 투자 단계의 '진입·청산 구간 확정' 버튼은 없앴다. 오른쪽 '주문 확정 · 진행' 버튼이 같은 일을 한다.
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

            // 가장자리만 붉게(가운데 내용은 가리지 않음)
            urgency = new UrgencyEdges(root, new Color(0.86f, 0.12f, 0.12f), 0.32f, 4f);

            if (!myTurnNow) return;
            var banner = UiKit.Panel(root, "MyTurnBanner", Palette.Accent, rounded: true);
            UiKit.Place(banner.rectTransform, new Vector2(.5f, .5f), new Vector2(.5f, .5f), new Vector2(0, 20), new Vector2(360, 64));
            banner.raycastTarget = false;
            var text = UiKit.Label(banner.transform, "<b>내 차례!</b>  <size=15>" + (State.developerMode ? "매수 후 완료를 누르세요" : StockRules.DraftSeconds + "초 안에 매수") + "</size>", 26, Color.white, TextAnchor.MiddleCenter);
            UiKit.Stretch(text.rectTransform);
            UiAnim.Rise(banner.rectTransform, 0f, 30f, 0.3f);
            UiAnim.Pulse(banner.rectTransform, 0.3f, 1.08f);
            UiAnim.FadeOut(banner.rectTransform, 1.1f, 0.35f);
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
                currentBar.color = ratio > .5f ? new Color32(0x10, 0xB9, 0x81, 0xFF) : ratio > .25f ? new Color32(0xF5, 0x9E, 0x0B, 0xFF) : new Color32(0xDC, 0x26, 0x26, 0xFF);
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
                currentSlotText.text = head + "<size=12>" + (State.selectionIndex / 4 + 1) + "차 · " + (hurry ? "<color=#DC2626><b>" + sec + "초</b></color>" : sec + "초") + " · 지금</size>";
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
                var panel = UiKit.Panel(root, "StockDraftSlot_" + i, current ? Palette.AccentSoft : Palette.Card, rounded: true);
                panel.rectTransform.anchorMin = new Vector2((i - first) / 4f, 1); panel.rectTransform.anchorMax = new Vector2((i - first + 1) / 4f, 1);
                panel.rectTransform.offsetMin = new Vector2(3, -45); panel.rectTransform.offsetMax = new Vector2(-3, 0);
                var text = UiKit.Label(panel.transform, ids[i] + "  <size=12>" + (i / 4 + 1) + (State.developerMode ? "차 · 완료 시 진행" : "차 · 7초") + (current ? " · 지금" : "") + "</size>", 22, Palette.Player(ids[i]), TextAnchor.MiddleCenter, FontStyle.Bold);
                text.name = "StockDraftPlayer_" + i; UiKit.Stretch(text.rectTransform, 4, 1, 4, 1);
                if (current)
                {
                    // 현재 차례: 테두리 강조 + 남은 시간 막대(초록→주황→빨강) + 등장 연출
                    var outline = panel.gameObject.AddComponent<Outline>();
                    outline.effectColor = Palette.Player(ids[i]); outline.effectDistance = new Vector2(2, -2);
                    var track = UiKit.Panel(panel.transform, "DraftTimerTrack", new Color32(0xE5, 0xE7, 0xEB, 0xFF), rounded: true);
                    track.rectTransform.anchorMin = Vector2.zero; track.rectTransform.anchorMax = new Vector2(1, 0);
                    track.rectTransform.offsetMin = new Vector2(8, 3); track.rectTransform.offsetMax = new Vector2(-8, 7);
                    currentBar = UiKit.Panel(track.transform, "DraftTimerBar", new Color32(0x10, 0xB9, 0x81, 0xFF), rounded: true);
                    currentBar.rectTransform.anchorMin = Vector2.zero; currentBar.rectTransform.anchorMax = Vector2.one;
                    currentBar.rectTransform.offsetMin = Vector2.zero; currentBar.rectTransform.offsetMax = Vector2.zero;
                    track.gameObject.SetActive(!State.developerMode);
                    currentSlot = panel.rectTransform; currentSlotText = text;
                    UiAnim.Pulse(panel.rectTransform, 0f, 1.08f);
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
            card.MineBadgeText = UiKit.Label(badge.transform, "", 11, Color.white, TextAnchor.MiddleCenter, FontStyle.Bold);
            UiKit.Stretch(card.MineBadgeText.rectTransform);
            card.MineBadge.gameObject.SetActive(false);
            card.Owners = Label(r, "", 30, 18, 12); card.Owners.name = "ShareOwners_" + id;
            card.Owners.rectTransform.offsetMin = new Vector2(10, card.Owners.rectTransform.offsetMin.y);
            card.Owners.horizontalOverflow = HorizontalWrapMode.Wrap; card.Owners.verticalOverflow = VerticalWrapMode.Truncate;
            card.Owners.resizeTextForBestFit = true; card.Owners.resizeTextMinSize = 8; card.Owners.resizeTextMaxSize = 12;
            card.Frame = panel.gameObject.AddComponent<Outline>();
            card.Frame.effectColor = Palette.Accent; card.Frame.effectDistance = new Vector2(3, -3); card.Frame.enabled = false;
            var chartRoot = UiKit.Rect(r, "TradeChart_" + id); UiKit.Stretch(chartRoot, 8, 53, 8, State.phase != Protocol.Selection ? 8 : 54);
            card.Chart = chartRoot.gameObject.AddComponent<BarChartGraphic>(); card.Chart.SetInteractive(true);
            UiKit.Click(chartRoot.gameObject, () => OpenPlan?.Invoke(id));
            card.Shade = UiKit.Panel(r, "NotOwnedShade_" + id, new Color(0.96f, 0.97f, 0.98f, 0.72f), rounded: true);
            UiKit.Stretch(card.Shade.rectTransform, 0, 50, 0, 0);
            var shadeText = UiKit.Label(card.Shade.transform, "미보유 · 이번 턴 매매 불가", 14, Palette.SubText, TextAnchor.MiddleCenter, FontStyle.Bold);
            UiKit.Stretch(shadeText.rectTransform);
            card.Shade.gameObject.SetActive(false);
            if (State.phase != Protocol.Selection) return;
            if (id == "BANK") { BuildBankSlots(r, card); return; }
            card.Inventory = UiKit.Rect(r, "ShareInventory_" + id);
            card.Inventory.anchorMin = Vector2.zero; card.Inventory.anchorMax = new Vector2(1, 0);
            card.Inventory.offsetMin = new Vector2(8, 22); card.Inventory.offsetMax = new Vector2(-8, 44);
            card.Units = new Image[StockRules.Supply];
            for (int i = 0; i < card.Units.Length; i++)
            {
                card.Units[i] = UiKit.Panel(card.Inventory, "ShareUnit_" + id + "_" + i, Palette.Disabled);
                UiKit.Anchor(card.Units[i].rectTransform, i / 20f, 0, (i + 1) / 20f, 1, 1, 1);
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
            card.Stamp = UiKit.Label(r, "매진", 30, new Color32(0xDC, 0x26, 0x26, 0xD0), TextAnchor.MiddleCenter, FontStyle.Bold);
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
            Color gold = new Color32(0xCA, 0x8A, 0x04, 0xFF);
            Color invested = Palette.Player(controller.LocalPlayerId);
            invested.a = 0.35f;
            for (int i = 0; i < total; i++) card.BankUnits[i].color = i < used ? invested : gold;
            card.BankLabel.text = "<color=#CA8A04><b>자동 예금</b></color> · 내 BANK <b>" + cash + "</b>/" + total + "칸  <size=11><color=#6B7280>기업을 덜 살수록 늘어납니다</color></size>";
        }

        private int OtherShares(string id) => State.shareMarket?.FirstOrDefault(c => c.companyId == id)?.owners?.Where(v => v.playerId != controller.LocalPlayerId)
            .Sum(v => v.shares + v.reserved) ?? 0;
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
                : State.phase == Protocol.Info ? "정보 탐색 중 구간 예약 가능 · 차트를 누르면 크게 볼 수 있습니다."
                : State.phase == Protocol.Investment ? "예약 구간을 확인하고 확정하세요. BANK 구간 밖의 미투자 수량은 현금으로 유지됩니다."
                : "확정된 기업별 주식 현황 · 차트를 눌러 확인하세요.";
            foreach (var pair in cards)
            {
                string id = pair.Key; var card = pair.Value;
                var company = State.shareMarket?.FirstOrDefault(c => c.companyId == id);
                var owners = company?.owners ?? Array.Empty<ShareOwner>();
                int used = owners.Sum(o => o.shares + o.reserved), total = company?.totalShares ?? (id == "BANK" ? StockRules.BankSupply : StockRules.Supply);
                string tag = id == "BANK" ? "" : used >= total ? "  <color=#DC2626><b>매진</b></color>" : total - used <= 4 ? "  <color=#DC2626>품절 임박</color>" : "";
                card.Heading.text = "<b>" + id + "</b> · " + used + "/" + total + "주" + tag;
                // 진입·청산 단계: 내가 보유한(매매 가능한) 기업 강조, 미보유는 흐리게
                bool investing = State.phase == Protocol.Investment && !controller.Store.OrderLocked;
                int mine = Portfolio?.holdings?.Where(h => h.companyId == id).Sum(h => h.shares) ?? 0;
                bool tradable = investing && (mine > 0 || id == "BANK");
                card.Frame.enabled = tradable;
                card.MineBadge.gameObject.SetActive(tradable);
                card.MineBadgeText.text = id == "BANK" ? "예금 가능" : "내 " + mine + "주";
                card.Shade.gameObject.SetActive(investing && !tradable);
                card.Heading.rectTransform.offsetMax = new Vector2(tradable ? -150 : -70, card.Heading.rectTransform.offsetMax.y);
                card.Owners.text = string.Join("  ", owners.Select(o => "<color=#" + ColorUtility.ToHtmlStringRGB(Palette.Player(o.playerId)) + ">" + o.playerId + " ■" + (o.shares + o.reserved) + "</color>"));
                var visible = controller.Store.View?.companies?.FirstOrDefault(c => c.companyId == id);
                var revealed = controller.Store.RevealedTurn == State.turn ? controller.Store.Revealed?.FirstOrDefault(c => c.companyId == id) : null;
                card.Chart.SetData(DailyBarModel.Build(visible, revealed));
                if (revealed?.investments != null) card.Chart.SetRevealedInvestments(revealed.investments);
                else card.Chart.SetTradeIntervals(controller.Store.TradePlans.ChartIntervals(controller.Store.View, State.turn, id, controller.Store.OrderLocked), !controller.Store.OrderLocked);
                if (card.BankUnits != null) RefreshBank(card);
                if (card.Slider == null) continue;
                var colors = owners.Where(o => o.playerId != controller.LocalPlayerId).SelectMany(o => Enumerable.Repeat(Palette.Player(o.playerId), Math.Max(0, o.shares + o.reserved))).ToList();
                int other = Math.Min(20, colors.Count);
                colors.AddRange(Enumerable.Repeat(Palette.Player(controller.LocalPlayerId), Math.Max(0, targets[id])));
                Color empty = new Color32(185, 190, 199, 255);
                bool first = card.LastColors == null;
                if (first) card.LastColors = new Color[card.Units.Length];
                for (int i = 0; i < card.Units.Length; i++)
                {
                    var c = i < colors.Count ? colors[i] : empty;
                    card.Units[i].color = c; card.LastColors[i] = c;
                }
                bool soldOut = used >= total;
                card.Stamp.gameObject.SetActive(soldOut);
                if (soldOut && !card.WasSoldOut && !first) UiAnim.Pulse(card.Stamp.rectTransform, 0.1f, 1.35f);
                card.WasSoldOut = soldOut;
                card.Count.text = targets[id] > 0 ? "내 " + targets[id] + "주" : "";
                card.Count.color = Palette.Player(controller.LocalPlayerId);
                card.SliderRoot.anchorMin = new Vector2(other / 20f, 0); card.SliderRoot.anchorMax = Vector2.one;
                card.SliderRoot.offsetMin = Vector2.zero; card.SliderRoot.offsetMax = Vector2.zero;
                card.Slider.maxValue = Math.Max(1, 20 - other); card.Slider.SetValueWithoutNotify(targets[id]);
                card.Slider.interactable = Editable && other < 20 && (Holding(id) > 0 || Portfolio.draftSharesRemaining > 0 && Portfolio.CashSlots > 0);
            }
            if (finish != null)
            {
                finish.interactable = State.phase == Protocol.Selection ? Editable : !State.paused && !controller.Store.OrderLocked && !controller.Store.HasPending(CommandTypes.SubmitInvestment);
                if (State.phase == Protocol.Investment) UiKit.SetButton(finish, controller.Store.OrderLocked ? "구간 확정 완료 · 대기" : "진입·청산 구간 확정", finish.interactable, Palette.Accent, Color.white);
            }
        }
        private void BuildAugments()
        {
            Label(root, "증강 선택 · 1·3·5턴에 하나씩 선택합니다", 0, 40, 20);
            Label(root, "선택한 증강: " + string.Join(" / ", Portfolio.augments.Select(StockRules.Name)), 42, 35, 13);
            if (Portfolio.augmentChosen) { Label(root, "선택 완료 · 다른 플레이어를 기다리는 중입니다.", 100, 100, 18); return; }
            var choices = (Portfolio.candidates ?? new string[0]).Where(c => !string.IsNullOrEmpty(c)).ToArray();
            // 후보 수만큼 칸을 나눈다(3칸 고정이면 후보가 2개일 때 한 칸이 비어 보임)
            float columns = Mathf.Max(1, choices.Length);
            for (int i = 0; i < choices.Length; i++)
            {
                string id = choices[i];
                var cell = UiKit.Rect(root, "Augment_" + id); UiKit.Anchor(cell, i / columns, 0, (i + 1) / columns, 1, 6, 90);
                var panel = UiKit.Panel(cell, "Card", Palette.Card, rounded: true); UiKit.Stretch(panel.rectTransform);
                var name = UiKit.Label(panel.transform, StockRules.Name(id), 20, Palette.Accent, TextAnchor.UpperCenter, FontStyle.Bold); UiKit.Stretch(name.rectTransform, 12, 20, 12, 0);
                var description = UiKit.Label(panel.transform, StockRules.Description(id), 15, Palette.Text, TextAnchor.UpperLeft); UiKit.Stretch(description.rectTransform, 16, 64, 16, 64);
                var button = UiKit.Button(panel.transform, "이 증강 선택", 14, Palette.Accent, Color.white, () => controller.ChooseAugment(id, focus));
                button.name = "ChooseAugment_" + id;
                UiKit.Place((RectTransform)button.transform, new Vector2(.5f, 0), new Vector2(.5f, 0), new Vector2(0, 14), new Vector2(160, 36));
                button.interactable = !State.paused && !controller.Store.HasPending(StockRules.Choose);
                // 증강 후보가 처음 뜰 때 카드가 한 장씩 뒤집히며 공개
                UiAnim.FlipIn(cell, "augment:" + State.turn + ":" + string.Join(",", choices) + ":" + i, 0.15f + i * 0.22f);
            }
            var targetLabel = Label(root, "집중 투자자 적용 기업: " + focus, 0, 0, 12);
            targetLabel.rectTransform.anchorMin = Vector2.zero; targetLabel.rectTransform.anchorMax = new Vector2(1, 0);
            targetLabel.rectTransform.offsetMin = new Vector2(6, 45); targetLabel.rectTransform.offsetMax = new Vector2(-6, 70);
            for (int i = 0; i < UiRules.CompanyIds.Length; i++)
            {
                string id = UiRules.CompanyIds[i]; if (id == "BANK") continue; var b = UiKit.Button(root, id, 12, focus == id ? Palette.AccentSoft : Palette.Card, Palette.Accent,
                    () => { focus = id; targetLabel.text = "집중 투자자 적용 기업: " + focus; });
                b.name = "FocusCompany_" + id;
                UiKit.Place((RectTransform)b.transform, new Vector2(0, 0), new Vector2(0, 0), new Vector2(6 + i * 90, 8), new Vector2(82, 30));
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
