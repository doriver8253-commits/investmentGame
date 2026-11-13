// 화면 버튼 -> A의 Command 생성 -> IGameClient.SendCommand
// IGameClient.MessageReceived -> 상태 저장소 반영
// 방장도 다른 참가자와 같은 IGameClient 경로를 쓴다(A: 호스트도 동일한 TCP 클라이언트 사용).
// B·C를 화면에서 직접 호출하지 않는다.
using System;
using System.Linq;
using BlackWhale.Shared;
using BlackWhale.UI.Presentation;

namespace BlackWhale.UI.State
{
    public enum CommandStatus
    {
        Sent,
        AlreadyPending,    // 같은 요청이 응답 대기 중(재클릭 방지)
        BlockedLocally,    // 화면에서 미리 막음(최종 판정은 서버)
        NoState
    }

    public sealed class GameClientController : IDisposable
    {
        private readonly IGameClient _client;
        private int _requestCounter;

        public ClientStateStore Store { get; }
        public string LocalPlayerId => Store.LocalPlayerId;
        public bool HasCurrentPlayerView => Store.Public != null && Store.View?.turn == Store.Public.turn;
        /// <summary>BlockedLocally일 때의 사유(공통 오류 코드).</summary>
        public string LastLocalBlockCode { get; private set; } = "";

        public GameClientController(IGameClient client, ClientStateStore store = null)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            Store = store ?? new ClientStateStore();
            _client.MessageReceived += OnMessage;
            Store.TradePlans.ReservationsChanged += SyncReservations;
        }

        public void Dispose()
        { _client.MessageReceived -= OnMessage; Store.TradePlans.ReservationsChanged -= SyncReservations; }

        private void OnMessage(ServerMessage message) => Store.Apply(message);

        private void SyncReservations()
        {
            var s = Store.Public;
            if (s == null || !s.windowInformation || s.paused || Store.OrderLocked ||
                (s.phase != Protocol.Info && s.phase != Protocol.Selection && s.phase != Protocol.Investment)) return;
            if (s.stockSystem && !HasCurrentPlayerView) return;
            if (s.stockSystem && s.phase == Protocol.Selection && !IsMyTurnToSelect()) return;
            Send(CommandTypes.SaveTradePlan, new CommandPayload { investments = CurrentTradeReservations() }, notify: false);
        }

        public Investment[] CurrentTradeReservations()
        {
            var plans = Store.TradePlans.ToReservations();
            if (Store.Public?.stockSystem != true) return plans;
            return plans.Where(i => i.companyId == "BANK" ? i.intervals?.Length > 0 : (Store.Public.phase != Protocol.Investment ||
                Store.View?.portfolio?.holdings?.Any(h => h.companyId == i.companyId && h.shares > 0) == true)).ToArray();
        }

        public bool CanPlanTrade(string companyId)
        {
            var s = Store.Public;
            if (s == null || !HasCurrentPlayerView || s.paused || s.publicSelectionActive || Store.OrderLocked || !string.IsNullOrEmpty(Store.EndReason)) return false;
            if (s.stockSystem) return (s.phase == Protocol.Info ||
                s.phase == Protocol.Selection && IsMyTurnToSelect() || s.phase == Protocol.Investment &&
                (companyId == "BANK" || Store.View?.portfolio?.holdings?.Any(h => h.companyId == companyId && h.shares > 0) == true));
            return s.phase == Protocol.Info || s.phase == Protocol.Selection;
        }

        // ------------------------------------------------------------------
        // 대기실
        // ------------------------------------------------------------------

        public CommandStatus SetReady(bool ready)
        {
            if (Store.Public == null) return CommandStatus.NoState;
            if (Store.HasPending(CommandTypes.SetReady)) return CommandStatus.AlreadyPending;
            Send(CommandTypes.SetReady, new CommandPayload { ready = ready });
            return CommandStatus.Sent;
        }

        /// <summary>방장(P1)만 가능. 4명 모두 준비해야 서버가 받아 준다.</summary>
        public CommandStatus StartGame()
        {
            if (Store.Public == null) return CommandStatus.NoState;
            if (Store.HasPending(CommandTypes.StartGame)) return CommandStatus.AlreadyPending;
            Send(CommandTypes.StartGame, new CommandPayload());
            return CommandStatus.Sent;
        }

        public bool IsHost => LocalPlayerId == "P1";
        public CommandStatus GetHistory(int turn)
        {
            if (Store.Public == null) return CommandStatus.NoState;
            if (turn < 1 || turn > Store.CurrentTurn) return Block("HISTORY_UNAVAILABLE");
            if (Store.HasPending("GET_HISTORY")) return CommandStatus.AlreadyPending;
            Send("GET_HISTORY", new CommandPayload { historyTurn = turn }); return CommandStatus.Sent;
        }
        public CommandStatus SelectAbility(string abilityId)
        {
            if (Store.Public == null) return CommandStatus.NoState;
            if (Store.Public.paused) return Block(ErrorCodes.GamePaused);
            if (Store.Public.phase != Protocol.AbilitySelection) return Block(ErrorCodes.WrongPhase);
            if (Store.HasPending(CommandTypes.SelectAbility)) return CommandStatus.AlreadyPending;
            Send(CommandTypes.SelectAbility, new CommandPayload { abilityId = abilityId }); return CommandStatus.Sent;
        }
        public CommandStatus SubmitBid(long amount, string[] seatPreferences = null, bool useFreePass = false)
        {
            if (Store.Public == null) return CommandStatus.NoState;
            if (Store.Public.paused) return Block(ErrorCodes.GamePaused);
            if (Store.Public.phase != Protocol.Auction) return Block(ErrorCodes.WrongPhase);
            if (Store.HasPending(CommandTypes.SubmitBid)) return CommandStatus.AlreadyPending;
            Send(CommandTypes.SubmitBid, new CommandPayload { bidAmount = amount, seatPreferences = (string[])seatPreferences?.Clone(), useFreePass = useFreePass });
            return CommandStatus.Sent;
        }

        public bool AllPlayersReady
            => Store.Public?.players != null && Store.Public.players.Length == 4
               && Store.Public.players.All(p => p.connected && p.ready);

        public bool IsLocalReady
            => Store.Public?.players?.FirstOrDefault(p => p.playerId == LocalPlayerId)?.ready ?? false;

        // ------------------------------------------------------------------
        // 게임 명령
        // ------------------------------------------------------------------

        public CommandStatus BuyInformation(string companyId, int bundleIndex)
        {
            if (Store.View == null) return CommandStatus.NoState;
            if (Store.Public?.paused == true) return Block(ErrorCodes.GamePaused);
            if (Store.IsPurchasePending(companyId, bundleIndex)) return CommandStatus.AlreadyPending;
            if (IsVisible(companyId, bundleIndex)) return Block(ErrorCodes.AlreadyVisible);

            Send(CommandTypes.BuyInformation, new CommandPayload { companyId = companyId, bundleIndex = bundleIndex },
                ClientStateStore.PurchaseKey(companyId, bundleIndex));
            return CommandStatus.Sent;
        }

        public CommandStatus SelectCompany(string companyId)
        {
            if (Store.Public == null) return CommandStatus.NoState;
            if (Store.Public.paused) return Block(ErrorCodes.GamePaused);
            if (Store.HasPending(CommandTypes.SelectCompany)) return CommandStatus.AlreadyPending;
            if (!IsMyTurnToSelect()) return Block(ErrorCodes.NotYourTurn);

            Send(CommandTypes.SelectCompany, new CommandPayload { companyId = companyId }, companyId);
            return CommandStatus.Sent;
        }
        public CommandStatus TradeShares(ShareOrder[] orders)
        {
            if (!CanDraftShares()) return Block(ErrorCodes.WrongPhase);
            Send(StockRules.Trade, new CommandPayload { shareOrders = orders?.Select(v => new ShareOrder { companyId = v.companyId, targetShares = v.targetShares }).ToArray() });
            return CommandStatus.Sent;
        }
        public bool CanDraftShares() => HasCurrentPlayerView && Store.Public?.stockSystem == true && IsMyTurnToSelect() && !Store.Public.paused &&
            !Store.HasPending(StockRules.Trade) && !Store.HasPending(StockRules.Pass);
        public CommandStatus PassStockDraft()
        {
            if (!CanDraftShares()) return Block(ErrorCodes.WrongPhase);
            Send(StockRules.Pass, new CommandPayload()); return CommandStatus.Sent;
        }
        public CommandStatus ChooseAugment(string id, string companyId = "AI")
        {
            if (Store.Public?.phase != StockRules.AugmentPhase || Store.Public.paused || Store.View?.portfolio == null || Store.View.portfolio.augmentChosen || Store.HasPending(StockRules.Choose)) return Block(ErrorCodes.WrongPhase);
            Send(StockRules.Choose, new CommandPayload { augmentId = id, companyId = companyId }); return CommandStatus.Sent;
        }
        public CommandStatus UseDraftPriority()
        {
            if (Store.Public?.phase != Protocol.Selection || Store.Public.paused || Store.HasPending(StockRules.Priority)) return Block(ErrorCodes.WrongPhase);
            Send(StockRules.Priority, new CommandPayload()); return CommandStatus.Sent;
        }

        public CommandStatus SubmitInvestment(Investment[] investments)
        {
            if (!HasCurrentPlayerView) return CommandStatus.NoState;
            if (Store.Public?.paused == true) return Block(ErrorCodes.GamePaused);
            if (Store.HasPending(CommandTypes.SubmitInvestment)) return CommandStatus.AlreadyPending;
            if (Store.OrderLocked) return Block(ErrorCodes.OrderLocked);

            Send(CommandTypes.SubmitInvestment, new CommandPayload { investments = DtoCopy.Copy(investments) });
            return CommandStatus.Sent;
        }

        /// <summary>완료 버튼(COMPLETE_PHASE). 의도만 보내고 단계 진행은 A가 결정한다.</summary>
        public CommandStatus FinishPhase()
        {
            var s = Store.Public;
            if (s == null) return CommandStatus.NoState;
            if (s.paused) return Block(ErrorCodes.GamePaused);
            if (!CanFinishPhase()) return Block(ErrorCodes.WrongPhase);
            var requestId = Send(CommandTypes.CompletePhase, new CommandPayload());
            Store.MarkPhaseCompleted(requestId, s.turn, s.phase);
            return CommandStatus.Sent;
        }

        public bool CanCompleteStep()
        {
            var s = Store.Public;
            if (s?.developerMode == true && s.stockSystem)
            {
                if (s.phase == Protocol.Selection) return CanDraftShares();
                if (s.phase == Protocol.Investment) return HasCurrentPlayerView && !s.paused && !Store.OrderLocked &&
                    !Store.HasPending(CommandTypes.SubmitInvestment);
            }
            return CanFinishPhase();
        }

        public CommandStatus CompleteStep()
        {
            if (!CanCompleteStep()) return Block(ErrorCodes.WrongPhase);
            var s = Store.Public;
            if (s.developerMode && s.stockSystem && s.phase == Protocol.Selection) return PassStockDraft();
            if (s.developerMode && s.stockSystem && s.phase == Protocol.Investment) return SubmitInvestment(CurrentTradeReservations());
            return FinishPhase();
        }

        public bool IsDoneForCurrentPhase
            => Store.Public != null && Store.IsPhaseCompleted(Store.Public.turn, Store.Public.phase);

        /// <summary>A 규칙: COMPLETE_PHASE는 INFO·시장 공개·결과 단계에서만. 투자 단계는 4명 주문이 잠기면 자동 진행.</summary>
        public bool CanFinishPhase()
        {
            var s = Store.Public;
            if (s == null || s.paused || s.publicSelectionActive || IsDoneForCurrentPhase) return false;
            if (s.stockSystem && !HasCurrentPlayerView) return false;
            return s.phase == Protocol.Info || s.phase == Protocol.Reveal || s.phase == Protocol.Result ||
                (s.phase == Protocol.AbilitySelection && IsHost);
        }

        // ------------------------------------------------------------------
        // 화면 조회 도우미
        // ------------------------------------------------------------------

        public bool IsMyTurnToSelect()
        {
            var s = Store.Public;
            return s != null && !s.paused && s.phase == Protocol.Selection && s.currentPlayerId == LocalPlayerId
                   && !string.IsNullOrEmpty(LocalPlayerId);
        }

        public int RemainingSeats(string companyId)
            => Store.Public?.companySeats?.FirstOrDefault(c => c.companyId == companyId)?.remainingSeats ?? 0;

        /// <summary>선택 버튼 활성 여부(화면 편의용). 서버가 최종 판정.</summary>
        public bool CanSelect(string companyId)
            => IsMyTurnToSelect() && RemainingSeats(companyId) > 0 && !Store.OwnSelections.Contains(companyId)
               && !Store.HasPending(CommandTypes.SelectCompany);

        public bool IsVisible(string companyId, int bundleIndex)
            => Store.View?.companies?.FirstOrDefault(c => c.companyId == companyId)?
                   .visibleBundles?.Any(b => b.bundleIndex == bundleIndex) ?? false;

        public CommandStatus SelectInformationWindow(string companyId, int startDay, int endDay = 0)
        {
            var s = Store.Public;
            if (s == null || !HasCurrentPlayerView) return CommandStatus.NoState;
            if (s.paused) return Block(ErrorCodes.GamePaused);
            if (s.phase != Protocol.Info) return Block(ErrorCodes.WrongPhase);
            if (endDay == 0) endDay = startDay + 4;
            if (startDay < 1 || endDay > 30 || endDay < startDay || endDay - startDay != 4) return Block(ErrorCodes.InformationWindowNotFiveDays);
            if (s.publicSelectionActive
                ? InformationWindowRules.TooCloseToPublic(s, companyId, startDay)
                : InformationWindowRules.TooCloseToKnown(Store.View?.companies?.FirstOrDefault(c => c.companyId == companyId), startDay))
                return Block(ErrorCodes.InformationWindowTooClose);
            if (s.publicSelectionActive && s.publicSelectionPlayerId != LocalPlayerId) return Block(ErrorCodes.NotYourTurn);
            string type = s.publicSelectionActive ? CommandTypes.RevealInformationWindow : CommandTypes.BuyInformationWindow;
            if (Store.HasPending(type)) return CommandStatus.AlreadyPending;
            Send(type, new CommandPayload { companyId = companyId, startDay = startDay, endDay = endDay });
            return CommandStatus.Sent;
        }
        public CommandStatus UseInformationItem(string itemId, string companyId = "", int startDay = 1, string targetPlayerId = "")
        {
            var s = Store.Public;
            if (s == null) return CommandStatus.NoState;
            if (s.paused) return Block(ErrorCodes.GamePaused);
            if (s.phase != Protocol.Info || s.publicSelectionActive) return Block("PUBLIC_SELECTION_PENDING");
            if (Store.HasPending(CommandTypes.UseInformationItem)) return CommandStatus.AlreadyPending;
            Send(CommandTypes.UseInformationItem, new CommandPayload { itemId = itemId, companyId = companyId,
                startDay = startDay, targetPlayerId = targetPlayerId }); return CommandStatus.Sent;
        }

        // ------------------------------------------------------------------
        // 내부
        // ------------------------------------------------------------------

        public CommandStatus RefreshPlayerView()
        {
            if (Store.Public == null || Store.Public.turn == 0) return CommandStatus.NoState;
            if (Store.HasPending("GET_PLAYER_VIEW")) return CommandStatus.AlreadyPending;
            Send("GET_PLAYER_VIEW", new CommandPayload(), notify: false);
            return CommandStatus.Sent;
        }

        private string Send(string type, CommandPayload payload, string pendingKey = "", bool notify = true)
        {
            int turn = Store.CurrentTurn;
            var command = new Command
            {
                type = type,
                requestId = NextRequestId(type, turn),
                turn = turn,
                payload = payload
            };
            Store.MarkPending(command.requestId, type, turn, pendingKey, notify);
            _client.SendCommand(command);
            return command.requestId;
        }

        private CommandStatus Block(string code)
        {
            LastLocalBlockCode = code;
            return CommandStatus.BlockedLocally;
        }

        private string NextRequestId(string type, int turn)
        {
            _requestCounter++;
            string prefix = type == CommandTypes.BuyInformation ? "info"
                : type == CommandTypes.SelectCompany ? "pick"
                : type == CommandTypes.SubmitInvestment ? "order"
                : type.ToLowerInvariant();
            // A 제한: requestId 64자 이하
            return prefix + "-t" + turn + "-" + _requestCounter.ToString("000");
        }
    }
}
