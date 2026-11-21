using System;
using System.Collections.Generic;

namespace BlackWhale.Shared
{
    // Server-created, immutable context. A connection's identity never comes from JSON.
    public sealed class RequestContext
    {
        public string RoomId { get; private set; }
        public int Turn { get; private set; }
        public string Phase { get; private set; }
        public string PlayerId { get; private set; }
        public string RequestId { get; private set; }
        public RequestContext(string roomId, int turn, string phase, string playerId = "", string requestId = "")
        { RoomId = roomId; Turn = turn; Phase = phase; PlayerId = playerId; RequestId = requestId; }
    }
    public sealed class RuleResult
    {
        public bool Success { get { return ErrorCode == ""; } }
        public string ErrorCode { get; private set; }
        public ResultPayload Payload { get; private set; }
        public RuleResult(string errorCode = "", ResultPayload payload = null)
        { ErrorCode = errorCode; Payload = payload; }
    }
    public sealed class OwnInvestmentState
    {
        public long TotalAssets;
        public string[] SelectedCompanies;
        public Investment[] Investments;
    }
    // Server-only B -> C source. This object is never a client message or a replicated state.
    public sealed class ServerInformationBundle
    {
        public string CompanyId;
        public int BundleIndex, StartDay;
        public bool IsPublic;
        public int[] DailyChangesBps;
    }
    public interface IMarketDataSource
    {
        string[] GetCompanyIds();
        // Server-only. Real B retains immutable bundles for completed turns for the room's lifetime.
        ServerInformationBundle GetBundle(int turn, string companyId, int bundleIndex);
    }
    // B owns all selections, capacity, orders, market and asset state.
    public interface IMarketService : IMarketDataSource
    {
        void BeginTurn(RequestContext context, string[] playerIds);
        RuleResult ValidateAndSelectCompany(RequestContext context, string companyId);
        CompanySeat[] GetCompanySeats();
        string[] GetSelectedCompanies(string playerId);
        RuleResult SubmitInvestment(RequestContext context, Investment[] investments);
        bool IsOrderLocked(string playerId);
        void LockAllOrders(RequestContext context);
        Scoreboard SettleTurn(RequestContext context);
        OwnInvestmentState GetOwnInvestmentState(string playerId);
        RevealedCompany[] GetRevealedMarket(RequestContext context);
    }
    // C must implement its own idempotent grant ledger and atomic purchase operation.
    public interface IInformationService
    {
        void GrantTurnCoins(RequestContext context, string[] playerIds);
        RuleResult BuyInformation(RequestContext context, string companyId, int bundleIndex);
        PlayerView BuildPlayerView(RequestContext context, OwnInvestmentState ownState);
    }
    public interface IServerClock { double Seconds { get; } }
    // Opaque server-only snapshots. Restoring the same checkpoint repeatedly must make fresh copies.
    public interface IDeveloperCheckpoint { Action CaptureDeveloperCheckpoint(string roomId); }
    public static class DeveloperCheckpoint
    {
        public static Action Map<K, V>(Dictionary<K, V> target, Func<V, V> copy)
        {
            var snapshot = new Dictionary<K, V>();
            foreach (var item in target) snapshot.Add(item.Key, copy(item.Value));
            return () => { target.Clear(); foreach (var item in snapshot) target.Add(item.Key, copy(item.Value)); };
        }
    }
    // Explicit v3 capability: fixed v2 fixtures retain their original bundle contract.
    public interface IWindowMarketDataSource
    {
        bool UsesWindowInformation { get; }
        int[] GetWindow(int turn, string companyId, int startDay, int length = 5);
        int[] GetOpenWindow(int turn, string companyId, int startDay, int length = 5);
    }
    public interface IWindowInformationService
    {
        bool UsesWindowInformation { get; }
        void ConfigureRoom(string roomId, LobbySettings settings);
        void PrepareRandomInformation(RequestContext context);
        RuleResult BuyRandomDays(RequestContext context, string companyId, int count = 1);
        void GrantAnalystCoins(RequestContext context, string playerId);
        RuleResult RevealPublicDays(RequestContext context, string companyId, int[] days, bool automatic);
        RuleResult RevealWindow(RequestContext context, string companyId, int startDay, bool automatic);
        RuleResult BuyWindow(RequestContext context, string companyId, int startDay, int endDay = 0);
        RuleResult UseInformationItem(RequestContext context, string itemId, string companyId, int startDay, string targetPlayerId, double now);
        PublicInformationChoice[] GetPublicInformation(RequestContext context);
        CompanyInterest[] GetInformationInterest(RequestContext context);
        bool ExpireDistortions(RequestContext context, double now);
    }
    public interface IMarketHistory
    {
        OwnInvestmentState GetOwnInvestmentState(string playerId, int requestedTurn);
        Scoreboard GetScoreboard(int requestedTurn);
    }
    // Private editable reservations. They become orders only at the investment deadline.
    public interface IMarketReservations
    {
        RuleResult SaveReservations(RequestContext context, Investment[] reservations);
        Investment[] GetReservations(string playerId);
        Investment[] BuildAutomaticInvestments(string playerId);
    }
    // B owns the cost ledger and final asset/performance comparison.
    public interface IMarketProgression
    {
        long GetTurnStartAssets(string playerId);
        RuleResult CommitCost(RequestContext context, long amount);
        Scoreboard FinalizeGame(RequestContext context, string[] initialOrder, int[] finalCoins);
    }
    // C owns ability/auction/reward state. A only controls when these operations run.
    public interface IProgressionService
    {
        void PrepareGame(string roomId, string[] initialOrder);
        RuleResult SelectAbility(RequestContext context, string abilityId);
        bool AllAbilitiesSelected(string roomId);
        void CompleteAbilitySelection(string roomId);
        void BeginTurn(RequestContext context);
        RuleResult SubmitBid(RequestContext context, CommandPayload payload);
        bool AllBidsConfirmed(RequestContext context);
        string[] CloseAuction(RequestContext context);
        void RewardTurn(RequestContext context, Scoreboard board);
        void PopulateView(RequestContext context, PlayerView view);
        PublicEvent[] GetPublicEvents(string roomId);
        int GetCoins(string roomId, string playerId);
    }
    public interface IWireCodec
    {
        string EncodeClient(ClientMessage message);
        ClientMessage DecodeClient(string json);
        string EncodeServer(ServerMessage message);
        ServerMessage DecodeServer(string json);
    }
    // D subscribes to messages; both the host and remote players use this client.
    public interface IGameClient : IDisposable
    {
        event Action<ServerMessage> MessageReceived;
        void SendCommand(Command command);
        void Pump();
    }
}
