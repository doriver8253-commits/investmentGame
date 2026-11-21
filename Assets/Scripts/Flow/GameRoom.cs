using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using BlackWhale.Shared;

namespace BlackWhale.Flow
{
    public sealed class MonotonicClock : IServerClock
    {
        private readonly Stopwatch watch = Stopwatch.StartNew();
        public double Seconds { get { return watch.Elapsed.TotalSeconds; } }
    }
    public enum TimerMode { Realtime, ManualForFixture, Developer }
    public sealed class Delivery
    {
        public readonly string PlayerId;
        public readonly ServerMessage Message;
        public Delivery(string playerId, ServerMessage message) { PlayerId = playerId; Message = message; }
    }

    // One room, one synchronization boundary. B/C calls occur inside this boundary.
    // Transport never executes callbacks while the room lock is held.
    public sealed partial class GameRoom
    {
        private sealed class Member
        {
            public string Id, Name, Connection, Token;
            public bool Ready, IsBot;
            public double DisconnectedAt;
        }
        private sealed class Cached
        {
            public string Fingerprint;
            public CommandResult Result;
        }
        private readonly object gate = new object();
        private readonly IMarketService market;
        private readonly IInformationService information;
        private readonly IServerClock clock;
        private readonly TimerMode mode;
        private readonly bool fixedOrder;
        private readonly GameSettings settings;
        private readonly IProgressionService progression;
        private string[] initialOrder = new string[0], seatsForFinalTurns;
        private string botDifficulty = "Normal";
        private LobbySettings lobbySettings = new LobbySettings();
        private Scoreboard latestBoard;
        private readonly List<Member> members = new List<Member>();
        private readonly Queue<Delivery> outbox = new Queue<Delivery>();
        private readonly Dictionary<string, Cached> cache = new Dictionary<string, Cached>();
        private readonly Dictionary<string, int> counts = new Dictionary<string, int>();
        private readonly HashSet<string> completed = new HashSet<string>();
        private readonly HashSet<int> begun = new HashSet<int>(), settled = new HashSet<int>();
        private string[] order = new string[0];
        private readonly string roomId;
        private string phase = Protocol.Lobby;
        private int turn, selectionIndex;
        private readonly Random publicRandom = new Random();
        private bool TimedPhase => mode != TimerMode.Developer || StockMode && phase == Protocol.Investment && developerTimerRunning;
        private double origin, phaseEnd, pickEnd;
        private double lastPublishedAt;
        private int publicPickIndex;
        private double publicPickEnd;
        private IWindowInformationService WindowInfo => information as IWindowInformationService;
        private bool WindowMode => WindowInfo?.UsesWindowInformation == true;
        private bool PublicPicking => WindowMode && phase == Protocol.Info && publicPickIndex < 4;
        private double NextDeadline => PublicPicking ? publicPickEnd : phase == Protocol.Selection ? pickEnd : phaseEnd;
        private bool ordersLocked;
        private bool paused;
        private double pausedAt, pausedSeconds;
        private static readonly string[] DefaultCompanies = { "BANK", "FOOD", "AUTO", "AI", "SPACE", "BIO" };

        public GameRoom(string roomId, IMarketService market, IInformationService information,
            IServerClock clock, TimerMode mode = TimerMode.Realtime, bool fixedOrderForFixture = false,
            GameSettings settings = null)
        {
            if (string.IsNullOrWhiteSpace(roomId)) throw new ArgumentException("Room ID is required.");
            this.roomId = roomId;
            this.market = market ?? throw new ArgumentNullException(nameof(market));
            this.information = information ?? throw new ArgumentNullException(nameof(information));
            this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
            this.mode = mode;
            fixedOrder = fixedOrderForFixture;
            this.settings = settings ?? (StockMode ? GameSettings.StockGame : new GameSettings());
            progression = !StockMode && this.settings.EnableProgressionFeatures ? information as IProgressionService : null;
            if (!StockMode && this.settings.EnableProgressionFeatures && (progression == null || !(market is IMarketProgression)))
                throw new ArgumentException("Production games require B/C progression services; use GameSettings.Fixture for fixture services.");
        }
        private double Now { get { return (paused ? pausedAt : clock.Seconds) - origin - pausedSeconds; } }
        // Host-process control, deliberately absent from the network command protocol.
        public bool SetPaused(bool value)
        {
            lock (gate)
            {
                if (phase == Protocol.Lobby || phase == Protocol.Ended || paused == value) return false;
                double current = clock.Seconds;
                if (value)
                {
                    if (mode == TimerMode.Realtime) AdvanceTo(current - origin - pausedSeconds);
                    if (phase == Protocol.Ended) return false;
                    pausedAt = current;
                }
                else pausedSeconds += current - pausedAt;
                paused = value;
                PublishState();
                return true;
            }
        }
        private RequestContext Context(string player = "", string request = "")
        { return new RequestContext(roomId, turn, phase, player, request); }
        private string CurrentPlayer { get { return phase == Protocol.Selection && selectionIndex < order.Length ? order[selectionIndex] : ""; } }
        public bool SetBotDifficulty(string value)
        {
            lock (gate)
            {
                if (phase != Protocol.Lobby || (value != "Easy" && value != "Normal" && value != "Hard")) return false;
                botDifficulty = value; PublishState(); return true;
            }
        }

        // Tokens are generated on the server and are never included in a public roster.
        public string Join(string connectionId, string displayName, string reconnectToken = null)
        {
            lock (gate)
            {
                if (string.IsNullOrEmpty(connectionId) || members.Any(m => m.Connection == connectionId)) return "INVALID_CONNECTION";
                if (phase == Protocol.Ended) return "ROOM_ENDED";
                if (!string.IsNullOrEmpty(reconnectToken))
                {
                    Member returning = members.Find(m => m.Token == reconnectToken);
                    if (returning == null || returning.Connection != null || clock.Seconds >= returning.DisconnectedAt + 30)
                        return "INVALID_RECONNECT_TOKEN";
                    returning.Connection = connectionId;
                    Welcome(returning);
                    PublishState();
                    if (turn > 0 || phase == Protocol.AbilitySelection) SendView(returning.Id);
                    if (latestBoard != null) Send(returning.Id, new ServerMessage { type = "SCOREBOARD", scoreboard = latestBoard });
                    if (phase == Protocol.Reveal || phase == Protocol.Result)
                        Send(returning.Id, new ServerMessage { type = "MARKET_REVEAL", revealedMarket = market.GetRevealedMarket(Context()) });
                    return "";
                }
                if (phase != Protocol.Lobby) return "GAME_STARTED";
                if (members.Count == 4) return "ROOM_FULL";
                if (string.IsNullOrWhiteSpace(displayName) || displayName.Length > 32) return "INVALID_NAME";
                string id = Enumerable.Range(1, 4).Select(i => "P" + i).First(candidate => members.All(m => m.Id != candidate));
                var member = new Member { Id = id, Name = displayName,
                    Connection = connectionId, Token = NewToken() };
                members.Add(member);
                Welcome(member);
                PublishState();
                return "";
            }
        }

        // Host-process administration only. These methods are never exposed as wire commands.
        public bool RegisterLocalBot(string playerId)
        {
            lock (gate)
            {
                var member = members.Find(m => m.Id == playerId && m.Id != "P1" && m.Connection != null);
                if (phase != Protocol.Lobby || member == null) return false;
                member.IsBot = true;
                PublishState();
                return true;
            }
        }
        public bool RemoveLobbyBot(string playerId)
        {
            lock (gate)
            {
                var member = members.Find(m => m.Id == playerId && m.IsBot);
                if (phase != Protocol.Lobby || member == null) return false;
                RemoveLobbyMember(member);
                PublishState();
                return true;
            }
        }
        public void ReleaseDisconnectedLobbySlots()
        {
            lock (gate)
            {
                if (phase != Protocol.Lobby) return;
                foreach (var member in members.Where(m => m.Id != "P1" && m.Connection == null).ToArray()) RemoveLobbyMember(member);
                PublishState();
            }
        }
        private void RemoveLobbyMember(Member member)
        {
            members.Remove(member);
            // A new occupant of this ID must not receive the previous occupant's queued replies.
            var retained = outbox.Where(d => d.PlayerId != member.Id).ToArray();
            outbox.Clear();
            foreach (var delivery in retained) outbox.Enqueue(delivery);
            foreach (string key in cache.Keys.Where(k => k.StartsWith(member.Id + ":", StringComparison.Ordinal)).ToArray()) cache.Remove(key);
            foreach (string key in counts.Keys.Where(k => k.StartsWith(member.Id + ":", StringComparison.Ordinal)).ToArray()) counts.Remove(key);
        }
        private static string NewToken()
        {
            byte[] bytes = new byte[32];
            using (var random = RandomNumberGenerator.Create()) random.GetBytes(bytes);
            return Convert.ToBase64String(bytes);
        }
        private void Welcome(Member member)
        {
            Send(member.Id, new ServerMessage { type = "WELCOME", session = new Session {
                playerId = member.Id, reconnectToken = member.Token } });
        }
        public string PlayerForConnection(string connectionId)
        { lock (gate) return members.Find(m => m.Connection == connectionId)?.Id; }
        public string ConnectionForPlayer(string playerId)
        { lock (gate) return members.Find(m => m.Id == playerId)?.Connection; }

        public void Disconnect(string connectionId)
        {
            lock (gate)
            {
                Member member = members.Find(m => m.Connection == connectionId);
                if (member == null) return;
                member.Connection = null;
                member.DisconnectedAt = clock.Seconds;
                if (member.Id == "P1") End("HOST_DISCONNECTED");
                else
                {
                    if (PublicPicking && order[publicPickIndex] == member.Id) AutoPublicSelect(Now);
                    PublishState();
                }
            }
        }
        public void Shutdown()
        { lock (gate) if (phase != Protocol.Ended) End("HOST_DISCONNECTED"); }

        public CommandResult ProcessCommand(string connectionId, Command input)
        { return ProcessCommandAt(connectionId, input, clock.Seconds); }

        // Only the server transport supplies receipt time. This value is absent from the wire DTO.
        public CommandResult ProcessCommandAt(string connectionId, Command input, double receivedAtServerSeconds)
        {
            lock (gate)
            {
                Member member = members.Find(m => m.Connection == connectionId);
                Command command = Copy(input);
                if (member == null) return CommandResult.Fail(command, "NOT_CONNECTED");
                if (string.IsNullOrEmpty(command.requestId) || command.requestId.Length > 64 ||
                    string.IsNullOrEmpty(command.type) || command.type.Length > 40)
                    return Reply(member.Id, CommandResult.Fail(command, "INVALID_REQUEST"));
                string key = member.Id + ":" + command.turn + ":" + command.requestId;
                string fingerprint = Fingerprint(command);
                if (mode == TimerMode.Developer && StockMode && command.developerRevision != developerRevision)
                    return Reply(member.Id, CommandResult.Fail(command, "STALE_DEVELOPER_STATE"));
                Cached previous;
                // Replay is checked before stage, deadline, quota or ended-state checks.
                if (cache.TryGetValue(key, out previous))
                    return Reply(member.Id, previous.Fingerprint == fingerprint ? Copy(previous.Result)
                        : CommandResult.Fail(command, "REQUEST_ID_CONFLICT"));
                if (paused) return Reply(member.Id, CommandResult.Fail(command, ErrorCodes.GamePaused));
                string quotaKey = member.Id + ":" + turn;
                int count;
                counts.TryGetValue(quotaKey, out count);
                if (count >= 256) return Reply(member.Id, CommandResult.Fail(command, "RATE_LIMITED"));
                counts[quotaKey] = count + 1;
                double received = receivedAtServerSeconds - origin - pausedSeconds;
                bool expired = TimedPhase && phase != Protocol.Lobby && phase != Protocol.Ended && phaseEnd > 0 &&
                    received >= (PublicPicking && command.type == "REVEAL_INFORMATION_WINDOW" ? publicPickEnd :
                        phase == Protocol.Selection ? pickEnd : phaseEnd);
                if (mode != TimerMode.ManualForFixture) AdvanceTo(received);
                CommandResult result;
                if (command.turn != turn) result = CommandResult.Fail(command, "STALE_TURN");
                else if (phase == Protocol.Ended && command.type != "GET_HISTORY") result = CommandResult.Fail(command, "ROOM_ENDED");
                else if (expired) result = CommandResult.Fail(command, "DEADLINE_EXCEEDED");
                else result = Execute(member, command, received);
                // Snapshot before delivery so consumers cannot mutate the cached response.
                cache[key] = new Cached { Fingerprint = fingerprint, Result = Copy(result) };
                return Reply(member.Id, result);
            }
        }
        private CommandResult Execute(Member member, Command command, double now)
        {
            if (command.type.StartsWith("DEV_", StringComparison.Ordinal)) return ExecuteDeveloperCommand(member.Id, command, now);
            if (StockMode && (command.type == "SUBMIT_INVESTMENT" || command.type == "SAVE_TRADE_PLAN" && phase != Protocol.Info))
                return CommandResult.Fail(command, "WRONG_PHASE");
            if (StockMode && IsStockCommand(command.type)) return ExecuteStockCommand(member.Id, command, now);
            if (StockMode && (command.type == "SELECT_COMPANY" || command.type == "SUBMIT_BID" || command.type == "SELECT_ABILITY"))
                return CommandResult.Fail(command, "USE_STOCK_DRAFT");
            if (command.type == "GET_HISTORY")
            {
                int requested = command.payload?.historyTurn ?? 0;
                if (!(market is IMarketHistory history) || requested < 1 || requested > turn)
                    return CommandResult.Fail(command, "HISTORY_UNAVAILABLE");
                var board = history.GetScoreboard(requested);
                if (board == null) return CommandResult.Fail(command, "HISTORY_UNAVAILABLE");
                // The authenticated connection chooses the player; there is no target player field.
                var historyContext = new RequestContext(roomId, requested, Protocol.Result, member.Id, command.requestId);
                var own = information.BuildPlayerView(historyContext, history.GetOwnInvestmentState(member.Id, requested));
                Send(member.Id, new ServerMessage { type = "HISTORY_VIEW", history = new HistorySnapshot {
                    turn = requested, playerView = own, scoreboard = board } });
                return Success(command);
            }
            if (command.type == "SET_READY")
            {
                if (phase != Protocol.Lobby) return CommandResult.Fail(command, "WRONG_PHASE");
                if (command.payload == null) return CommandResult.Fail(command, "INVALID_REQUEST");
                member.Ready = command.payload.ready;
                PublishState();
                return Success(command);
            }
            if (command.type == "SET_LOBBY_SETTINGS")
            {
                if (phase != Protocol.Lobby) return CommandResult.Fail(command, "WRONG_PHASE");
                if (member.Id != "P1") return CommandResult.Fail(command, "HOST_ONLY");
                if (!StockMode || !WindowMode) return CommandResult.Fail(command, "WINDOWS_UNAVAILABLE");
                var options = command.payload?.lobbySettings;
                if (options == null || !options.IsValid) return CommandResult.Fail(command, "INVALID_LOBBY_SETTINGS");
                if (!lobbySettings.SameAs(options)) {
                    lobbySettings = options.Copy();
                    foreach (var participant in members) participant.Ready = false;
                }
                PublishState();
                return Success(command);
            }
            if (command.type == "START_GAME")
            {
                if (phase != Protocol.Lobby) return CommandResult.Fail(command, "WRONG_PHASE");
                if (member.Id != "P1") return CommandResult.Fail(command, "HOST_ONLY");
                if (members.Count != 4 || members.Any(m => m.Connection == null || !m.Ready))
                    return CommandResult.Fail(command, "NOT_ALL_READY");
                if (StockMode && WindowMode) WindowInfo.ConfigureRoom(roomId, lobbySettings);
                origin = clock.Seconds; pausedSeconds = 0;
                initialOrder = members.OrderBy(m => m.Id, StringComparer.Ordinal).Select(m => m.Id).ToArray();
                if (!fixedOrder)
                {
                    var random = new Random(BitConverter.ToInt32(Guid.NewGuid().ToByteArray(), 0));
                    for (int i = initialOrder.Length - 1; i > 0; i--)
                    { int j = random.Next(i + 1); string temp = initialOrder[i]; initialOrder[i] = initialOrder[j]; initialOrder[j] = temp; }
                }
                if (progression != null)
                {
                    progression.PrepareGame(roomId, initialOrder);
                    // No time limit has been agreed. With no configured limit, the host can finish
                    // selection explicitly; all four choices also advance immediately.
                    Enter(Protocol.AbilitySelection, 0, settings.AbilitySelectionSeconds ?? 0);
                    foreach (var m in members) SendView(m.Id);
                }
                else BeginTurn(0);
                return Success(command);
            }
            if (command.type == "SELECT_ABILITY")
            {
                if (phase != Protocol.AbilitySelection) return CommandResult.Fail(command, "WRONG_PHASE");
                if (command.payload == null) return CommandResult.Fail(command, "INVALID_REQUEST");
                var choice = progression.SelectAbility(Context(member.Id, command.requestId), command.payload.abilityId);
                if (!choice.Success) return CommandResult.Fail(command, choice.ErrorCode);
                SendView(member.Id);
                if (progression.AllAbilitiesSelected(roomId)) NextPhase(now);
                return Success(command, choice.Payload);
            }
            if (command.type == "SUBMIT_BID")
            {
                if (phase != Protocol.Auction) return CommandResult.Fail(command, "WRONG_PHASE");
                if (command.payload == null) return CommandResult.Fail(command, "INVALID_REQUEST");
                var bid = progression.SubmitBid(Context(member.Id, command.requestId), command.payload);
                if (!bid.Success) return CommandResult.Fail(command, bid.ErrorCode);
                SendView(member.Id);
                if (progression.AllBidsConfirmed(Context())) NextPhase(now);
                return Success(command, bid.Payload);
            }
            if (command.type == "COMPLETE_PHASE")
            {
                if (phase == Protocol.AbilitySelection)
                {
                    if (member.Id != "P1") return CommandResult.Fail(command, "HOST_ONLY");
                    NextPhase(now); return Success(command);
                }
                if (phase != Protocol.Info && phase != Protocol.Reveal && phase != Protocol.Result)
                    return CommandResult.Fail(command, "WRONG_PHASE");
                if (PublicPicking) return CommandResult.Fail(command, "PUBLIC_SELECTION_PENDING");
                completed.Add(member.Id);
                if (completed.Count == 4) NextPhase(now);
                return Success(command);
            }
            if (command.type == "GET_PLAYER_VIEW")
            {
                if (turn == 0 && phase != Protocol.AbilitySelection) return CommandResult.Fail(command, "WRONG_PHASE");
                Send(member.Id, new ServerMessage { type = "PUBLIC_STATE", state = Snapshot() });
                SendView(member.Id);
                return Success(command);
            }
            if (command.type == "REVEAL_INFORMATION_WINDOW" || command.type == "BUY_INFORMATION_WINDOW" || command.type == "USE_INFORMATION_ITEM")
            {
                if (!WindowMode) return CommandResult.Fail(command, "WINDOWS_UNAVAILABLE");
                if (phase != Protocol.Info) return CommandResult.Fail(command, "WRONG_PHASE");
                var payload = command.payload;
                if (payload == null) return CommandResult.Fail(command, "INVALID_REQUEST");
                var ctx = Context(member.Id, command.requestId);
                RuleResult operation;
                if (command.type == "REVEAL_INFORMATION_WINDOW")
                {
                    if (!PublicPicking) return CommandResult.Fail(command, "PUBLIC_SELECTION_COMPLETE");
                    if (member.Id != order[publicPickIndex]) return CommandResult.Fail(command, "NOT_YOUR_TURN");
                    if (StockMode) {
                        if (!market.GetCompanyIds().Contains(payload.companyId)) return CommandResult.Fail(command, "INVALID_COMPANY");
                        var days = RandomPublicDays(payload.companyId);
                        if (days == null) return CommandResult.Fail(command, "NO_PUBLIC_DAYS");
                        operation = WindowInfo.RevealPublicDays(ctx, payload.companyId, days, false);
                    }
                    else operation = WindowInfo.RevealWindow(ctx, payload.companyId, payload.startDay, false);
                    if (operation.Success) PublicSelected(now);
                }
                else
                {
                    if (PublicPicking) return CommandResult.Fail(command, "PUBLIC_SELECTION_PENDING");
                    if (!StockMode && command.type == "BUY_INFORMATION_WINDOW" && payload.endDay == 0) return CommandResult.Fail(command, "INVALID_WINDOW");
                    operation = command.type == "BUY_INFORMATION_WINDOW" ? StockMode ? WindowInfo.BuyRandomDays(ctx, payload.companyId, payload.informationDays)
                        : WindowInfo.BuyWindow(ctx, payload.companyId, payload.startDay, payload.endDay) :
                        WindowInfo.UseInformationItem(ctx, payload.itemId, payload.companyId, payload.startDay, payload.targetPlayerId, now);
                }
                if (!operation.Success) return CommandResult.Fail(command, operation.ErrorCode);
                foreach (var recipient in members) SendView(recipient.Id);
                return Success(command, operation.Payload);
            }
            if (command.type == "SAVE_TRADE_PLAN")
            {
                if (StockMode && PublicPicking) return CommandResult.Fail(command, "PUBLIC_SELECTION_PENDING");
                if (StockMode && phase != Protocol.Info && phase != Protocol.Investment &&
                    (phase != Protocol.Selection || member.Id != CurrentPlayer)) return CommandResult.Fail(command, "WRONG_PHASE");
                if (!(market is IMarketReservations reservations)) return CommandResult.Fail(command, "RESERVATIONS_UNAVAILABLE");
                if (command.payload == null) return CommandResult.Fail(command, "INVALID_REQUEST");
                var saved = reservations.SaveReservations(Context(member.Id, command.requestId), command.payload.investments);
                return saved.Success ? Success(command) : CommandResult.Fail(command, saved.ErrorCode);
            }
            string required = command.type == "BUY_INFORMATION" ? Protocol.Info :
                command.type == "SELECT_COMPANY" ? Protocol.Selection :
                command.type == "SUBMIT_INVESTMENT" ? Protocol.Investment : null;
            if (required == null) return CommandResult.Fail(command, "INVALID_REQUEST");
            if (phase != required) return CommandResult.Fail(command, "WRONG_PHASE");
            if (command.payload == null) return CommandResult.Fail(command, "INVALID_REQUEST");
            if (phase == Protocol.Selection && member.Id != CurrentPlayer)
                return CommandResult.Fail(command, "NOT_YOUR_TURN");
            RequestContext context = Context(member.Id, command.requestId);
            RuleResult rule;
            if (command.type == "BUY_INFORMATION")
                rule = information.BuyInformation(context, command.payload.companyId, command.payload.bundleIndex);
            else if (command.type == "SELECT_COMPANY")
                rule = market.ValidateAndSelectCompany(context, command.payload.companyId);
            else rule = market.SubmitInvestment(context, command.payload.investments);
            if (!rule.Success) return CommandResult.Fail(command, rule.ErrorCode);
            if (command.type == "SELECT_COMPANY") Selected(now);
            if (command.type == "SUBMIT_INVESTMENT" && members.All(m => market.IsOrderLocked(m.Id))) NextPhase(now);
            SendView(member.Id);
            return Success(command, rule.Payload);
        }
        private static CommandResult Success(Command command, ResultPayload payload = null)
        {
            return new CommandResult { type = command.type + "_RESULT", requestId = command.requestId,
                turn = command.turn, success = true, payload = payload };
        }
        private void BeginTurn(double now)
        {
            developerCheckpoints.Clear();
            turn++;
            if (!begun.Add(turn)) return;
            string[] ids = members.OrderBy(m => m.Id, StringComparer.Ordinal).Select(m => m.Id).ToArray();
            string[] seats = turn >= 9 && seatsForFinalTurns != null ? seatsForFinalTurns :
                Enumerable.Range(0, 4).Select(i => initialOrder[(i + (turn - 1) % 4) % 4]).ToArray();
            order = StockMode ? seats : seats.Concat(seats.Reverse()).ToArray();
            selectionIndex = 0; ordersLocked = false;
            phase = Protocol.Info;
            market.BeginTurn(Context(), ids);
            information.GrantTurnCoins(Context(), ids);
            if (StockMode) WindowInfo.PrepareRandomInformation(Context());
            progression?.BeginTurn(Context());
            if (StockMode && (turn == 1 || turn == 4))
            { Stock.BeginAugmentSelection(); Enter(StockRules.AugmentPhase, now, settings.AbilitySelectionSeconds ?? 20); }
            else Enter(progression != null && (turn == 3 || turn == 6 || turn == 9) ? Protocol.Auction : Protocol.Info,
                now, progression != null && (turn == 3 || turn == 6 || turn == 9) ? 15 : settings.InfoSeconds);
            foreach (Member member in members) SendView(member.Id);
        }
        private void Enter(string next, double now, double duration)
        {
            phase = next;
            developerTimerRunning = false;
            phaseEnd = duration == 0 ? 0 : now + duration;
            pickEnd = next == Protocol.Selection ? now + (StockMode ? StockRules.DraftSeconds : 4) : 0;
            if (next == Protocol.Info && WindowMode)
            {
                publicPickIndex = StockMode ? 4 : 0;
                publicPickEnd = publicPickIndex < 4 ? now + settings.PublicInformationPickSeconds : 0;
            }
            completed.Clear();
            if (StockMode && next == Protocol.Info)
                foreach (var member in members) WindowInfo.GrantAnalystCoins(Context(), member.Id);
            CaptureDeveloperPhase(duration);
            PublishState();
        }
        private void Selected(double now)
        {
            selectionIndex++;
            if (selectionIndex == 8) Enter(Protocol.Investment, now, 23);
            else { pickEnd = Math.Min(now + 4, phaseEnd); PublishState(); }
        }
        public void Tick()
        {
            lock (gate) if (!paused && mode != TimerMode.ManualForFixture)
            {
                AdvanceTo(Now);
                if (WindowMode && turn > 0 && WindowInfo.ExpireDistortions(Context(), Now))
                    foreach (var member in members) SendView(member.Id);
                if (phase != Protocol.Lobby && phase != Protocol.Ended && Now - lastPublishedAt >= .5) PublishState();
            }
        }
        private void AdvanceTo(double now)
        {
            while (PublicPicking && members.Any(m => m.Id == order[publicPickIndex] && m.Connection == null)) AutoPublicSelect(Math.Min(now, publicPickEnd));
            while (TimedPhase && phase != Protocol.Lobby && phase != Protocol.Ended && phaseEnd > 0 &&
                now >= NextDeadline)
            {
                if (PublicPicking) AutoPublicSelect(publicPickEnd);
                else if (phase == Protocol.Selection) AutoSelect(pickEnd);
                else NextPhase(phaseEnd);
                while (PublicPicking && members.Any(m => m.Id == order[publicPickIndex] && m.Connection == null)) AutoPublicSelect(Math.Min(now, publicPickEnd));
            }
            if (StockMode && phase == Protocol.Investment && TimedPhase)
            {
                int day = Math.Min(30, (int)Math.Floor((now - phaseEnd + 30 * lobbySettings.daySeconds) / lobbySettings.daySeconds));
                if (Stock.ProcessedDay < day) {
                    while (Stock.ProcessedDay < day) Stock.SimulateDay();
                    SendAllViews();
                }
            }
        }
        // Explicit server-side fixture control; never exposed as a network command.
        public void AdvanceManualPhase()
        {
            lock (gate)
            {
                if (mode != TimerMode.ManualForFixture) throw new InvalidOperationException("Not in manual fixture mode.");
                if (paused) return;
                if (PublicPicking) AutoPublicSelect(Now);
                else if (phase == Protocol.Selection) AutoSelect(Now);
                else if (phase != Protocol.Lobby && phase != Protocol.Ended) NextPhase(Now);
            }
        }
        private void AutoSelect(double now)
        {
            if (StockMode) { Selected(now); return; } // Unused slots remain in BANK; finish this player's purchase window.
            string player = CurrentPlayer;
            string[] selected = market.GetSelectedCompanies(player);
            CompanySeat[] seats = market.GetCompanySeats();
            foreach (string company in DefaultCompanies)
            {
                if (selected.Contains(company) || !seats.Any(s => s.companyId == company && s.remainingSeats > 0)) continue;
                RuleResult result = market.ValidateAndSelectCompany(Context(player, "auto-pick-" + selectionIndex), company);
                if (!result.Success) continue;
                ResultPayload payload = result.Payload ?? new ResultPayload { companyId = company };
                payload.automatic = true;
                Send(player, new ServerMessage { type = "COMMAND_RESULT", response = new CommandResult {
                    type = "SELECT_COMPANY_RESULT", requestId = "auto-pick-" + selectionIndex,
                    turn = turn, success = true, payload = payload } });
                Selected(now);
                SendView(player);
                return;
            }
            throw new InvalidOperationException("B service cannot commit a legal automatic selection.");
        }
        private void PublicSelected(double now)
        {
            publicPickIndex++;
            publicPickEnd = publicPickIndex < 4 ? Math.Min(phaseEnd, now + settings.PublicInformationPickSeconds) : 0;
            PublishState();
        }
        private int[] RandomPublicDays(string company)
        {
            var choices = WindowInfo.GetPublicInformation(Context());
            int length = lobbySettings.publicRevealDays;
            var used = new bool[31];
            foreach (var choice in choices.Where(c => c.companyId == company))
                foreach (int day in InformationWindowRules.PublicDays(choice)) used[day] = true;
            if (length == 0) return null;
            if (lobbySettings.scatteredPublicDays) {
                var freeDays = Enumerable.Range(1, 30).Where(d => !used[d]).ToArray();
                if (freeDays.Length < length) return null;
                for (int i = freeDays.Length - 1; i > 0; i--) {
                    int j = publicRandom.Next(i + 1); int temp = freeDays[i]; freeDays[i] = freeDays[j]; freeDays[j] = temp;
                }
                return freeDays.Take(length).OrderBy(d => d).ToArray();
            }
            var free = Enumerable.Range(1, 31 - length).Where(start => !Enumerable.Range(start, length).Any(d => used[d])).ToArray();
            if (free.Length == 0) return null;
            // Preserve room for repeated choices when all four players can fit in this company.
            var spacious = free.Where(start => {
                int run = 0, available = 0;
                for (int d = 1; d <= 30; d++) {
                    if (used[d] || d >= start && d < start + length) run = 0;
                    else if (++run == length) { available++; run = 0; }
                }
                return available >= 3 - choices.Length;
            }).ToArray();
            if (spacious.Length > 0) free = spacious;
            return Enumerable.Range(free[publicRandom.Next(free.Length)], length).ToArray();
        }
        private void AutoPublicSelect(double now)
        {
            var choices = WindowInfo.GetPublicInformation(Context());
            // Public masks only: automatic choice must not use hidden prices or profitability.
            foreach (var company in market.GetCompanyIds().OrderBy(id => choices.Count(c => c.companyId == id)))
            {
                if (StockMode) {
                    var days = RandomPublicDays(company);
                    if (days == null) continue;
                    var result = WindowInfo.RevealPublicDays(Context(order[publicPickIndex], "auto-public-" + publicPickIndex), company, days, true);
                    if (!result.Success) throw new InvalidOperationException("Invalid automatic public dates: " + result.ErrorCode);
                    PublicSelected(now); SendAllViews(); return;
                }
                for (int start = 1; start <= 26; start++)
                {
                    if (choices.Any(c => c.companyId == company && start < c.startDay + 5 && c.startDay < start + 5)) continue;
                    var result = WindowInfo.RevealWindow(Context(order[publicPickIndex], "auto-public-" + publicPickIndex), company, start, true);
                    if (!result.Success) throw new InvalidOperationException("Invalid automatic public window: " + result.ErrorCode);
                    PublicSelected(now);
                    foreach (var member in members) SendView(member.Id);
                    return;
                }
            }
            throw new InvalidOperationException("No public information candidate.");
        }
        private void NextPhase(double now)
        {
            if (StockMode && phase == StockRules.AugmentPhase)
            { Stock.CompleteAugmentSelection(); Enter(Protocol.Info, now, settings.InfoSeconds); SendAllViews(); return; }
            switch (phase)
            {
                case Protocol.AbilitySelection:
                    progression.CompleteAbilitySelection(roomId); BeginTurn(now); break;
                case Protocol.Auction:
                    var auctionSeats = progression.CloseAuction(Context());
                    if (turn == 9)
                    {
                        seatsForFinalTurns = auctionSeats;
                        order = auctionSeats.Concat(auctionSeats.Reverse()).ToArray();
                    }
                    Enter(Protocol.Info, now, settings.InfoSeconds);
                    foreach (var member in members) SendView(member.Id);
                    break;
                case Protocol.Info:
                    if (StockMode) { Stock.BeginLiveTrading(); Enter(Protocol.Investment, now, 30 * lobbySettings.daySeconds); SendAllViews(); }
                    else Enter(Protocol.Selection, now, 32);
                    break;
                case Protocol.Investment:
                    if (StockMode) { RevealStockPortfolio(now); break; }
                    foreach (Member member in members.Where(m => !market.IsOrderLocked(m.Id)))
                    {
                        string[] companies = market.GetSelectedCompanies(member.Id);
                        var investments = market is IMarketReservations reservations ? reservations.BuildAutomaticInvestments(member.Id) :
                            companies.Select(c => new Investment { companyId = c, weightBps = 5000,
                            intervals = new[] { new Interval { entryDay = 0, exitDay = 5 } } }).ToArray();
                        RuleResult result = market.SubmitInvestment(Context(member.Id, "auto-order"), investments);
                        if (!result.Success) throw new InvalidOperationException("B service rejected default order: " + result.ErrorCode);
                        ResultPayload payload = result.Payload ?? new ResultPayload { locked = true, investments = investments };
                        payload.automatic = true;
                        Send(member.Id, new ServerMessage { type = "COMMAND_RESULT", response = new CommandResult {
                            type = "SUBMIT_INVESTMENT_RESULT", requestId = "auto-order", turn = turn, success = true, payload = payload } });
                        SendView(member.Id);
                    }
                    if (!ordersLocked) { market.LockAllOrders(Context()); ordersLocked = true; }
                    Enter(Protocol.Reveal, now, 10);
                    Broadcast(new ServerMessage { type = "MARKET_REVEAL", revealedMarket = market.GetRevealedMarket(Context()) });
                    break;
                case Protocol.Reveal:
                    Scoreboard scoreboard = null;
                    if (!settled.Contains(turn))
                    {
                        scoreboard = market.SettleTurn(Context()); settled.Add(turn);
                        progression?.RewardTurn(Context(), scoreboard);
                        if (turn == settings.Turns && market is IMarketProgression finalMarket && (progression != null || StockMode))
                            scoreboard = finalMarket.FinalizeGame(Context(), initialOrder,
                                initialOrder.Select(id => StockMode ? 0 : progression.GetCoins(roomId, id)).ToArray());
                        latestBoard = scoreboard;
                    }
                    Enter(Protocol.Result, now, 5);
                    if (scoreboard != null) Broadcast(new ServerMessage { type = "SCOREBOARD", scoreboard = scoreboard });
                    foreach (Member member in members) SendView(member.Id);
                    break;
                case Protocol.Result:
                    if (turn < settings.Turns) BeginTurn(now);
                    else End("GAME_COMPLETE");
                    break;
            }
        }
        private void End(string reason)
        {
            if (paused) { pausedSeconds += clock.Seconds - pausedAt; paused = false; }
            phase = Protocol.Ended;
            phaseEnd = pickEnd = 0;
            Broadcast(new ServerMessage { type = "ROOM_ENDED", errorCode = reason });
            PublishState();
        }
        public PublicState Snapshot()
        {
            lock (gate) return new PublicState { roomId = roomId, turn = turn, phase = phase,
                selectionIndex = selectionIndex, currentPlayerId = CurrentPlayer, paused = paused, developerMode = mode == TimerMode.Developer,
                developerTimerRunning = developerTimerRunning, developerCanGoBack = developerCheckpoints.Count > 1, developerRevision = developerRevision,
                serverElapsedSeconds = phase != Protocol.Lobby ? Now : 0, phaseEndsAtServerSeconds = TimedPhase ? phaseEnd : 0,
                selectionDeadlineAtServerSeconds = mode == TimerMode.Developer ? 0 : pickEnd,
                initialPlayerOrder = (string[])initialOrder.Clone(), seatPlayerIds = order.Take(4).ToArray(),
                botDifficulty = botDifficulty, events = progression?.GetPublicEvents(roomId) ?? new PublicEvent[0],
                stockSystem = StockMode, totalTurns = settings.Turns, draftOrder = (string[])order.Clone(),
                lobbySettings = lobbySettings.Copy(),
                shareMarket = StockMode && turn > 0 ? Stock.GetShareMarket() : new CompanyShareState[0],
                windowInformation = WindowMode, publicSelectionActive = PublicPicking, publicSelectionIndex = publicPickIndex,
                publicSelectionPlayerId = PublicPicking ? order[publicPickIndex] : "",
                publicSelectionDeadlineAtServerSeconds = PublicPicking && TimedPhase ? publicPickEnd : 0,
                publicInformation = WindowMode && turn > 0 ? WindowInfo.GetPublicInformation(Context()) : new PublicInformationChoice[0],
                informationInterest = StockMode && WindowMode && turn > 0 ? WindowInfo.GetInformationInterest(Context()) : new CompanyInterest[0],
                players = members.OrderBy(m => m.Id, StringComparer.Ordinal).Select(m => new Player { playerId = m.Id, displayName = m.Name,
                    connected = m.Connection != null, ready = m.Ready, isBot = m.IsBot,
                    augments = StockMode && turn > 0 ? Stock.GetPortfolio(m.Id).augments : Array.Empty<string>() }).ToArray(),
                companySeats = turn > 0 ? market.GetCompanySeats() : new CompanySeat[0] };
        }
        private void SendView(string player)
        {
            var view = turn == 0 ? new PlayerView { recipientPlayerId = player, turn = 0, phase = phase } :
                information.BuildPlayerView(Context(player), market.GetOwnInvestmentState(player));
            progression?.PopulateView(Context(player), view);
            if (market is IMarketReservations reservations) view.reservedInvestments = reservations.GetReservations(player);
            if (StockMode) {
                view.portfolio = Stock.GetPortfolio(player);
                if (Stock.ProcessedDay > 0 && market is IWindowMarketDataSource source)
                    foreach (var company in view.companies)
                        company.visibleWindows = (company.visibleWindows ?? new VisibleInformationWindow[0]).Concat(new[] { new VisibleInformationWindow {
                            companyId = company.companyId, source = "MARKET", recordId = "market-" + turn + "-" + company.companyId, startDay = 1,
                            dailyChangesBps = source.GetWindow(turn, company.companyId, 1, Stock.ProcessedDay),
                            dailyOpenBps = source.GetOpenWindow(turn, company.companyId, 1, Stock.ProcessedDay) } }).ToArray();
            }
            Send(player, new ServerMessage { type = "PLAYER_VIEW", playerView = view });
        }
        private void PublishState() {
            var snapshot = Snapshot(); lastPublishedAt = snapshot.serverElapsedSeconds;
            Broadcast(new ServerMessage { type = "PUBLIC_STATE", state = snapshot });
        }
        private void Broadcast(ServerMessage message)
        { foreach (Member member in members) if (member.Connection != null) Send(member.Id, message); }
        private void Send(string player, ServerMessage message) { outbox.Enqueue(new Delivery(player, message)); }
        private CommandResult Reply(string player, CommandResult result)
        { Send(player, new ServerMessage { type = "COMMAND_RESULT", response = Copy(result) }); return result; }
        public Delivery[] DrainMessages()
        { lock (gate) { Delivery[] result = outbox.ToArray(); outbox.Clear(); return result; } }

        private static Investment[] Copy(Investment[] input)
        {
            return input?.Select(i => i == null ? null : new Investment { companyId = i.companyId, weightBps = i.weightBps,
                intervals = i.intervals?.Select(t => t == null ? null : new Interval { entryDay = t.entryDay, exitDay = t.exitDay }).ToArray() }).ToArray();
        }
        private static Command Copy(Command input)
        {
            return new Command { type = input?.type, requestId = input?.requestId, turn = input?.turn ?? 0,
                developerRevision = input?.developerRevision ?? 0,
                payload = input?.payload == null ? null : new CommandPayload { companyId = input.payload.companyId,
                    bundleIndex = input.payload.bundleIndex, ready = input.payload.ready, investments = Copy(input.payload.investments),
                    startDay = input.payload.startDay, endDay = input.payload.endDay, itemId = input.payload.itemId, targetPlayerId = input.payload.targetPlayerId,
                    abilityId = input.payload.abilityId, bidAmount = input.payload.bidAmount, useFreePass = input.payload.useFreePass,
                    historyTurn = input.payload.historyTurn, informationDays = input.payload.informationDays,
                    lobbySettings = input.payload.lobbySettings?.Copy(),
                    augmentId = input.payload.augmentId,
                    shareOrders = input.payload.shareOrders?.Select(v => v == null ? null : new ShareOrder { companyId = v.companyId, targetShares = v.targetShares }).ToArray(),
                    seatPreferences = (string[])input.payload.seatPreferences?.Clone() } };
        }
        private static CommandResult Copy(CommandResult input)
        {
            ResultPayload p = input.payload;
            return new CommandResult { type = input.type, requestId = input.requestId, turn = input.turn,
                success = input.success, errorCode = input.errorCode, payload = p == null ? null : new ResultPayload {
                    companyId = p.companyId, remainingCoins = p.remainingCoins, bundleIndex = p.bundleIndex,
                    startDay = p.startDay, dailyChangesBps = (int[])p.dailyChangesBps?.Clone(), dailyOpenBps = (int[])p.dailyOpenBps?.Clone(), locked = p.locked,
                    automatic = p.automatic, source = p.source, recordId = p.recordId, paidCoins = p.paidCoins, investments = Copy(p.investments) } };
        }
        private static string Fingerprint(Command c)
        {
            using (var bytes = new MemoryStream())
            using (var writer = new BinaryWriter(bytes, Encoding.UTF8))
            {
                writer.Write(c.type ?? ""); writer.Write(c.developerRevision); writer.Write(c.payload != null);
                if (c.payload != null)
                {
                    writer.Write(c.payload.companyId ?? ""); writer.Write(c.payload.bundleIndex); writer.Write(c.payload.ready);
                    writer.Write(c.payload.startDay); writer.Write(c.payload.endDay); writer.Write(c.payload.itemId ?? ""); writer.Write(c.payload.targetPlayerId ?? "");
                    writer.Write(c.payload.abilityId ?? ""); writer.Write(c.payload.bidAmount); writer.Write(c.payload.useFreePass);
                    writer.Write(c.payload.historyTurn); writer.Write(c.payload.informationDays);
                    writer.Write(c.payload.lobbySettings != null);
                    if (c.payload.lobbySettings != null) {
                        writer.Write(c.payload.lobbySettings.earlyTurnCoins); writer.Write(c.payload.lobbySettings.lateTurnCoins);
                        writer.Write(c.payload.lobbySettings.publicRevealDays); writer.Write(c.payload.lobbySettings.scatteredPublicDays);
                        writer.Write(c.payload.lobbySettings.daySeconds);
                        writer.Write(c.payload.lobbySettings.informationStartDay); writer.Write(c.payload.lobbySettings.informationEndDay);
                    }
                    writer.Write(c.payload.augmentId ?? "");
                    writer.Write(c.payload.shareOrders?.Length ?? -1);
                    foreach (var v in c.payload.shareOrders ?? new ShareOrder[0])
                    { writer.Write(v != null); if (v != null) { writer.Write(v.companyId ?? ""); writer.Write(v.targetShares); } }
                    writer.Write(c.payload.seatPreferences?.Length ?? -1);
                    if (c.payload.seatPreferences != null) foreach (var seat in c.payload.seatPreferences) writer.Write(seat ?? "");
                    writer.Write(c.payload.investments?.Length ?? -1);
                    if (c.payload.investments != null) foreach (Investment i in c.payload.investments)
                    {
                        writer.Write(i != null); if (i == null) continue;
                        writer.Write(i.companyId ?? ""); writer.Write(i.weightBps); writer.Write(i.intervals?.Length ?? -1);
                        if (i.intervals != null) foreach (Interval t in i.intervals)
                        { writer.Write(t != null); if (t != null) { writer.Write(t.entryDay); writer.Write(t.exitDay); } }
                    }
                }
                writer.Flush();
                using (var sha = SHA256.Create()) return Convert.ToBase64String(sha.ComputeHash(bytes.ToArray()));
            }
        }
    }
}
