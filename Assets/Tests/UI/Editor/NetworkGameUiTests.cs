using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.IO;
using BlackWhale.Network;
using BlackWhale.Flow;
using BlackWhale.Rules;
using BlackWhale.Rules.Information;
using BlackWhale.Shared;
using BlackWhale.UI.Dev;
using BlackWhale.UI.Presentation;
using BlackWhale.UI.State;
using BlackWhale.UI.View;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;

namespace BlackWhale.UI.Tests
{
    public class NetworkGameUiTests
    {
        [UnityTest]
        public IEnumerator StockDraftAutoBuysColoredInventoryAndShowsLargeReservedCharts()
        {
            var client = new PlanningClient(); var controller = new GameClientController(client);
            var canvas = new GameObject("Stock System UI", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvas.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.GetComponent<CanvasScaler>().uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            canvas.GetComponent<CanvasScaler>().referenceResolution = new Vector2(1280, 720);
            try
            {
                client.Receive(new ServerMessage { type = "WELCOME", session = new Session { playerId = "P1" } });
                var state = new PublicState { turn = 2, totalTurns = 6, stockSystem = true, windowInformation = true, phase = Protocol.Selection,
                    currentPlayerId = "P1", selectionDeadlineAtServerSeconds = 7, phaseEndsAtServerSeconds = 56,
                    draftOrder = new[] { "P1", "P2", "P3", "P4", "P1", "P2", "P3", "P4" },
                    players = new[] { "P1", "P2", "P3", "P4" }.Select(id => new Player { playerId = id, displayName = id, connected = true }).ToArray(),
                    shareMarket = UiRules.CompanyIds.Select(id => new CompanyShareState { companyId = id, totalShares = id == "BANK" ? 120 : 20, owners = new[] { 10, 4, 3, 2 }.Select((q, n) => new ShareOwner { playerId = "P" + (n + 1), shares = id == "AI" ? q : id == "BANK" ? 30 - q : 0 }).ToArray() }).ToArray() };
                var portfolio = new PortfolioState { usedSlots = 10, augmentChosen = true, augments = new string[0], candidates = new string[0], insights = new StockInsight[0],
                    holdings = UiRules.CompanyIds.Select(id => new PortfolioHolding { companyId = id, shares = id == "AI" ? 10 : 0 }).ToArray() };
                var view = new PlayerView { turn = 2, phase = Protocol.Selection, entryAtOpen = true, recipientPlayerId = "P1", assets = 10000, portfolio = portfolio, reservedInvestments = new[] { new Investment { companyId = "AI", intervals = new[] { new Interval { entryDay = 3, exitDay = 5 } } } },
                    companies = UiRules.CompanyIds.Select(id => new VisibleCompany { companyId = id }).ToArray() };
                client.Receive(new ServerMessage { type = "PUBLIC_STATE", state = state }); client.Receive(new ServerMessage { type = "PLAYER_VIEW", playerView = view });
                var tablet = TabletView.Create((RectTransform)canvas.transform, controller);
                yield return new WaitForSecondsRealtime(.4f); Canvas.ForceUpdateCanvases();
                var ai = canvas.GetComponentsInChildren<RectTransform>().Single(r => r.name == "ShareCard_AI");
                Assert.AreEqual(6, canvas.GetComponentsInChildren<BarChartGraphic>().Count(c => c.name.StartsWith("TradeChart_")));
                StringAssert.Contains("P1", ai.GetComponentsInChildren<Text>().Single(t => t.name == "ShareOwners_AI").text);
                Assert.IsFalse(canvas.GetComponentsInChildren<Text>().Any(t => t.name.StartsWith("ShareTarget_")), "ratio text below the quantity control is removed");
                Assert.IsFalse(canvas.GetComponentsInChildren<RectTransform>().Any(r => r.name.StartsWith("PortfolioSlot_")), "the bottom gold slot row is removed");
                Assert.IsFalse(canvas.GetComponentsInChildren<Button>().Any(b => b.name == "ApplyShareDraft"), "quantity changes do not require an apply button");
                var chart = ai.GetComponentInChildren<BarChartGraphic>();
                Assert.Greater(chart.rectTransform.rect.width, ai.rect.width * .85f, "the card chart uses the full width");
                Assert.Contains(new Vector2Int(3, 5), Field<List<Vector2Int>>(chart, "_bands"), "saved reservation appears on the purchase chart");
                ClickPlanning(canvas, "ExpandShare_AI");
                yield return new WaitForSecondsRealtime(.4f); Canvas.ForceUpdateCanvases();
                var zoom = canvas.GetComponentsInChildren<BarChartGraphic>().Single(c => c.name == "ZoomChart_AI");
                Assert.Greater(zoom.rectTransform.rect.width, chart.rectTransform.rect.width * 2);
                Assert.Contains(new Vector2Int(3, 5), Field<List<Vector2Int>>(zoom, "_bands"));
                CapturePlanningScreen(canvas.GetComponent<Canvas>(), "stock-auto-buy-zoom");
                ClickPlanning(canvas, "ShrinkChart"); tablet.ShowPage(0);
                yield return new WaitForSecondsRealtime(.4f);
                var slider = ai.GetComponentInChildren<Slider>();
                var pointer = new PointerEventData(null) { button = PointerEventData.InputButton.Left };
                slider.GetComponent<EventTrigger>().OnPointerDown(pointer); slider.value = 5;
                Assert.AreEqual(0, client.Sent.Count, "dragging can preview an exact quantity before release");
                Assert.AreEqual(5, ai.GetComponentsInChildren<Image>().Count(i => i.name.StartsWith("ShareUnit_") && i.color == Palette.Player("P1")));
                slider.GetComponent<EventTrigger>().OnPointerUp(pointer);
                Assert.AreEqual(StockRules.Trade, client.Sent.Single().type); Assert.AreEqual(5, client.Sent[0].payload.shareOrders.Single().targetShares);
                Action<string, int, int> acknowledge = (company, quantity, budget) => {
                    state.shareMarket.Single(c => c.companyId == company).owners.Single(o => o.playerId == "P1").shares = quantity;
                    portfolio.holdings.Single(h => h.companyId == company).shares = quantity;
                    portfolio.usedSlots = portfolio.holdings.Sum(h => h.shares); portfolio.draftSharesRemaining = budget;
                    state.shareMarket.Single(c => c.companyId == "BANK").owners.Single(o => o.playerId == "P1").shares = 30 - portfolio.usedSlots;
                    client.Receive(new ServerMessage { type = "PUBLIC_STATE", state = state }); client.Receive(new ServerMessage { type = "PLAYER_VIEW", playerView = view });
                    var last = client.Sent.Last(c => c.type == StockRules.Trade);
                    client.Receive(new ServerMessage { type = "COMMAND_RESULT", response = new CommandResult { type = StockRules.Trade + "_RESULT", requestId = last.requestId, turn = 2, success = true } });
                };
                acknowledge("AI", 5, 15);
                Assert.AreSame(slider, ai.GetComponentInChildren<Slider>(), "network replies do not destroy an active quantity control");
                canvas.GetComponentsInChildren<Slider>().Single(s => s.name == "ShareSlider_FOOD").value = 10;
                acknowledge("FOOD", 10, 5);
                canvas.GetComponentsInChildren<Slider>().Single(s => s.name == "ShareSlider_SPACE").value = 10;
                Assert.AreEqual(5, client.Sent.Last().payload.shareOrders.Single().targetShares, "auto-buy clamps to the remaining five-share allowance");
                acknowledge("SPACE", 5, 0);
                Assert.IsFalse(canvas.GetComponentsInChildren<Slider>().Single(s => s.name == "ShareSlider_BIO").interactable, "spent budget prevents further purchases");
                CapturePlanningScreen(canvas.GetComponent<Canvas>(), "stock-auto-buy-inventory");
                state.selectionIndex = 4; state.selectionDeadlineAtServerSeconds = 35; portfolio.draftSharesRemaining = 15;
                client.Receive(new ServerMessage { type = "PUBLIC_STATE", state = state }); client.Receive(new ServerMessage { type = "PLAYER_VIEW", playerView = view });
                Assert.IsTrue(canvas.GetComponentsInChildren<RectTransform>().Any(r => r.name == "StockDraftSlot_4"));
                Assert.IsTrue(canvas.GetComponentsInChildren<Slider>().Single(s => s.name == "ShareSlider_BIO").interactable);
                Assert.GreaterOrEqual(canvas.GetComponentsInChildren<Text>().Single(t => t.name == "StockDraftPlayer_4").fontSize, 20);
                CapturePlanningScreen(canvas.GetComponent<Canvas>(), "stock-system-second-round");
                state.phase = StockRules.AugmentPhase; state.turn = 3; state.phaseEndsAtServerSeconds = 20;
                view.turn = 3; view.phase = StockRules.AugmentPhase; portfolio.augmentChosen = false;
                portfolio.candidates = new[] { "CONCENTRATION", "DIVERSIFY", "ANALYST" };
                client.Receive(new ServerMessage { type = "PUBLIC_STATE", state = state }); client.Receive(new ServerMessage { type = "PLAYER_VIEW", playerView = view });
                yield return null; Canvas.ForceUpdateCanvases();
                Assert.AreEqual(3, canvas.GetComponentsInChildren<Button>().Count(b => b.name.StartsWith("ChooseAugment_")));
                ClickPlanning(canvas, "FocusCompany_FOOD");
                CapturePlanningScreen(canvas.GetComponent<Canvas>(), "stock-system-augment-choice");
                ClickPlanning(canvas, "ChooseAugment_CONCENTRATION");
                Assert.AreEqual(StockRules.Choose, client.Sent.Last().type); Assert.AreEqual("FOOD", client.Sent.Last().payload.companyId);
            }
            finally { controller.Dispose(); UnityEngine.Object.DestroyImmediate(canvas); }
        }

        [UnityTest]
        public IEnumerator NewTurnRefreshesTimerAndControlsWhenPrivateViewAndChangeNotificationAreDelayed()
        {
            var client = new PlanningClient(); var controller = new GameClientController(client);
            var canvas = new GameObject("Delayed turn UI", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvas.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.GetComponent<CanvasScaler>().uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            canvas.GetComponent<CanvasScaler>().referenceResolution = new Vector2(1280, 720);
            try
            {
                client.Receive(new ServerMessage { type = "WELCOME", session = new Session { playerId = "P1" } });
                var state = new PublicState { turn = 1, totalTurns = 6, phase = Protocol.Result, stockSystem = true, windowInformation = true,
                    serverElapsedSeconds = 100, phaseEndsAtServerSeconds = 105, players = new[] { "P1", "P2", "P3", "P4" }.Select(id => new Player { playerId = id, displayName = id }).ToArray(),
                    draftOrder = new[] { "P1", "P2", "P3", "P4", "P1", "P2", "P3", "P4" },
                    shareMarket = UiRules.CompanyIds.Select(id => new CompanyShareState { companyId = id, totalShares = id == "BANK" ? 120 : 20,
                        owners = new[] { new ShareOwner { playerId = "P1", shares = id == "BANK" ? 30 : 0 } } }).ToArray() };
                var view = new PlayerView { turn = 1, phase = Protocol.Result, recipientPlayerId = "P1", entryAtOpen = true,
                    companies = UiRules.CompanyIds.Select(id => new VisibleCompany { companyId = id }).ToArray(),
                    ownInvestments = new[] { new Investment { companyId = "AI", intervals = new[] { new Interval { entryDay = 3, exitDay = 5 } } } },
                    portfolio = new PortfolioState { augments = Array.Empty<string>(), candidates = Array.Empty<string>(), holdings = UiRules.CompanyIds.Select(id => new PortfolioHolding { companyId = id }).ToArray() } };
                client.Receive(new ServerMessage { type = "PUBLIC_STATE", state = state }); client.Receive(new ServerMessage { type = "PLAYER_VIEW", playerView = view });
                var tablet = TabletView.Create((RectTransform)canvas.transform, controller);
                // Simulate a missed notification; the frame loop must recover independently.
                controller.Store.Changed -= tablet.Refresh;
                state.turn = 2; state.phase = Protocol.Info; state.serverElapsedSeconds = 105; state.phaseEndsAtServerSeconds = 165;
                client.Receive(new ServerMessage { type = "PUBLIC_STATE", state = state });
                Assert.IsNull(controller.Store.View, "previous-turn holdings and locked buttons must disappear immediately");
                Assert.IsFalse(controller.HasCurrentPlayerView); Assert.IsFalse(controller.CanPlanTrade("AI"));
                yield return new WaitForSecondsRealtime(.65f);
                StringAssert.Contains("2/6", Field<Text>(tablet, "_turnLabel").text);
                Assert.Greater(Field<PhaseClock>(tablet, "_clock").RemainingSeconds(controller.Store.Public, Time.realtimeSinceStartup), 50);
                Assert.AreEqual(1, client.Sent.Count(c => c.type == "GET_PLAYER_VIEW"), "missing private snapshot triggers one resynchronization request");
                view.turn = 2; view.phase = Protocol.Info; view.coins = 15; view.ownInvestments = Array.Empty<Investment>();
                client.Receive(new ServerMessage { type = "PLAYER_VIEW", playerView = view });
                yield return null;
                Assert.IsTrue(controller.CanPlanTrade("AI")); Assert.IsFalse(controller.Store.OrderLocked);
                int before = Field<PhaseClock>(tablet, "_clock").RemainingSeconds(controller.Store.Public, Time.realtimeSinceStartup);
                yield return new WaitForSecondsRealtime(1.1f);
                Assert.Less(Field<PhaseClock>(tablet, "_clock").RemainingSeconds(controller.Store.Public, Time.realtimeSinceStartup), before);
                Assert.AreEqual(CommandStatus.Sent, controller.SelectInformationWindow("BIO", 10, 12));
                Assert.AreEqual("", controller.Store.TradePlans.TryAddInterval("AI", 3, 5));
                state.phase = Protocol.Selection; state.currentPlayerId = "P1"; state.serverElapsedSeconds = 165;
                state.phaseEndsAtServerSeconds = 221; state.selectionDeadlineAtServerSeconds = 172;
                client.Receive(new ServerMessage { type = "PUBLIC_STATE", state = state });
                yield return null;
                Assert.AreEqual(7, Field<PhaseClock>(tablet, "_clock").RemainingSeconds(controller.Store.Public, Time.realtimeSinceStartup));
                var slider = canvas.GetComponentsInChildren<Slider>().Single(s => s.name == "ShareSlider_AI");
                Assert.IsTrue(slider.interactable); slider.value = 5;
                Assert.IsTrue(client.Sent.Any(c => c.type == StockRules.Trade && c.payload.shareOrders[0].targetShares == 5));
                state.phase = Protocol.Investment; state.currentPlayerId = ""; state.serverElapsedSeconds = 221; state.phaseEndsAtServerSeconds = 244;
                view.phase = Protocol.Investment; view.portfolio.usedSlots = 5; view.portfolio.holdings.Single(h => h.companyId == "AI").shares = 5;
                client.Receive(new ServerMessage { type = "PUBLIC_STATE", state = state }); client.Receive(new ServerMessage { type = "PLAYER_VIEW", playerView = view });
                yield return null;
                Assert.AreEqual(23, Field<PhaseClock>(tablet, "_clock").RemainingSeconds(controller.Store.Public, Time.realtimeSinceStartup));
                ClickPlanning(canvas, "ConfirmStockIntervals");
                Assert.AreEqual(3, client.Sent.Last(c => c.type == CommandTypes.SubmitInvestment).payload.investments.Single().intervals[0].entryDay);
                CapturePlanningScreen(canvas.GetComponent<Canvas>(), "next-turn-timer-recovered");
            }
            finally { controller.Dispose(); UnityEngine.Object.DestroyImmediate(canvas); }
        }

        [UnityTest]
        public IEnumerator SixStockTurnsKeepTimersPurchasesReservationsAndOrderLocksWorking()
        {
            var market = MarketService.CreateStockSystem(7741);
            using (var host = new LocalRoomHost(market, new SharedInformationService(market), TimerMode.Realtime, settings: GameSettings.StockGame))
            {
                var controllers = new List<GameClientController>();
                var canvas = new GameObject("Multi turn timer UI", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
                canvas.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.GetComponent<CanvasScaler>().uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                canvas.GetComponent<CanvasScaler>().referenceResolution = new Vector2(1280, 720);
                try
                {
                    for (int i = 1; i <= 4; i++) controllers.Add(new GameClientController(host.Join("Timer " + i)));
                    host.Pump(); foreach (var c in controllers) c.SetReady(true); host.Pump();
                    controllers[0].StartGame(); host.Pump();
                    var tablet = TabletView.Create((RectTransform)canvas.transform, controllers[0]);
                    var displayClock = Field<PhaseClock>(tablet, "_clock");
                    Func<int> remaining = () => displayClock.RemainingSeconds(controllers[0].Store.Public, Time.realtimeSinceStartup);
                    for (int turn = 1; turn <= 6; turn++)
                    {
                        if (host.Room.Snapshot().phase == StockRules.AugmentPhase)
                        {
                            foreach (var c in controllers) c.ChooseAugment(c.Store.View.portfolio.candidates.First(a => a != "CONCENTRATION"));
                            host.Pump();
                        }
                        Assert.AreEqual(Protocol.Info, host.Room.Snapshot().phase, "turn " + turn + " info");
                        Assert.IsTrue(controllers.All(c => c.Store.View.portfolio.usedSlots == 0 && c.Store.View.portfolio.CashSlots == 30), "turn " + turn + " resets every player's holdings to BANK");
                        Assert.IsTrue(host.Room.Snapshot().shareMarket.Where(c => c.companyId != "BANK").All(c => c.owners.All(o => o.shares == 0 && o.reserved == 0)), "turn " + turn + " restores all company supply");
                        Assert.Greater(remaining(), 0, "turn " + turn + " public selection timer");
                        while (host.Room.Snapshot().publicSelectionActive)
                        {
                            var state = host.Room.Snapshot();
                            var selector = controllers.Single(c => c.LocalPlayerId == state.publicSelectionPlayerId);
                            selector.SelectInformationWindow(new[] { "AI", "BANK", "FOOD", "AUTO" }[state.publicSelectionIndex], 1);
                            host.Pump();
                        }
                        Assert.IsFalse(controllers[0].Store.OrderLocked, "turn " + turn + " previous order lock resets");
                        int before = remaining();
                        yield return new WaitForSecondsRealtime(1.1f);
                        Assert.Less(remaining(), before, "turn " + turn + " timer must actually count down");
                        controllers[0].SelectInformationWindow("BIO", 10, 12); host.Pump();
                        Assert.IsNull(controllers[0].Store.LastError, "turn " + turn + " information purchase");
                        Assert.IsTrue(controllers[0].CanPlanTrade("AI"));
                        Assert.AreEqual("", controllers[0].Store.TradePlans.TryAddInterval("AI", 3, 5)); host.Pump();
                        foreach (var c in controllers) c.FinishPhase(); host.Pump();
                        for (int pick = 0; pick < 8; pick++)
                        {
                            var state = host.Room.Snapshot();
                            Assert.AreEqual(Protocol.Selection, state.phase);
                            Assert.Greater(remaining(), 0, "turn " + turn + " pick " + pick + " timer");
                            var buyer = controllers.Single(c => c.LocalPlayerId == state.currentPlayerId);
                            if (buyer == controllers[0] && market.GetPortfolio("P1").usedSlots == 0)
                            {
                                buyer.TradeShares(new[] { new ShareOrder { companyId = "AI", targetShares = 10 }, new ShareOrder { companyId = "FOOD", targetShares = 5 } }); host.Pump();
                                Assert.IsNull(buyer.Store.LastError, "turn " + turn + " stock purchase");
                            }
                            buyer.PassStockDraft(); host.Pump();
                        }
                        Assert.AreEqual(Protocol.Investment, host.Room.Snapshot().phase);
                        Assert.Greater(remaining(), 0, "turn " + turn + " interval timer");
                        foreach (var c in controllers) c.SubmitInvestment(c.CurrentTradeReservations()); host.Pump();
                        Assert.AreEqual(Protocol.Reveal, host.Room.Snapshot().phase, "turn " + turn + " interval confirmation");
                        Assert.AreEqual(0, market.GetPortfolio("P1").holdings.Single(h => h.companyId == "AI").shares, "turn " + turn + " scheduled exit executes");
                        Assert.AreEqual(5, market.GetPortfolio("P1").usedSlots, "turn " + turn + " unsold stocks remain in the result until the next turn resets them");
                        foreach (var c in controllers) c.FinishPhase(); host.Pump();
                        yield return null;
                        foreach (var c in controllers) c.FinishPhase(); host.Pump();
                        yield return null;
                    }
                    Assert.AreEqual(Protocol.Ended, host.Room.Snapshot().phase);
                }
                finally { foreach (var c in controllers) c.Dispose(); UnityEngine.Object.DestroyImmediate(canvas); }
            }
        }

        [UnityTest]
        public IEnumerator StockInformationCanReserveBeforeBuyingAndKeepBandsDuringDraft()
        {
            var client = new PlanningClient(); var controller = new GameClientController(client);
            var events = new GameObject("Stock reservation events", typeof(EventSystem));
            var canvas = new GameObject("Stock Planning UI", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvas.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.GetComponent<CanvasScaler>().uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            canvas.GetComponent<CanvasScaler>().referenceResolution = new Vector2(1280, 720);
            try
            {
                client.Receive(new ServerMessage { type = "WELCOME", session = new Session { playerId = "P1" } });
                var state = new PublicState { turn = 1, totalTurns = 6, phase = Protocol.Info, stockSystem = true, windowInformation = true, phaseEndsAtServerSeconds = 60,
                    draftOrder = new[] { "P1", "P2", "P3", "P4", "P1", "P2", "P3", "P4" },
                    players = new[] { "P1", "P2", "P3", "P4" }.Select(id => new Player { playerId = id, displayName = id }).ToArray(),
                    shareMarket = UiRules.CompanyIds.Select(id => new CompanyShareState { companyId = id, totalShares = id == "BANK" ? 120 : 20,
                        owners = new[] { new ShareOwner { playerId = "P1", shares = id == "BANK" ? 30 : 0 } } }).ToArray() };
                var view = new PlayerView { turn = 1, phase = Protocol.Info, entryAtOpen = true, recipientPlayerId = "P1", coins = 15, assets = 10000,
                    companies = UiRules.CompanyIds.Select(id => new VisibleCompany { companyId = id }).ToArray(),
                    portfolio = new PortfolioState { augments = new string[0], candidates = new string[0], holdings = UiRules.CompanyIds.Select(id => new PortfolioHolding { companyId = id }).ToArray() } };
                client.Receive(new ServerMessage { type = "PUBLIC_STATE", state = state }); client.Receive(new ServerMessage { type = "PLAYER_VIEW", playerView = view });
                var tablet = TabletView.Create((RectTransform)canvas.transform, controller); tablet.ShowPage(1);
                yield return new WaitForSecondsRealtime(.4f); ClickPlanning(canvas, "Expand_AI"); Canvas.ForceUpdateCanvases();
                var pointer = canvas.GetComponentsInChildren<ChartPointerLayer>().Single(p => p.name == "ZoomChart_AI");
                DragChart(pointer, 4, 9); ClickPlanning(canvas, "ReserveRange");
                var save = client.Sent.Single(c => c.type == CommandTypes.SaveTradePlan);
                Assert.AreEqual(4, save.payload.investments.Single(i => i.companyId == "AI").intervals[0].entryDay);
                Assert.IsFalse(client.Sent.Any(c => c.type == StockRules.Trade || c.type == CommandTypes.BuyInformationWindow), "reservation never buys shares or information");
                CapturePlanningScreen(canvas.GetComponent<Canvas>(), "stock-information-reservation");
                state.phase = Protocol.Selection; state.currentPlayerId = "P1"; state.selectionDeadlineAtServerSeconds = 7; state.phaseEndsAtServerSeconds = 56;
                client.Receive(new ServerMessage { type = "PUBLIC_STATE", state = state });
                yield return new WaitForSecondsRealtime(.4f); Canvas.ForceUpdateCanvases();
                var chart = canvas.GetComponentsInChildren<BarChartGraphic>().Single(c => c.name == "TradeChart_AI");
                Assert.Contains(new Vector2Int(4, 9), Field<List<Vector2Int>>(chart, "_bands"));
                ClickPlanning(canvas, "ExpandShare_AI"); yield return new WaitForSecondsRealtime(.4f);
                Assert.Contains(new Vector2Int(4, 9), Field<List<Vector2Int>>(canvas.GetComponentsInChildren<BarChartGraphic>().Single(c => c.name == "ZoomChart_AI"), "_bands"));
            }
            finally { controller.Dispose(); UnityEngine.Object.DestroyImmediate(canvas); UnityEngine.Object.DestroyImmediate(events); }
        }

        [UnityTest]
        public IEnumerator StockIntervalsEditAfterPurchasesAndConfirmOnlyOwnedCompanies()
        {
            var client = new PlanningClient(); var controller = new GameClientController(client);
            var events = new GameObject("Stock interval events", typeof(EventSystem));
            var canvas = new GameObject("Stock Interval UI", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvas.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.GetComponent<CanvasScaler>().uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            canvas.GetComponent<CanvasScaler>().referenceResolution = new Vector2(1280, 720);
            try
            {
                client.Receive(new ServerMessage { type = "WELCOME", session = new Session { playerId = "P1" } });
                var state = new PublicState { turn = 1, totalTurns = 6, phase = Protocol.Investment, stockSystem = true, windowInformation = true,
                    phaseEndsAtServerSeconds = 23, selectionIndex = 8, draftOrder = new[] { "P1", "P2", "P3", "P4", "P1", "P2", "P3", "P4" },
                    players = new[] { "P1", "P2", "P3", "P4" }.Select(id => new Player { playerId = id, displayName = id }).ToArray(),
                    shareMarket = UiRules.CompanyIds.Select(id => new CompanyShareState { companyId = id, totalShares = id == "BANK" ? 120 : 20,
                        owners = new[] { new ShareOwner { playerId = "P1", shares = id == "AI" ? 10 : id == "BANK" ? 20 : 0 } } }).ToArray() };
                var view = new PlayerView { turn = 1, phase = Protocol.Investment, entryAtOpen = true, recipientPlayerId = "P1", assets = 10000,
                    companies = UiRules.CompanyIds.Select(id => new VisibleCompany { companyId = id }).ToArray(),
                    portfolio = new PortfolioState { usedSlots = 10, augments = new string[0], candidates = new string[0],
                        holdings = UiRules.CompanyIds.Select(id => new PortfolioHolding { companyId = id, shares = id == "AI" ? 10 : 0 }).ToArray() } };
                client.Receive(new ServerMessage { type = "PUBLIC_STATE", state = state }); client.Receive(new ServerMessage { type = "PLAYER_VIEW", playerView = view });
                var tablet = TabletView.Create((RectTransform)canvas.transform, controller);
                yield return null; Canvas.ForceUpdateCanvases();
                Assert.IsTrue(controller.CanPlanTrade("AI")); Assert.IsFalse(controller.CanPlanTrade("FOOD")); Assert.IsTrue(controller.CanPlanTrade("BANK"));
                ClickPlanning(canvas, "ExpandShare_AI");
                yield return new WaitForSecondsRealtime(.4f); Canvas.ForceUpdateCanvases();
                var pointer = canvas.GetComponentsInChildren<ChartPointerLayer>().Single(p => p.name == "ZoomChart_AI");
                DragChart(pointer, 3, 5); // 투자 단계: 드래그하면 바로 진입·청산으로 정해짐(예약하기 클릭 없음)
                var save = client.Sent.Single(c => c.type == CommandTypes.SaveTradePlan);
                Assert.AreEqual(1, save.payload.investments.Length); Assert.AreEqual("AI", save.payload.investments[0].companyId);
                Assert.AreEqual(3, save.payload.investments[0].intervals[0].entryDay); Assert.AreEqual(5, save.payload.investments[0].intervals[0].exitDay);
                Assert.IsFalse(canvas.GetComponentsInChildren<Button>().Any(b => b.name == "ConfirmInformationWindow"), "interval stage cannot buy more information");
                CapturePlanningScreen(canvas.GetComponent<Canvas>(), "stock-interval-after-purchase");
                tablet.ShowPage(0); yield return new WaitForSecondsRealtime(.4f);
                ClickPlanning(canvas, "ExpandShare_BANK");
                yield return new WaitForSecondsRealtime(.4f); Canvas.ForceUpdateCanvases();
                var bankPointer = canvas.GetComponentsInChildren<ChartPointerLayer>().Single(p => p.name == "ZoomChart_BANK");
                DragChart(bankPointer, 8, 12); // 투자 단계: 드래그하면 바로 진입·청산으로 정해짐(예약하기 클릭 없음)
                Assert.AreEqual(8, controller.CurrentTradeReservations().Single(i => i.companyId == "BANK").intervals[0].entryDay);
                tablet.ShowPage(0); yield return new WaitForSecondsRealtime(.4f);
                ClickPlanning(canvas, "ConfirmStockIntervals");
                var submit = client.Sent.Single(c => c.type == CommandTypes.SubmitInvestment);
                Assert.AreEqual(2, submit.payload.investments.Length);
                Assert.AreEqual(12, submit.payload.investments.Single(i => i.companyId == "BANK").intervals[0].exitDay);
                client.Receive(new ServerMessage { type = "COMMAND_RESULT", response = new CommandResult { type = CommandTypes.ResultOf(CommandTypes.SubmitInvestment),
                    requestId = submit.requestId, turn = 1, success = true, payload = new ResultPayload { locked = true, investments = submit.payload.investments } } });
                Assert.IsFalse(controller.CanPlanTrade("AI"));
            }
            finally { controller.Dispose(); UnityEngine.Object.DestroyImmediate(canvas); UnityEngine.Object.DestroyImmediate(events); }
        }

        [UnityTest]
        public IEnumerator MarketRevealShowsEveryPlayersColoredRangesAndScrollableReturns()
        {
            var client = new PlanningClient(); var controller = new GameClientController(client);
            var canvas = new GameObject("Public trade UI", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvas.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.GetComponent<CanvasScaler>().uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            canvas.GetComponent<CanvasScaler>().referenceResolution = new Vector2(1280, 720);
            try
            {
                client.Receive(new ServerMessage { type = "WELCOME", session = new Session { playerId = "P1" } });
                var state = new PublicState { turn = 1, phase = Protocol.Reveal, windowInformation = true, phaseEndsAtServerSeconds = 10,
                    players = new[] { "P1", "P2", "P3", "P4" }.Select(id => new Player { playerId = id, displayName = id, connected = true }).ToArray(), companySeats = new CompanySeat[0] };
                client.Receive(new ServerMessage { type = "PUBLIC_STATE", state = state });
                client.Receive(new ServerMessage { type = "PLAYER_VIEW", playerView = new PlayerView { turn = 1, phase = Protocol.Reveal, entryAtOpen = true,
                    recipientPlayerId = "P1", assets = 10000, selectedCompanies = new[] { "AI", "BANK" },
                    ownInvestments = new[] { new Investment { companyId = "AI", weightBps = 5000, intervals = new[] { new Interval { entryDay = 1, exitDay = 3 } } } },
                    companies = UiRules.CompanyIds.Select(id => new VisibleCompany { companyId = id }).ToArray() } });
                var tablet = TabletView.Create((RectTransform)canvas.transform, controller); tablet.ShowPage(1);
                var positions = new[] {
                    new RevealedInvestment { playerId = "P1", returnBps = 1800, intervals = new[] {
                        new RevealedTrade { entryDay = 1, exitDay = 3, returnBps = 300 },
                        new RevealedTrade { entryDay = 9, exitDay = 9, returnBps = 100 },
                        new RevealedTrade { entryDay = 17, exitDay = 30, returnBps = 1400 } } },
                    new RevealedInvestment { playerId = "P2", returnBps = 1000, intervals = new[] {
                        new RevealedTrade { entryDay = 2, exitDay = 8, returnBps = 700 },
                        new RevealedTrade { entryDay = 20, exitDay = 22, returnBps = 300 } } }
                };
                client.Receive(new ServerMessage { type = "MARKET_REVEAL", revealedMarket = UiRules.CompanyIds.Select(id => new RevealedCompany {
                    companyId = id, dailyChangesBps = Enumerable.Range(0, 31).Select(d => d * 100).ToArray(),
                    investments = id == "AI" ? positions : new RevealedInvestment[0] }).ToArray() });
                yield return new WaitForSecondsRealtime(.4f); Canvas.ForceUpdateCanvases();
                var badge = canvas.GetComponentsInChildren<Text>().Single(t => t.name == "PlanBadge_AI").text;
                StringAssert.Contains("P1", badge); StringAssert.Contains("P2", badge);
                CapturePlanningScreen(canvas.GetComponent<Canvas>(), "trading-reveal-all-companies");
                ClickPlanning(canvas, "Expand_AI"); Canvas.ForceUpdateCanvases();
                var chart = canvas.GetComponentsInChildren<BarChartGraphic>().Single(c => c.name == "ZoomChart_AI");
                Assert.AreEqual(2, Field<RevealedInvestment[]>(chart, "_revealedInvestments").Length);
                Assert.AreEqual(Palette.Player("P2"), chart.GetComponentsInChildren<Text>().Single(t => t.name == "RevealedPlayer_P2").color);
                var rows = canvas.GetComponentsInChildren<RectTransform>().Single(r => r.name == "PlannedIntervals");
                var labels = rows.GetComponentsInChildren<Text>();
                Assert.AreEqual(7, labels.Length, "both player totals and all five trades are accessible");
                Assert.IsTrue(labels.Any(t => t.text.Contains("9일 시가 → 9일 종가") && t.text.Contains("+1%")));
                Assert.IsTrue(labels.Any(t => t.text.Contains("P1") && t.text.Contains("+18%")));
                Assert.Greater(rows.sizeDelta.y, ((RectTransform)rows.parent).rect.height, "returns scroll instead of being clipped away");
                Assert.IsFalse(canvas.GetComponentsInChildren<Button>().Where(b => b.name == "ReserveRange").Any(b => b.interactable));
                CapturePlanningScreen(canvas.GetComponent<Canvas>(), "trading-reveal-colored-intervals");
            }
            finally { controller.Dispose(); UnityEngine.Object.DestroyImmediate(canvas); }
        }

        [UnityTest]
        public IEnumerator MouseRanges_BuyPerDay_ReserveMoveResize_DeleteWithKeyboard_AndOpenPrice()
        {
            var client = new PlanningClient(); var controller = new GameClientController(client);
            var events = new GameObject("Range test events", typeof(EventSystem));
            var previousInputMode = InputSystem.settings.updateMode;
            var previousBackground = InputSystem.settings.backgroundBehavior;
            var previousEditorInput = InputSystem.settings.editorInputBehaviorInPlayMode;
            InputSystem.settings.updateMode = InputSettings.UpdateMode.ProcessEventsManually;
            InputSystem.settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
            InputSystem.settings.editorInputBehaviorInPlayMode = InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;
            var keyboard = InputSystem.AddDevice<Keyboard>();
            InputSystem.EnableDevice(keyboard);
            var canvas = new GameObject("Mouse Range UI", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvas.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.GetComponent<CanvasScaler>().uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            canvas.GetComponent<CanvasScaler>().referenceResolution = new Vector2(1280, 720);
            try
            {
                client.Receive(new ServerMessage { type = "WELCOME", session = new Session { playerId = "P1" } });
                var state = new PublicState { turn = 1, phase = Protocol.Info, windowInformation = true, phaseEndsAtServerSeconds = 90,
                    players = new[] { new Player { playerId = "P1", displayName = "Range", connected = true } }, companySeats = new CompanySeat[0] };
                var view = new PlayerView { turn = 1, phase = Protocol.Info, entryAtOpen = true, recipientPlayerId = "P1", coins = 15, assets = 10000,
                    selectedCompanies = new string[0], ownInvestments = new Investment[0],
                    companies = UiRules.CompanyIds.Select(id => new VisibleCompany { companyId = id, visibleWindows = new VisibleInformationWindow[0] }).ToArray() };
                view.companies[0].visibleWindows = new[] { new VisibleInformationWindow { recordId = "public", source = "PUBLIC", startDay = 1,
                    dailyChangesBps = new[] { 100, 200, 300, 400, 500 }, dailyOpenBps = new[] { 0, 100, 200, 300, 400 } } };
                client.Receive(new ServerMessage { type = "PUBLIC_STATE", state = state }); client.Receive(new ServerMessage { type = "PLAYER_VIEW", playerView = view });
                var tablet = TabletView.Create((RectTransform)canvas.transform, controller); tablet.ShowPage(1);
                yield return null; ClickPlanning(canvas, "Expand_AI"); Canvas.ForceUpdateCanvases();
                var pointer = canvas.GetComponentsInChildren<ChartPointerLayer>().Single(p => p.name == "ZoomChart_AI");
                Assert.AreEqual(0, canvas.GetComponentsInChildren<Slider>().Length, "no range sliders");
                DragChart(pointer, 3, 9);
                Assert.AreEqual(0, client.Sent.Count); Assert.AreEqual(0, controller.Store.TradePlans.Get("AI").Intervals.Length);
                StringAssert.Contains("4코인", canvas.GetComponentsInChildren<Text>().Single(t => t.name == "InformationRangeCost").text);
                ClickPlanning(canvas, "ReserveRange");
                Assert.AreEqual(3, controller.Store.TradePlans.Get("AI").Intervals[0].entryDay);
                Assert.AreEqual(CommandTypes.SaveTradePlan, client.Sent.Single().type, "reservation is saved privately for automatic execution, without buying information or locking an order");
                DragChart(pointer, 6, 8);
                Assert.AreEqual(5, controller.Store.TradePlans.Get("AI").Intervals[0].entryDay);
                Assert.AreEqual(11, controller.Store.TradePlans.Get("AI").Intervals[0].exitDay);
                DragChart(pointer, 11, 13);
                Assert.AreEqual(13, controller.Store.TradePlans.Get("AI").Intervals[0].exitDay);
                DragChart(pointer, 13, 20);
                Assert.AreEqual(20, controller.Store.TradePlans.Get("AI").Intervals[0].exitDay, "holding longer than ten days is allowed");
                CapturePlanningScreen(canvas.GetComponent<Canvas>(), "mouse-range-reservation");
                EventSystem.current.SetSelectedGameObject(pointer.gameObject);
                Assert.AreSame(pointer.gameObject, EventSystem.current.currentSelectedGameObject);
                Assert.AreEqual(0, pointer.RangeSelection.SelectedIndex);
                Assert.IsTrue(Field<InputAction>(pointer, "_deleteAction").enabled);
                InputSystem.QueueStateEvent(keyboard, new KeyboardState(Key.Delete)); InputSystem.Update();
                var deleteAction = Field<InputAction>(pointer, "_deleteAction");
                Assert.IsTrue(deleteAction.triggered, "Delete invokes the chart input action");
                Assert.IsTrue(keyboard.deleteKey.isPressed, "queued Delete reaches the test keyboard"); yield return null;
                Assert.AreEqual(0, controller.Store.TradePlans.Get("AI").Intervals.Length, "Delete removes selected reservation");
                InputSystem.QueueStateEvent(keyboard, new KeyboardState()); InputSystem.Update(); yield return null;
                DragChart(pointer, 3, 9); ClickPlanning(canvas, "ReserveRange");
                InputSystem.QueueStateEvent(keyboard, new KeyboardState(Key.Backspace)); InputSystem.Update(); yield return null;
                Assert.AreEqual(0, controller.Store.TradePlans.Get("AI").Intervals.Length, "Backspace removes selected reservation");
                InputSystem.QueueStateEvent(keyboard, new KeyboardState()); InputSystem.Update(); yield return null;
                DragChart(pointer, 30, 30); ClickPlanning(canvas, "ConfirmInformationWindow");
                var buy = client.Sent.Single(c => c.type == CommandTypes.BuyInformationWindow); Assert.AreEqual(30, buy.payload.startDay); Assert.AreEqual(30, buy.payload.endDay);
                client.Receive(new ServerMessage { type = "COMMAND_RESULT", response = new CommandResult { type = "BUY_INFORMATION_WINDOW_RESULT", turn = 1,
                    requestId = buy.requestId, success = true, payload = new ResultPayload { recordId = "day30", companyId = "AI", startDay = 30,
                        dailyChangesBps = new[] { 1000 }, dailyOpenBps = new[] { 950 }, paidCoins = 1, remainingCoins = 14, source = "PURCHASED" } } });
                Assert.AreEqual(14, controller.Store.View.coins);
                var bars = Field<DayBar[]>(pointer.GetComponent<BarChartGraphic>(), "_bars");
                Assert.IsNull(bars[29].Bps); Assert.AreEqual(950, bars[30].OpenBps); Assert.AreEqual(1000, bars[30].Bps);
                CapturePlanningScreen(canvas.GetComponent<Canvas>(), "mouse-range-day-purchase");
                state.phase = Protocol.Selection; state.currentPlayerId = "P1"; state.phaseEndsAtServerSeconds = 122; state.selectionDeadlineAtServerSeconds = 94;
                client.Receive(new ServerMessage { type = "PUBLIC_STATE", state = state });
                view.selectedCompanies = new[] { "AI", "BANK" }; client.Receive(new ServerMessage { type = "PLAYER_VIEW", playerView = view });
                state.phase = Protocol.Investment; client.Receive(new ServerMessage { type = "PUBLIC_STATE", state = state });
                Canvas.ForceUpdateCanvases();
                pointer = canvas.GetComponentsInChildren<ChartPointerLayer>().Single(p => p.GetComponentsInParent<RectTransform>().Any(r => r.name == "Col_AI"));
                DragChart(pointer, 3, 9); ClickPlanning(canvas, "ReserveOrderRange_AI");
                pointer = canvas.GetComponentsInChildren<ChartPointerLayer>().Single(p => p.GetComponentsInParent<RectTransform>().Any(r => r.name == "Col_AI"));
                DragChart(pointer, 9, 10);
                var draft = Field<InvestmentDraft>(Field<InvestmentPage>(tablet, "_investmentPage"), "_draft");
                Assert.AreEqual(10, draft.FirstIntervals[0].exitDay, "order intervals can be resized by mouse too");
                pointer = canvas.GetComponentsInChildren<ChartPointerLayer>().Single(p => p.GetComponentsInParent<RectTransform>().Any(r => r.name == "Col_AI"));
                DragChart(pointer, 6, 6); EventSystem.current.SetSelectedGameObject(pointer.gameObject);
                InputSystem.QueueStateEvent(keyboard, new KeyboardState(Key.Delete)); InputSystem.Update(); yield return null;
                Assert.AreEqual(0, draft.FirstIntervals.Count, "keyboard deletion works in the investment draft");
            }
            finally {
                InputSystem.RemoveDevice(keyboard); InputSystem.settings.updateMode = previousInputMode; InputSystem.settings.backgroundBehavior = previousBackground;
                InputSystem.settings.editorInputBehaviorInPlayMode = previousEditorInput;
                controller.Dispose(); UnityEngine.Object.DestroyImmediate(canvas); UnityEngine.Object.DestroyImmediate(events);
            }
        }
        [UnityTest]
        public IEnumerator CompanyCardClick_SelectsImmediately_AndBlocksPendingFullExpiredAndOtherTurns()
        {
            var client = new PlanningClient(); var controller = new GameClientController(client);
            var canvas = new GameObject("Direct Selection UI", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvas.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.GetComponent<CanvasScaler>().uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            canvas.GetComponent<CanvasScaler>().referenceResolution = new Vector2(1280, 720);
            try
            {
                client.Receive(new ServerMessage { type = "WELCOME", session = new Session { playerId = "P1" } });
                var state = new PublicState { turn = 1, phase = Protocol.Selection, currentPlayerId = "P1", serverElapsedSeconds = 90,
                    phaseEndsAtServerSeconds = 122, selectionDeadlineAtServerSeconds = 94,
                    players = new[] { new Player { playerId = "P1", displayName = "Selector", connected = true } },
                    companySeats = UiRules.CompanyIds.Select(id => new CompanySeat { companyId = id, remainingSeats = id == "BANK" ? 0 : 2 }).ToArray() };
                var view = new PlayerView { turn = 1, phase = Protocol.Selection, recipientPlayerId = "P1", coins = 3,
                    selectedCompanies = new string[0], ownInvestments = new Investment[0],
                    companies = UiRules.CompanyIds.Select(id => new VisibleCompany { companyId = id, visibleWindows = new VisibleInformationWindow[0] }).ToArray() };
                client.Receive(new ServerMessage { type = "PUBLIC_STATE", state = state });
                client.Receive(new ServerMessage { type = "PLAYER_VIEW", playerView = view });
                var tablet = TabletView.Create((RectTransform)canvas.transform, controller);
                yield return null; Canvas.ForceUpdateCanvases();
                Assert.IsFalse(canvas.GetComponentsInChildren<Button>().Any(b => b.name == "ConfirmSelection" || b.name == "CancelCandidate"));
                var full = canvas.GetComponentsInChildren<Button>().Single(b => b.name == "SelectCompany_BANK");
                Assert.IsFalse(full.interactable); full.onClick.Invoke(); Assert.AreEqual(0, client.Sent.Count);
                var ai = canvas.GetComponentsInChildren<Button>().Single(b => b.name == "SelectCompany_AI");
                Assert.IsTrue(ai.interactable); ai.onClick.Invoke(); ai.onClick.Invoke();
                Assert.AreEqual(1, client.Sent.Count, "one card click sends one command immediately, pending click cannot duplicate it");
                Assert.AreEqual(CommandTypes.SelectCompany, client.Sent[0].type); Assert.AreEqual("AI", client.Sent[0].payload.companyId);
                Assert.AreEqual(0, controller.Store.OwnSelections.Count, "confirmed selections still come from the server");
                client.Receive(new ServerMessage { type = "COMMAND_RESULT", response = new CommandResult { type = "SELECT_COMPANY_RESULT",
                    turn = 1, requestId = client.Sent[0].requestId, success = true, payload = new ResultPayload { companyId = "AI" } } });
                CollectionAssert.AreEqual(new[] { "AI" }, controller.Store.OwnSelections);
                state.currentPlayerId = "P2"; state.selectionIndex = 1;
                client.Receive(new ServerMessage { type = "PUBLIC_STATE", state = state });
                var space = canvas.GetComponentsInChildren<Button>().Single(b => b.name == "SelectCompany_SPACE");
                Assert.IsFalse(space.interactable); space.onClick.Invoke(); Assert.AreEqual(1, client.Sent.Count);
                state.currentPlayerId = "P1"; state.selectionIndex = 7; state.serverElapsedSeconds = 120; state.selectionDeadlineAtServerSeconds = 119;
                client.Receive(new ServerMessage { type = "PUBLIC_STATE", state = state });
                space = canvas.GetComponentsInChildren<Button>().Single(b => b.name == "SelectCompany_SPACE");
                Assert.IsFalse(space.interactable); space.onClick.Invoke(); Assert.AreEqual(1, client.Sent.Count);
                CapturePlanningScreen(canvas.GetComponent<Canvas>(), "company-click-selection");
            }
            finally { controller.Dispose(); UnityEngine.Object.DestroyImmediate(canvas); }
        }
        [UnityTest]
        public IEnumerator InformationWindowZoom_KeepsTimerVisible_AndConfirmsOnlyAfterClick()
        {
            var client = new PlanningClient(); var controller = new GameClientController(client);
            var canvas = new GameObject("Window UI", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvas.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.GetComponent<CanvasScaler>().uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            canvas.GetComponent<CanvasScaler>().referenceResolution = new Vector2(1280, 720);
            try
            {
                client.Receive(new ServerMessage { type = "WELCOME", session = new Session { playerId = "P1" } });
                var state = new PublicState { turn = 1, phase = Protocol.Info, windowInformation = true, publicSelectionActive = true,
                    publicSelectionPlayerId = "P1", publicSelectionDeadlineAtServerSeconds = 10, phaseEndsAtServerSeconds = 90,
                    players = new[] { "P1", "P2", "P3", "P4" }.Select(id => new Player { playerId = id, displayName = id, connected = true }).ToArray(),
                    publicInformation = new PublicInformationChoice[0], companySeats = new CompanySeat[0] };
                var view = new PlayerView { turn = 1, phase = Protocol.Info, recipientPlayerId = "P1", coins = 3, assets = 10000,
                    selectedCompanies = new string[0], ownInvestments = new Investment[0],
                    companies = UiRules.CompanyIds.Select(id => new VisibleCompany { companyId = id, visibleWindows = new VisibleInformationWindow[0] }).ToArray() };
                client.Receive(new ServerMessage { type = "PUBLIC_STATE", state = state });
                client.Receive(new ServerMessage { type = "PLAYER_VIEW", playerView = view });
                var tablet = TabletView.Create((RectTransform)canvas.transform, controller); tablet.ShowPage(1);
                yield return new WaitForSecondsRealtime(.35f); Canvas.ForceUpdateCanvases();
                Assert.IsTrue(Field<RectTransform>(tablet, "_turnHighlight").gameObject.activeInHierarchy);
                StringAssert.Contains("지금 내 차례", Field<Text>(tablet, "_turnNoticeText").text);
                CapturePlanningScreen(canvas.GetComponent<Canvas>(), "stock-window-six-companies");
                yield return new WaitForSecondsRealtime(5.1f);
                Assert.IsTrue(Field<RectTransform>(tablet, "_turnNotice").gameObject.activeInHierarchy, "turn notice persists after the transient banner expires");
                Assert.IsTrue(Field<RectTransform>(tablet, "_turnHighlight").gameObject.activeInHierarchy);
                ClickPlanning(canvas, "Expand_AI"); yield return null; Canvas.ForceUpdateCanvases();
                var zoom = canvas.GetComponentsInChildren<RectTransform>().Single(t => t.name == "ZoomCompanyPanel");
                Assert.IsFalse(canvas.GetComponentsInChildren<Text>().Any(t => t.text == DisplayFormat.Percent(5000) || t.text == DisplayFormat.Percent(-5000)), "stock charts have no upper/lower percentage labels");
                var timer = Field<Text>(tablet, "_timerText");
                var zoomCorners = new Vector3[4]; var timerCorners = new Vector3[4];
                zoom.GetWorldCorners(zoomCorners); timer.rectTransform.GetWorldCorners(timerCorners);
                Assert.Less(zoomCorners[2].y, timerCorners[0].y, "the timer stays above the enlarged chart");
                Assert.AreSame(tablet.ScreenRect, timer.transform.parent.parent, "timer belongs to the top HUD, outside the right column");
                Assert.IsTrue(timer.gameObject.activeInHierarchy);
                StringAssert.Contains("공용 공개", timer.text); StringAssert.Contains("P1", timer.text);
                Assert.AreEqual(0, zoom.GetComponentsInChildren<Slider>().Length);
                var mouseChart = zoom.GetComponentInChildren<ChartPointerLayer>();
                DragChart(mouseChart, 26, 26);
                Assert.AreEqual(0, client.Sent.Count, "moving the window never spends or reveals");
                ClickPlanning(canvas, "ConfirmInformationWindow");
                Assert.AreEqual(CommandTypes.RevealInformationWindow, client.Sent.Single().type);
                Assert.AreEqual(26, client.Sent.Single().payload.startDay);
                Assert.IsFalse(controller.CanFinishPhase(), "cannot finish before all public picks");
                CapturePlanningScreen(canvas.GetComponent<Canvas>(), "public-my-turn-highlight-zoom");
                state.publicSelectionIndex = 1; state.publicSelectionPlayerId = "P2"; state.publicSelectionDeadlineAtServerSeconds = 20;
                state.publicInformation = new[] { new PublicInformationChoice { playerId = "P1", companyId = "AI", startDay = 26 } };
                view.companies.Single(c => c.companyId == "AI").visibleWindows = new[] { new VisibleInformationWindow {
                    recordId = "public-1", source = "PUBLIC", startDay = 26, dailyChangesBps = new[] { 100, 200, 300, 250, 350 } } };
                client.Receive(new ServerMessage { type = "PUBLIC_STATE", state = state });
                client.Receive(new ServerMessage { type = "PLAYER_VIEW", playerView = view });
                yield return null;
                StringAssert.Contains("P2", timer.text);
                Assert.IsFalse(Field<RectTransform>(tablet, "_turnHighlight").gameObject.activeInHierarchy);
                StringAssert.Contains("P2", Field<Text>(tablet, "_turnNoticeText").text);
                Assert.IsTrue(zoom.gameObject.activeInHierarchy, "public turns preserve the enlarged chart");
                var bars = Field<DayBar[]>(zoom.GetComponentInChildren<BarChartGraphic>(), "_bars");
                Assert.IsNull(bars[25].Bps); Assert.AreEqual(350, bars[30].Bps);
                CapturePlanningScreen(canvas.GetComponent<Canvas>(), "stock-window-public-timer");
                state.publicSelectionActive = false; state.publicSelectionIndex = 4;
                state.publicSelectionPlayerId = ""; state.publicSelectionDeadlineAtServerSeconds = 0;
                client.Receive(new ServerMessage { type = "PUBLIC_STATE", state = state });
                client.Receive(new ServerMessage { type = "COMMAND_RESULT", response = new CommandResult { type = "REVEAL_INFORMATION_WINDOW_RESULT",
                    requestId = client.Sent[0].requestId, turn = 1, success = true } });
                yield return null;
                StringAssert.DoesNotContain("공용 공개", timer.text);
                Assert.IsFalse(Field<RectTransform>(tablet, "_turnNotice").gameObject.activeInHierarchy);
                DragChart(mouseChart, 24, 28);
                ClickPlanning(canvas, "ConfirmInformationWindow");
                Assert.AreEqual(CommandTypes.BuyInformationWindow, client.Sent.Last().type);
                Assert.AreEqual(24, client.Sent.Last().payload.startDay);
                CapturePlanningScreen(canvas.GetComponent<Canvas>(), "stock-window-private-timer");
            }
            finally { controller.Dispose(); UnityEngine.Object.DestroyImmediate(canvas); }
        }
        private sealed class PlanningClient : IGameClient
        {
            public readonly List<Command> Sent = new List<Command>();
            public event Action<ServerMessage> MessageReceived;
            public void SendCommand(Command command) => Sent.Add(command);
            public void Receive(ServerMessage message) => MessageReceived?.Invoke(message);
            public void Pump() { }
            public void Dispose() { }
        }
        [UnitySetUp] public IEnumerator Enter() { yield return new EnterPlayMode(); }

        [UnityTearDown] public IEnumerator Exit()
        {
            foreach (var root in UnityEngine.Object.FindObjectsByType<DevTabletBootstrap>(FindObjectsSortMode.None))
                UnityEngine.Object.Destroy(root.gameObject);
            yield return null;
            yield return new ExitPlayMode();
        }

        [UnityTest]
        public IEnumerator HostAndGuestUi_BotSeats_PersonalInformation_FullSettlement_LeaveAndRehost()
        {
            int number = FreePort();
            var host = Create("Host UI");
            yield return null;
            Port(host, number);
            Click(host, "Host");
            yield return Until(() => C(host)?.Store.Public?.players.Length == 1, "host lobby");
            Assert.IsFalse(host.GetComponentsInChildren<Text>().Any(t => t.text.Contains("사람이 입장한 뒤") || t.text.Contains("혼자 테스트할 때는")), "lobby has no small setup instructions");
            Assert.AreEqual("P1", C(host).LocalPlayerId);
            Assert.IsFalse(C(host).Store.Public.players[0].isBot);
            Click(host, "OpenDifficulty");
            Click(host, "BotDifficulty_Hard");
            yield return Until(() => C(host).Store.Public.botDifficulty == "Hard", "D difficulty screen updates actual room setting");
            Click(host, "CloseGameScreen");
            Click(host, "FillBots");
            yield return Until(() => C(host).Store.Public.players.Length == 4 && C(host).Store.Public.players.Skip(1).All(p => p.isBot && p.ready), "three bot clients ready");
            Assert.AreEqual(Protocol.Lobby, C(host).Store.Public.phase, "host chooses when to start");
            Click(host, "RemoveBots");
            yield return Until(() => C(host).Store.Public.players.Length == 1, "remove all bots");

            var guest = Create("Guest UI");
            yield return null;
            Port(guest, number);
            Click(guest, "Join");
            yield return Until(() => C(guest)?.Store.Public?.players.Length == 2, "human reuses released P2 slot");
            Assert.AreEqual("P2", C(guest).LocalPlayerId);
            Click(guest, "OpenDifficulty");
            Assert.IsTrue(guest.GetComponentsInChildren<Button>().Where(b => b.name.StartsWith("BotDifficulty_")).All(b => !b.interactable), "guest cannot edit host difficulty");
            Click(guest, "CloseGameScreen");
            Assert.IsFalse(guest.GetComponentsInChildren<Button>().Any(b => b.name == "FillBots" || b.name == "RemoveBots"), "guest has no host controls");
            Click(host, "FillBots");
            yield return Until(() => C(host).Store.Public.players.Length == 4 && C(host).Store.Public.players.Count(p => p.isBot && p.ready) == 2, "two humans and two bots");
            CollectionAssert.AreEqual(new[] { "P1", "P2", "P3", "P4" }, C(host).Store.Public.players.Select(p => p.playerId));

            var extra = Create("Full room UI");
            yield return null;
            Port(extra, number);
            Click(extra, "Join");
            yield return Until(() => extra.GetComponentsInChildren<Text>().Any(t => t.text.Contains("방이 가득")), "full-room error shown in UI");
            Assert.IsNull(Field<NetworkSession>(extra, "session"));
            UnityEngine.Object.Destroy(extra.gameObject);

            Click(host, "Ready");
            Click(guest, "Ready");
            yield return Until(() => C(host).AllPlayersReady, "all players ready");
            Click(host, "StartGame");
            yield return Until(() => C(host).Store.View?.portfolio?.candidates?.Length > 0 && C(guest).Store.View?.portfolio?.candidates?.Length > 0, "augment selection");
            DriveHuman(C(host)); DriveHuman(C(guest));
            yield return Until(() => C(guest).Store.View?.turn == 1 && C(host).Store.Public.phase == Protocol.Info, "six-turn stock game starts with information");
            Assert.IsTrue(C(host).Store.Public.stockSystem);
            Assert.AreEqual(6, C(host).Store.Public.totalTurns);
            var hostSession = Field<NetworkSession>(host, "session");
            var guestSession = Field<NetworkSession>(guest, "session");
            var hostedRoom = Field<BlackWhale.Flow.GameRoom>(hostSession, "hostedRoom");
            Assert.IsFalse(guestSession.SetGamePaused(true), "guest cannot pause the host's room");
            Assert.IsFalse(guest.GetComponentsInChildren<Button>().Any(b => b.name == "PauseGame"));
            Click(host, "PauseGame");
            yield return Until(() => C(host).Store.Public.paused && C(guest).Store.Public.paused, "pause broadcast to both actual TCP clients");
            Assert.IsTrue(guest.GetComponentsInChildren<RectTransform>().Any(r => r.name == "GamePausedOverlay"));
            double pausedTime = hostedRoom.Snapshot().serverElapsedSeconds;
            int pausedCoins = C(guest).Store.View.coins;
            Assert.AreEqual(CommandStatus.BlockedLocally, C(guest).BuyInformation("AI", 0));
            guestSession.Client.SendCommand(new Command { type = "COMPLETE_PHASE", requestId = "while-paused", turn = 1, payload = new CommandPayload() });
            yield return Until(() => C(guest).Store.LastError?.RequestId == "while-paused", "paused server rejects raw TCP command");
            Assert.AreEqual(ErrorCodes.GamePaused, C(guest).Store.LastError.ErrorCode);
            C(guest).Store.ClearError();
            yield return new WaitForSecondsRealtime(.3f);
            Assert.AreEqual(pausedTime, hostedRoom.Snapshot().serverElapsedSeconds, "real clock stays frozen");
            Assert.AreEqual(pausedCoins, C(guest).Store.View.coins);
            CapturePlanningScreen(host.GetComponentInChildren<Canvas>(), "game-paused");
            var editorPause = typeof(NetworkSession).GetMethod("OnEditorPauseChanged", BindingFlags.Instance | BindingFlags.NonPublic);
            editorPause.Invoke(hostSession, new object[] { UnityEditor.PauseState.Paused });
            editorPause.Invoke(hostSession, new object[] { UnityEditor.PauseState.Unpaused });
            Assert.IsTrue(hostedRoom.Snapshot().paused, "editor unpause preserves manual game pause");
            Click(host, "ResumeGame");
            yield return Until(() => !C(host).Store.Public.paused && !C(guest).Store.Public.paused, "resume broadcast");
            editorPause.Invoke(hostSession, new object[] { UnityEditor.PauseState.Paused });
            pausedTime = hostedRoom.Snapshot().serverElapsedSeconds;
            yield return new WaitForSecondsRealtime(.3f);
            Assert.AreEqual(pausedTime, hostedRoom.Snapshot().serverElapsedSeconds, "editor pause hook freezes server time");
            editorPause.Invoke(hostSession, new object[] { UnityEditor.PauseState.Unpaused });
            Assert.Less(hostedRoom.Snapshot().serverElapsedSeconds - pausedTime, .1, "editor resume does not catch up elapsed wall time");
            yield return Until(() => !C(guest).Store.Public.paused, "guest sees editor resume");
            Assert.IsFalse(host.GetComponentsInChildren<Button>().Any(b => b.name == "OpenShop"), "stock mode uses augments and share competition");
            Assert.IsFalse(host.GetComponentsInChildren<Button>().Any(b => b.name == "RemoveBots"), "cannot remove bots mid-game");
            float publicDeadline = Time.realtimeSinceStartup + 20;
            while (C(guest).Store.Public.publicSelectionActive || C(host).Store.Public.publicSelectionActive)
            {
                RevealPublic(C(host)); RevealPublic(C(guest));
                Assert.Less(Time.realtimeSinceStartup, publicDeadline, "public selections finish in INFO");
                yield return null;
            }
            int startDay = Enumerable.Range(1, 26).First(d => InformationWindowRules.NewDays(C(guest).Store.View.companies.Single(c => c.companyId == "AI"), d) > 0);
            int guestBefore = C(guest).Store.View.coins;
            int cost = Math.Max(0, InformationWindowRules.NewDays(C(guest).Store.View.companies.Single(c => c.companyId == "AI"), startDay) - (C(guest).Store.View.ability?.freeInformationCredits ?? 0));
            Click(guest, "Expand_AI");
            DragChart(guest.GetComponentsInChildren<ChartPointerLayer>().Single(p => p.name == "ZoomChart_AI"), startDay, startDay + 4);
            Click(guest, "ConfirmInformationWindow");
            yield return Until(() => InformationWindowRules.NewDays(C(guest).Store.View.companies.Single(c => c.companyId == "AI"), startDay) == 0, "private C purchase result");
            Assert.IsTrue(guest.GetComponentsInChildren<ChartPointerLayer>().Any(p => p.name == "ZoomChart_AI"), "purchase keeps graph enlarged");
            Assert.IsFalse(guest.GetComponentsInChildren<Button>().Single(b => b.name == "ConfirmInformationWindow").interactable, "purchased window cannot be bought twice");
            CapturePlanningScreen(guest.GetComponentInChildren<Canvas>(), "zoom-purchase");
            Click(guest, "ShrinkChart");
            Assert.AreEqual(guestBefore - cost, C(guest).Store.View.coins);
            Assert.Greater(InformationWindowRules.NewDays(C(host).Store.View.companies.Single(c => c.companyId == "AI"), startDay), 0);
            Assert.AreEqual(15, C(host).Store.View.coins);

            var deadline = Time.realtimeSinceStartup + 240;
            while (C(host).Store.Scoreboard == null || C(guest).Store.Scoreboard == null)
            {
                Assert.Less(Time.realtimeSinceStartup, deadline, "B settlement reaches both UIs");
                DriveHuman(C(host)); DriveHuman(C(guest));
                yield return null;
            }
            Assert.AreEqual(4, C(host).Store.Scoreboard.rows.Length);
            CollectionAssert.AreEqual(C(host).Store.Scoreboard.rows.Select(r => r.totalAssets), C(guest).Store.Scoreboard.rows.Select(r => r.totalAssets));
            Assert.Greater(C(guest).Store.View.portfolio.usedSlots, 0);
            Assert.Greater(C(host).Store.View.portfolio.usedSlots, 0);
            Click(host, "LeaveRoom");
            yield return Until(() => !string.IsNullOrEmpty(C(guest).Store.EndReason), "host leaving closes the room");
            Assert.IsNotNull(C(guest).Store.Scoreboard, "settled history remains visible after disconnect");
            Click(guest, "LeaveRoom");
            Assert.IsNull(C(guest));
            Click(host, "Host");
            yield return Until(() => C(host)?.Store.Public?.players.Length == 1, "same port can host again");
        }

        [UnityTest]
        public IEnumerator ChartZoom_PersonalPlans_PersistIntoOrder_AndConfirmedRangesSurviveReveal()
        {
            var client = new PlanningClient(); var controller = new GameClientController(client);
            var canvas = new GameObject("Planning UI", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvas.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.GetComponent<CanvasScaler>().uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            canvas.GetComponent<CanvasScaler>().referenceResolution = new Vector2(1280, 720);
            try
            {
                client.Receive(new ServerMessage { type = "WELCOME", session = new Session { playerId = "P1" } });
                var state = new PublicState { turn = 1, phase = Protocol.Info, phaseEndsAtServerSeconds = 90,
                    players = new[] { new Player { playerId = "P1", displayName = "Planner", connected = true } },
                    companySeats = UiRules.CompanyIds.Select(id => new CompanySeat { companyId = id, remainingSeats = 2 }).ToArray() };
                var privateView = new PlayerView { turn = 1, phase = Protocol.Info, recipientPlayerId = "P1", assets = 10000, coins = 3,
                    selectedCompanies = new string[0], ownInvestments = new Investment[0],
                    companies = UiRules.CompanyIds.Select(id => new VisibleCompany { companyId = id, visibleBundles = new[] {
                        new VisibleBundle { bundleIndex = 1, startDay = 6, source = "PUBLIC", dailyChangesBps = new[] { 60, 70, 80, 90, 100 } },
                        new VisibleBundle { bundleIndex = 2, startDay = 11, source = "PURCHASED", dailyChangesBps = new[] { 110, 120, 130, 140, 150 } } } }).ToArray() };
                client.Receive(new ServerMessage { type = "PUBLIC_STATE", state = state });
                client.Receive(new ServerMessage { type = "PLAYER_VIEW", playerView = privateView });
                var tablet = TabletView.Create((RectTransform)canvas.transform, controller); tablet.ShowPage(1);
                yield return null; Canvas.ForceUpdateCanvases();
                float smallWidth = canvas.GetComponentsInChildren<BarChartGraphic>().First(c => c.name == "Chart").rectTransform.rect.width;
                ClickPlanning(canvas, "Expand_AI"); Canvas.ForceUpdateCanvases();
                var chart = canvas.GetComponentsInChildren<ChartPointerLayer>().Single(c => c.name == "ZoomChart_AI");
                Assert.Greater(chart.GetComponent<BarChartGraphic>().rectTransform.rect.width, smallWidth * 2, "enlarged chart fills central tablet outside the pager mask");
                Assert.IsFalse(chart.GetComponentsInParent<RectMask2D>().Any(), "large graph is not clipped to a company card or page");
                Assert.IsNull(Field<DayBar[]>(chart.GetComponent<BarChartGraphic>(), "_bars")[20].Bps, "zoom never reveals hidden prices");
                ClickPlanning(canvas, "FavoriteCompany"); ClickPlanning(canvas, "PlanRank_1");
                chart.Clicked(10, false); chart.Clicked(15, false);
                Assert.AreEqual(10, controller.Store.TradePlans.Get("AI").Intervals.Single().entryDay);
                CapturePlanningScreen(canvas.GetComponent<Canvas>(), "zoom-plan");
                ClickPlanning(canvas, "ShrinkChart");
                Assert.AreEqual(6, canvas.GetComponentsInChildren<Button>().Count(b => b.name.StartsWith("Expand_")));
                CapturePlanningScreen(canvas.GetComponent<Canvas>(), "six-companies");
                ClickPlanning(canvas, "Expand_SPACE");
                chart = canvas.GetComponentsInChildren<ChartPointerLayer>().Single(c => c.name == "ZoomChart_SPACE");
                chart.Clicked(0, false); chart.Clicked(5, false); ClickPlanning(canvas, "ShrinkChart");
                Assert.AreEqual(0, client.Sent.Count, "favorite, priority, zoom and planned dates send no server commands");
                Assert.AreEqual(3, controller.Store.View.coins);
                tablet.ShowBanner("이전 정보 탐색 안내");
                state.phase = Protocol.Selection; state.currentPlayerId = "P1";
                client.Receive(new ServerMessage { type = "PUBLIC_STATE", state = state });
                Assert.IsFalse(Field<Queue<string>>(tablet, "_bannerQueue").Any(m => m.Contains("이전 정보 탐색")), "queued messages expire on phase change");
                Assert.AreEqual("", Field<Text>(tablet, "_bannerText").text, "visible old-phase message disappears immediately");
                yield return null;
                StringAssert.Contains("내 차례", Field<Text>(tablet, "_bannerText").text);
                state.paused = true; client.Receive(new ServerMessage { type = "PUBLIC_STATE", state = state });
                Assert.IsFalse(canvas.GetComponentsInChildren<Button>().Single(b => b.name == "SelectCompany_AI").interactable, "pause blocks immediate selection");
                state.paused = false; client.Receive(new ServerMessage { type = "PUBLIC_STATE", state = state });
                Assert.IsTrue(canvas.GetComponentsInChildren<Button>().Single(b => b.name == "SelectCompany_AI").interactable, "resume allows selection before its original deadline");
                tablet.ShowBanner("지난 선택 차례 안내");
                state.currentPlayerId = "P2"; state.selectionIndex = 1;
                client.Receive(new ServerMessage { type = "PUBLIC_STATE", state = state });
                Assert.AreEqual("", Field<Text>(tablet, "_bannerText").text, "my-turn notice disappears when next player selects");
                Assert.AreEqual(0, Field<Queue<string>>(tablet, "_bannerQueue").Count);
                StringAssert.Contains("1순위", canvas.GetComponentsInChildren<Text>().Single(t => t.name == "SelectionPlan_AI").text);
                privateView.selectedCompanies = new[] { "AI", "SPACE" };
                client.Receive(new ServerMessage { type = "PLAYER_VIEW", playerView = privateView });
                state.phase = Protocol.Investment; state.currentPlayerId = "";
                client.Receive(new ServerMessage { type = "PUBLIC_STATE", state = state });
                var investmentPage = Field<InvestmentPage>(tablet, "_investmentPage");
                var draft = Field<InvestmentDraft>(investmentPage, "_draft");
                Assert.AreEqual(10, draft.FirstIntervals.Single().entryDay, "selected-company plan prepopulates real investment form");
                ClickPlanning(canvas, "RemoveInterval_AI_0");
                var orderChart = canvas.GetComponentsInChildren<ChartPointerLayer>().Single(p => p.GetComponentsInParent<RectTransform>().Any(r => r.name == "Col_AI"));
                orderChart.Clicked(6, false);
                orderChart = canvas.GetComponentsInChildren<ChartPointerLayer>().Single(p => p.GetComponentsInParent<RectTransform>().Any(r => r.name == "Col_AI"));
                orderChart.Clicked(11, false);
                ClickPlanning(canvas, "SubmitOrder");
                Assert.AreEqual(1, client.Sent.Count);
                var command = client.Sent.Single();
                Assert.AreEqual(6, command.payload.investments.Single(i => i.companyId == "AI").intervals.Single().entryDay);
                client.Receive(new ServerMessage { type = "COMMAND_RESULT", response = new CommandResult {
                    type = "SUBMIT_INVESTMENT_RESULT", requestId = command.requestId, turn = 1, success = true,
                    payload = new ResultPayload { locked = true, investments = command.payload.investments } } });
                state.phase = Protocol.Reveal;
                client.Receive(new ServerMessage { type = "PUBLIC_STATE", state = state });
                client.Receive(new ServerMessage { type = "MARKET_REVEAL", revealedMarket = UiRules.CompanyIds.Select(id => new RevealedCompany {
                    companyId = id, dailyChangesBps = Enumerable.Range(0, 31).Select(d => d * 10).ToArray() }).ToArray() });
                ClickPlanning(canvas, "Expand_AI");
                chart = canvas.GetComponentsInChildren<ChartPointerLayer>().Single(c => c.name == "ZoomChart_AI");
                StringAssert.Contains("6", chart.GetComponentsInChildren<Text>().Single(t => t.name == "EntryMarker_0").text);
                StringAssert.Contains("11", chart.GetComponentsInChildren<Text>().Single(t => t.name == "ExitMarker_0").text);
                Assert.IsTrue(Field<DayBar[]>(chart.GetComponent<BarChartGraphic>(), "_bars")[20].Bps.HasValue, "full prices appear only after reveal");
                Assert.IsFalse(canvas.GetComponentsInChildren<Button>().Single(b => b.name == "PlanRank_1").interactable);
                Assert.AreEqual(10, controller.Store.TradePlans.Get("AI").Intervals.Single().entryDay, "confirmed markers supersede the original plan without overwriting it");
                CapturePlanningScreen(canvas.GetComponent<Canvas>(), "reveal-confirmed-order");
                client.Receive(new ServerMessage { type = "SCOREBOARD", scoreboard = new Scoreboard { turn = 1,
                    rows = new[] { new ScoreRow { playerId = "P1", rank = 1, totalAssets = 10000, returnHistory = new ReturnPoint[0] } } } });
                state.phase = Protocol.Result;
                client.Receive(new ServerMessage { type = "PUBLIC_STATE", state = state });
                Assert.IsTrue(Field<Queue<string>>(tablet, "_bannerQueue").Any(m => m.Contains("정산 완료")), "scoreboard received before result phase is announced when that phase starts");
                yield return null;
                state.turn = 2; state.phase = Protocol.Info;
                client.Receive(new ServerMessage { type = "PUBLIC_STATE", state = state });
                Assert.AreEqual("", Field<Text>(tablet, "_bannerText").text, "previous turn result banner vanishes immediately");
                Assert.IsFalse(Field<Queue<string>>(tablet, "_bannerQueue").Any(m => m.Contains("정산 완료") || m.Contains("1턴")), "no prior turn result remains queued");
            }
            finally { UnityEngine.Object.Destroy(canvas); controller.Dispose(); }
            yield return null;
        }
        private static void ClickPlanning(GameObject root, string name)
        {
            var button = root.GetComponentsInChildren<Button>().Single(b => b.name == name);
            Assert.IsTrue(button.interactable, name + " enabled"); button.onClick.Invoke();
        }
        [UnityTest]
        public IEnumerator ZoomPurchase_FreeCredit_Pending_Response_AndPhaseGuard()
        {
            var client = new PlanningClient(); var controller = new GameClientController(client);
            var canvas = new GameObject("Zoom purchase UI", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvas.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.GetComponent<CanvasScaler>().uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            canvas.GetComponent<CanvasScaler>().referenceResolution = new Vector2(1280, 720);
            try
            {
                client.Receive(new ServerMessage { type = "WELCOME", session = new Session { playerId = "P1" } });
                var state = new PublicState { turn = 1, phase = Protocol.Info, phaseEndsAtServerSeconds = 90,
                    players = new[] { new Player { playerId = "P1", connected = true } } };
                var view = new PlayerView { turn = 1, phase = Protocol.Info, recipientPlayerId = "P1", coins = 0,
                    ability = new AbilityState { freeInformationCredits = 1 },
                    companies = UiRules.CompanyIds.Select(id => new VisibleCompany { companyId = id, visibleBundles = new VisibleBundle[0] }).ToArray() };
                client.Receive(new ServerMessage { type = "PUBLIC_STATE", state = state });
                client.Receive(new ServerMessage { type = "PLAYER_VIEW", playerView = view });
                TabletView.Create((RectTransform)canvas.transform, controller);
                ClickPlanning(canvas, "Expand_AI");
                var buy = canvas.GetComponentsInChildren<Button>().Single(b => b.name == "ZoomBuyBundle_0");
                Assert.IsTrue(buy.interactable, "free credit enables enlarged purchase at zero coins");
                ClickPlanning(canvas, "ZoomBuyBundle_0");
                Assert.IsFalse(buy.interactable, "pending purchase immediately disables duplicate input");
                Assert.AreEqual(0, controller.Store.View.coins, "no optimistic charge");
                Assert.AreEqual(CommandStatus.AlreadyPending, controller.BuyInformation("AI", 0));
                Assert.AreEqual(1, client.Sent.Count);
                var command = client.Sent.Single();
                Assert.AreEqual("AI", command.payload.companyId); Assert.AreEqual(0, command.payload.bundleIndex);
                client.Receive(new ServerMessage { type = "COMMAND_RESULT", response = new CommandResult {
                    type = "BUY_INFORMATION_RESULT", requestId = command.requestId, turn = 1, success = true,
                    payload = new ResultPayload { companyId = "AI", bundleIndex = 0, startDay = 1,
                        remainingCoins = 0, source = BundleSources.Purchased, dailyChangesBps = new[] { 100, 200, 300, 400, 500 } } } });
                var chart = canvas.GetComponentsInChildren<BarChartGraphic>().Single(c => c.name == "ZoomChart_AI");
                Assert.IsTrue(Field<DayBar[]>(chart, "_bars")[5].Bps.HasValue, "purchased prices update without collapsing");
                StringAssert.Contains("구매함", buy.GetComponentInChildren<Text>().text);
                view = DtoCopy.Copy(controller.Store.View); view.ability.freeInformationCredits = 0;
                client.Receive(new ServerMessage { type = "PLAYER_VIEW", playerView = view });
                Assert.IsTrue(canvas.GetComponentsInChildren<Button>().Where(b => b.name.StartsWith("ZoomBuyBundle_")).All(b => !b.interactable), "no coins or credits disables every remaining bundle");
                state.phase = Protocol.Reveal; client.Receive(new ServerMessage { type = "PUBLIC_STATE", state = state });
                ClickPlanning(canvas, "Expand_AI");
                view.coins = 3; client.Receive(new ServerMessage { type = "PLAYER_VIEW", playerView = view });
                Assert.IsTrue(canvas.GetComponentsInChildren<Button>().Where(b => b.name.StartsWith("ZoomBuyBundle_")).All(b => !b.interactable), "coins cannot enable purchases outside information phase");
                yield return null;
            }
            finally { controller.Dispose(); UnityEngine.Object.Destroy(canvas); }
        }

        private static void CapturePlanningScreen(Canvas canvas, string name)
        {
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) return;
            var cameraObject = new GameObject("Planning capture", typeof(Camera));
            var camera = cameraObject.GetComponent<Camera>(); camera.enabled = false;
            camera.transform.position = new Vector3(0, 0, -10); camera.orthographic = true;
            camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = Palette.Background;
            var target = new RenderTexture(1280, 720, 24); target.Create(); camera.targetTexture = target;
            var previousMode = canvas.renderMode; var previousCamera = canvas.worldCamera; float previousDistance = canvas.planeDistance;
            var previousTarget = RenderTexture.active; Texture2D image = null;
            try
            {
                canvas.renderMode = RenderMode.ScreenSpaceCamera; canvas.worldCamera = camera; canvas.planeDistance = 1;
                Canvas.ForceUpdateCanvases();
                if (UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline == null) camera.Render();
                else
                {
                    var request = new UnityEngine.Rendering.RenderPipeline.StandardRequest { destination = target };
                    Assert.IsTrue(UnityEngine.Rendering.RenderPipeline.SupportsRenderRequest(camera, request));
                    UnityEngine.Rendering.RenderPipeline.SubmitRenderRequest(camera, request);
                }
                RenderTexture.active = target; image = new Texture2D(1280, 720, TextureFormat.RGB24, false);
                image.ReadPixels(new Rect(0, 0, 1280, 720), 0, 0); image.Apply();
                string directory = Path.Combine(Path.GetDirectoryName(Application.dataPath), "Logs", "PlanningScreens");
                Directory.CreateDirectory(directory); File.WriteAllBytes(Path.Combine(directory, name + ".png"), image.EncodeToPNG());
            }
            finally
            {
                canvas.renderMode = previousMode; canvas.worldCamera = previousCamera; canvas.planeDistance = previousDistance;
                Canvas.ForceUpdateCanvases(); RenderTexture.active = previousTarget; camera.targetTexture = null;
                if (image != null) UnityEngine.Object.Destroy(image);
                target.Release(); UnityEngine.Object.Destroy(target); UnityEngine.Object.Destroy(cameraObject);
            }
        }

        [UnityTest]
        public IEnumerator InvalidPort_ConnectionCancellation_CanHostAfterCancelledHandshake()
        {
            var ui = Create("Cancel UI");
            yield return null;
            Port(ui, 0);
            Click(ui, "Host");
            Assert.IsNull(Field<NetworkSession>(ui, "session"));
            Assert.IsTrue(ui.GetComponentsInChildren<Text>().Any(t => t.text.Contains("1~65535")));
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                Port(ui, ((IPEndPoint)listener.LocalEndpoint).Port);
                Click(ui, "Join");
                yield return Until(() => Field<NetworkSession>(ui, "session")?.Client != null, "TCP connected, waiting for WELCOME");
                Click(ui, "CancelConnection");
                Port(ui, FreePort());
                Click(ui, "Host");
                yield return Until(() => C(ui)?.Store.Public?.players.Length == 1, "cancelled operation cannot tear down new host");
                Assert.AreEqual("P1", C(ui).LocalPlayerId);
            }
            finally { listener?.Stop(); }
        }

        private static void DragChart(ChartPointerLayer pointer, int start, int end)
        {
            var rect = (RectTransform)pointer.transform;
            Vector2 Point(int day) => RectTransformUtility.WorldToScreenPoint(null, rect.TransformPoint(new Vector3(DayAxis.CenterX(day, rect.rect.xMin, rect.rect.width), rect.rect.center.y)));
            var e = new PointerEventData(EventSystem.current) { position = Point(start), button = PointerEventData.InputButton.Left };
            pointer.OnPointerDown(e); e.position = Point(end); pointer.OnDrag(e); pointer.OnPointerUp(e);
        }
        private static void RevealPublic(GameClientController c)
        {
            var state = c.Store.Public;
            if (state?.publicSelectionActive != true || state.publicSelectionPlayerId != c.LocalPlayerId || c.Store.HasPending(CommandTypes.RevealInformationWindow)) return;
            foreach (var company in c.Store.View.companies)
            {
                int start = Enumerable.Range(1, 26).FirstOrDefault(d => !InformationWindowRules.OverlapsPublic(state, company.companyId, d));
                if (start > 0) { c.SelectInformationWindow(company.companyId, start); return; }
            }
        }
        private static void DriveHuman(GameClientController c)
        {
            if (c.Store.Public?.publicSelectionActive == true) { RevealPublic(c); return; }
            var state = c.Store.Public;
            if (state.stockSystem && state.phase == StockRules.AugmentPhase)
            {
                var p = c.Store.View?.portfolio;
                if (p?.augmentChosen == false && p.candidates?.Length > 0) c.ChooseAugment(p.candidates[0]);
                return;
            }
            if (state.stockSystem && state.phase == Protocol.Selection)
            {
                if (!c.CanDraftShares()) return;
                var p = c.Store.View.portfolio;
                foreach (var company in state.shareMarket.Where(v => v.companyId != "BANK"))
                {
                    int own = company.owners.Single(v => v.playerId == c.LocalPlayerId).shares;
                    int available = 20 - company.owners.Sum(v => v.shares);
                    int target = Math.Min(p.LimitFor(company.companyId), own + Math.Min(available, Math.Min(p.CashSlots, p.draftSharesRemaining)));
                    if (target > own) { c.TradeShares(new[] { new ShareOrder { companyId = company.companyId, targetShares = target } }); return; }
                }
                c.PassStockDraft(); return;
            }
            if (state.stockSystem && state.phase == Protocol.Investment)
            {
                if (!c.Store.OrderLocked && !c.Store.HasPending(CommandTypes.SubmitInvestment)) c.SubmitInvestment(c.CurrentTradeReservations());
                return;
            }
            if (state.phase == Protocol.Info || state.phase == Protocol.Reveal)
            { if (c.CanFinishPhase()) c.FinishPhase(); }
            else if (state.phase == Protocol.Selection && c.IsMyTurnToSelect() && !c.Store.HasPending(CommandTypes.SelectCompany))
            {
                var pick = new[] { "BANK", "FOOD", "AUTO", "AI", "SPACE", "BIO" }.FirstOrDefault(c.CanSelect);
                if (pick != null) c.SelectCompany(pick);
            }
            else if (state.phase == Protocol.Investment && !c.Store.OrderLocked && !c.Store.HasPending(CommandTypes.SubmitInvestment))
            {
                var draft = InvestmentDraft.FromSelections(c.Store.OwnSelections);
                if (draft == null) return;
                draft.TryAddInterval(draft.FirstCompanyId, 10, 15);
                draft.TryAddInterval(draft.SecondCompanyId, 5, 10);
                c.SubmitInvestment(draft.ToInvestments());
            }
        }

        private static DevTabletBootstrap Create(string name) => new GameObject(name).AddComponent<DevTabletBootstrap>();
        private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target);
        private static GameClientController C(DevTabletBootstrap root) => Field<GameClientController>(root, "controller");
        private static void Port(DevTabletBootstrap root, int number) => root.GetComponentsInChildren<InputField>().Single(f => f.name == "Port").text = number.ToString();
        private static void Click(DevTabletBootstrap root, string name)
        {
            var button = root.GetComponentsInChildren<Button>().Single(b => b.name == name);
            Assert.IsTrue(button.interactable, name + " enabled");
            button.onClick.Invoke();
        }
        private static IEnumerator Until(Func<bool> predicate, string message)
        {
            var deadline = Time.realtimeSinceStartup + 15;
            while (!predicate())
            {
                Assert.Less(Time.realtimeSinceStartup, deadline, message);
                yield return null;
            }
            // One frame for a just-created view to consume the remaining state messages.
            yield return null;
        }
        private static int FreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int number = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return number;
        }
    }
}
