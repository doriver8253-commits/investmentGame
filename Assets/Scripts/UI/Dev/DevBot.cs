using System;
using System.Collections.Generic;
using System.Linq;
using BlackWhale.Bots;
using BotDifficulty = BlackWhale.Bots.BotDifficulty;
using BlackWhale.Shared;
using BlackWhale.UI.Presentation;
using BlackWhale.UI.State;

namespace BlackWhale.UI.Dev
{
    // Host-owned lifetime, but only the same private/public messages as a human client.
    public sealed class DevBot
    {
        private readonly GameClientController controller;
        private readonly float thinkSeconds;
        private readonly int seed;
        private InformationBotStrategy strategy;
        private BotDifficulty difficulty;
        private BotPersonality personality;
        private float actionDelay;
        private readonly PhaseClock clock = new PhaseClock();
        private PublicState syncedState;
        private string stateKey = "";
        private int currentTurn = -1, currentRevision = -1, purchases, failures, orderAttempts;
        private float since;
        private readonly HashSet<string> attempted = new HashSet<string>();
        public IGameClient Client { get; }
        public GameClientController Controller => controller;
        public DevBot(IGameClient client, float thinkSeconds, int seed = 1, BotDifficulty difficulty = BotDifficulty.Normal, BotPersonality personality = BotPersonality.Strategic)
        {
            Client = client; controller = new GameClientController(client); this.thinkSeconds = thinkSeconds;
            this.seed = seed; this.difficulty = difficulty; this.personality = personality;
            strategy = new InformationBotStrategy(seed, new BotPolicy(difficulty, personality));
            Client.MessageReceived += OnResult;
        }
        public void Dispose() { Client.MessageReceived -= OnResult; controller.Dispose(); Client.Dispose(); }
        private void OnResult(ServerMessage message)
        {
            var r = message.type == "COMMAND_RESULT" ? message.response : null;
            if (r == null || r.turn != currentTurn) return;
            if ((r.type == "BUY_INFORMATION_RESULT" || r.type == "BUY_INFORMATION_WINDOW_RESULT") && !r.success) failures++;
        }
        public bool SetDifficulty(BotDifficulty value)
        {
            if (controller.Store.Public?.phase != Protocol.Lobby) return false;
            difficulty = value; strategy = new InformationBotStrategy(seed, new BotPolicy(value, personality)); return true;
        }
        public void Update(float now)
        {
            var s = controller.Store.Public;
            if (s == null || !string.IsNullOrEmpty(controller.Store.EndReason)) return;
            if (s.paused) { since = now; return; }
            if (!ReferenceEquals(syncedState, s)) { syncedState = s; clock.Sync(s.serverElapsedSeconds, now); }
            {
                // The server freezes profiles at game start; also consume the first in-game snapshot.
                var profile = s.players?.FirstOrDefault(p => p.playerId == controller.LocalPlayerId && p.isBot);
                if (profile != null && Enum.TryParse(profile.botDifficulty, out BotDifficulty selected) &&
                    Enum.TryParse(profile.botPersonality, out BotPersonality style) && (selected != difficulty || style != personality)) {
                    personality = style; difficulty = selected; strategy = new InformationBotStrategy(seed, new BotPolicy(selected, style));
                }
            }
            if (currentTurn != s.turn || currentRevision != s.developerRevision) { currentTurn = s.turn; currentRevision = s.developerRevision; purchases = failures = orderAttempts = 0; attempted.Clear(); }
            string key = s.turn + "|" + s.phase + "|" + s.selectionIndex + "|" + s.currentPlayerId + "|" + s.developerRevision;
            if (key != stateKey) { stateKey = key; since = now; actionDelay = s.stockSystem ? strategy.NextReactionSeconds() : 0; }
            if (now - since < thinkSeconds + (s.stockSystem && s.phase == Protocol.Investment ? 0 : actionDelay)) return;
            var view = controller.Store.View;
            if (s.phase != Protocol.Lobby && (view == null || view.turn != s.turn)) return;
            if (s.stockSystem && s.phase == StockRules.AugmentPhase)
            {
                if (view?.portfolio?.augmentChosen == false && view.portfolio.candidates?.Length > 0)
                    controller.ChooseAugment(strategy.SelectAugment(view), strategy.RankStockCompanies(view, s).FirstOrDefault() ?? "AI");
                return;
            }
            if (s.stockSystem && s.phase == Protocol.Selection)
            {
                if (!controller.CanDraftShares()) return;
                var p = view.portfolio;
                var ranks = strategy.RankStockCompanies(view, s);
                var ranked = ranks.Select(id => s.shareMarket.First(c => c.companyId == id));
                int budget = Math.Min(p.CashSlots, p.draftSharesRemaining);
                var shareOrders = new System.Collections.Generic.List<ShareOrder>();
                foreach (var c in ranked.Where(company => company.companyId != "BANK"))
                {
                    int own = p.holdings.First(v => v.companyId == c.companyId).shares;
                    int remaining = StockRules.Supply - c.owners.Sum(v => v.shares + v.reserved);
                    int added = Math.Max(0, Math.Min(p.LimitFor(c.companyId) - own, Math.Min(remaining, budget)));
                    if (added == 0) continue;
                    shareOrders.Add(new ShareOrder { companyId = c.companyId, targetShares = own + added }); budget -= added;
                }
                if (shareOrders.Count > 0) { controller.TradeShares(shareOrders.ToArray()); since = now; return; }
                controller.PassStockDraft(); since = now; return;
            }
            if (s.stockSystem && s.phase == Protocol.Investment)
            {
                if (controller.Store.HasPending(StockRules.Position)) return;
                foreach (var id in strategy.RankStockCompanies(view, s))
                {
                    var holding = view.portfolio.holdings.FirstOrDefault(h => h.companyId == id);
                    if (holding == null) continue;
                    // 교환 턴에는 StockTarget이 '내릴 것 같은 기업'을 고르도록 알아서 뒤집는다.
                    int target = strategy.StockTarget(view, s, id, now);
                    if (holding.activeShares == target) continue;
                    controller.SetStockPosition(id, target); break;
                }
                since = now; return;
            }
            switch (s.phase)
            {
                case Protocol.Lobby:
                    if (!controller.IsLocalReady && !controller.Store.HasPending(CommandTypes.SetReady)) controller.SetReady(true);
                    else if (controller.IsHost && controller.AllPlayersReady && !controller.Store.HasPending(CommandTypes.StartGame)) controller.StartGame();
                    break;
                case Protocol.AbilitySelection:
                    if (string.IsNullOrEmpty(view.ability?.selectedId) && view.ability?.candidates?.Length > 0 && !controller.Store.HasPending("SELECT_ABILITY"))
                        controller.SelectAbility(view.ability.candidates[0]);
                    break;
                case Protocol.Auction:
                    if (view.auction != null && !view.auction.confirmed && !controller.Store.HasPending("SUBMIT_BID")) controller.SubmitBid(0);
                    break;
                case Protocol.Info:
                    if (s.publicSelectionActive)
                    {
                        if (s.publicSelectionPlayerId == controller.LocalPlayerId && !controller.Store.HasPending(CommandTypes.RevealInformationWindow))
                        {
                            var choice = strategy.NextPublicChoice(view, s);
                            if (choice != null) controller.SelectInformationWindow(choice.CompanyId, choice.StartDay);
                        }
                        break;
                    }
                    if (controller.Store.HasPending(CommandTypes.BuyInformation) || controller.Store.HasPending(CommandTypes.BuyInformationWindow) || controller.IsDoneForCurrentPhase) break;
                    if (purchases < strategy.Policy.PurchaseBudget && failures < 3 &&
                        (!PhaseClock.HasDeadline(s) || clock.RemainingSeconds(s, now) > 2))
                    {
                        var buy = s.stockSystem ? strategy.NextRandomPurchase(view, s.lobbySettings) : strategy.NextPurchase(view, attempted);
                        if (buy != null)
                        {
                            attempted.Add(buy.CompanyId + ":" + (s.windowInformation ? buy.StartDay : buy.BundleIndex)); purchases++;
                            if (s.stockSystem) controller.BuyRandomInformation(buy.CompanyId);
                            else if (s.windowInformation) controller.SelectInformationWindow(buy.CompanyId, buy.StartDay, buy.EndDay);
                            else controller.BuyInformation(buy.CompanyId, buy.BundleIndex);
                            break;
                        }
                    }
                    if (controller.CanFinishPhase()) controller.FinishPhase();
                    break;
                case Protocol.Reveal:
                case Protocol.Result:
                    if (controller.CanFinishPhase()) controller.FinishPhase();
                    break;
                case Protocol.Selection:
                    if (controller.IsMyTurnToSelect() && !controller.Store.HasPending(CommandTypes.SelectCompany))
                    {
                        string pick = strategy.SelectCompany(view, s);
                        if (pick != null && controller.CanSelect(pick)) controller.SelectCompany(pick);
                        since = now;
                    }
                    break;
                case Protocol.Investment:
                    if (!controller.Store.OrderLocked && !controller.Store.HasPending(CommandTypes.SubmitInvestment) && orderAttempts < 2)
                    {
                        var order = strategy.BuildOrder(view, orderAttempts > 0);
                        if (order != null) { orderAttempts++; controller.SubmitInvestment(order); }
                        since = now;
                    }
                    break;
            }
        }
    }
}
