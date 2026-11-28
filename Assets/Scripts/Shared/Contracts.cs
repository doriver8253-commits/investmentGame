using System;

namespace BlackWhale.Shared
{
    public static class Protocol
    {
        public const string Version = "prototype-v6-stock";
        public const string RulesVersion = "rules-v6-random-information-eight-augments";
        public const string Info = "INFO", Selection = "SELECTION", Investment = "INVESTMENT";
        public const string Reveal = "MARKET_REVEAL", Result = "RESULT", Lobby = "LOBBY", Ended = "ENDED";
        public const string AbilitySelection = "ABILITY_SELECTION", Auction = "AUCTION";
    }

    // Field names and integer units intentionally match the frozen fixture format.
    [Serializable] public sealed class Interval { public int entryDay, exitDay; }
    [Serializable] public sealed class Investment
    {
        public string companyId;
        public int weightBps;
        public Interval[] intervals;
    }
    [Serializable] public sealed class CommandPayload
    {
        public string companyId;
        public int bundleIndex;
        public int startDay, endDay;
        public int informationDays = 1;
        public string itemId, targetPlayerId;
        public int historyTurn;
        public Investment[] investments;
        public bool ready;
        public string abilityId;
        public long bidAmount;
        public bool useFreePass;
        public string[] seatPreferences;
        public ShareOrder[] shareOrders;
        public string augmentId;
        public LobbySettings lobbySettings;
    }
    [Serializable] public sealed class LobbySettings
    {
        public int earlyTurnCoins = 15, lateTurnCoins = 25, publicRevealDays = 5;
        public int daySeconds = StockRules.DaySeconds;
        public int informationStartDay = 0, informationEndDay = 25;
        public bool scatteredPublicDays = true;
        public bool IsValid => earlyTurnCoins >= 0 && earlyTurnCoins <= 999 && lateTurnCoins >= 0 && lateTurnCoins <= 999
            && informationStartDay >= 0 && informationEndDay <= 25 && informationStartDay <= informationEndDay
            && publicRevealDays >= 0 && publicRevealDays <= informationEndDay - Math.Max(1, informationStartDay) + 1
            && daySeconds >= 1 && daySeconds <= 60;
        public LobbySettings Copy() => new LobbySettings { earlyTurnCoins = earlyTurnCoins, lateTurnCoins = lateTurnCoins,
            publicRevealDays = publicRevealDays, scatteredPublicDays = scatteredPublicDays, daySeconds = daySeconds,
            informationStartDay = informationStartDay, informationEndDay = informationEndDay };
        public bool SameAs(LobbySettings other) => other != null && earlyTurnCoins == other.earlyTurnCoins
            && lateTurnCoins == other.lateTurnCoins && publicRevealDays == other.publicRevealDays && scatteredPublicDays == other.scatteredPublicDays
            && daySeconds == other.daySeconds && informationStartDay == other.informationStartDay && informationEndDay == other.informationEndDay;
    }
    [Serializable] public sealed class Command
    {
        public string type, requestId;
        public int turn;
        public int developerRevision;
        public CommandPayload payload;
    }
    [Serializable] public sealed class ResultPayload
    {
        public string companyId;
        public int remainingCoins, bundleIndex, startDay;
        public int[] dailyChangesBps;
        public int[] dailyOpenBps;
        public bool locked, automatic;
        public Investment[] investments;
        public string source;
        public string recordId;
        public int paidCoins;
    }
    [Serializable] public sealed class CommandResult
    {
        public string type, requestId, errorCode = "";
        public int turn;
        public bool success;
        public ResultPayload payload;
        public static CommandResult Fail(Command command, string code)
        {
            return new CommandResult { type = (command.type ?? "UNKNOWN") + "_RESULT",
                requestId = command.requestId, turn = command.turn, errorCode = code };
        }
    }
    [Serializable] public sealed class Player
    {
        public string playerId, displayName;
        public bool connected, ready;
        public bool isBot;
        public string botDifficulty, botPersonality;
        public string[] augments;
    }
    [Serializable] public sealed class CompanySeat { public string companyId; public int remainingSeats; }
    [Serializable] public sealed class PublicState
    {
        public string schemaVersion = "1.0", roomId, phase, currentPlayerId = "";
        public int turn, selectionIndex;
        public bool paused, developerMode, developerTimerRunning, developerCanGoBack;
        public int developerRevision;
        public double serverElapsedSeconds, phaseEndsAtServerSeconds, selectionDeadlineAtServerSeconds;
        public Player[] players;
        public CompanySeat[] companySeats;
        public string[] initialPlayerOrder, seatPlayerIds;
        public string botDifficulty;
        public PublicEvent[] events;
        public bool windowInformation, publicSelectionActive;
        public int publicSelectionIndex;
        public string publicSelectionPlayerId;
        public double publicSelectionDeadlineAtServerSeconds;
        public PublicInformationChoice[] publicInformation;
        public bool stockSystem;
        public int totalTurns;
        public string[] draftOrder;
        public CompanyShareState[] shareMarket;
        public LobbySettings lobbySettings;
        // [실험] 자산 교환 턴: swapControllerIds[i]가 swapAccountIds[i] 계좌를 조종한다.
        public bool swapTurn;
        public string[] swapControllerIds, swapAccountIds;
        public int[] swapTurns;
        public int swapBonusPercent;
        // 실시간 뉴스(루머·속보)와 이번 턴 미션. 주식 모드에서만 채운다.
        public MarketNews[] news;
        public TurnMission mission;
        public CompanyMood[] moods;
        public bool liveMarket;
    }
    [Serializable] public sealed class VisibleBundle
    {
        public int bundleIndex, startDay;
        public string source;
        public int[] dailyChangesBps;
    }
    [Serializable] public sealed class VisibleCompany
    {
        public string companyId;
        public VisibleBundle[] visibleBundles;
        public VisibleInformationWindow[] visibleWindows;
    }
    [Serializable] public sealed class VisibleInformationWindow
    {
        public string recordId, source, companyId;
        public int startDay, paidCoins;
        public int[] dailyChangesBps;
        public int[] dailyOpenBps;
    }
    [Serializable] public sealed class PublicInformationChoice
    {
        public string playerId, companyId;
        public int startDay;
        public bool automatic;
        public int[] days;
    }
    [Serializable] public sealed class PlayerView
    {
        public string schemaVersion = "1.0", recipientPlayerId, phase;
        public int turn, coins;
        public bool entryAtOpen;
        public long assets;
        public string[] selectedCompanies;
        public Investment[] ownInvestments;
        public Investment[] reservedInvestments;
        public VisibleCompany[] companies;
        public AbilityState ability;
        public AuctionState auction;
        public string[] inventory;
        public PortfolioState portfolio;
    }
    [Serializable] public sealed class ReturnPoint { public int turn, returnBps; }
    [Serializable] public sealed class ScoreRow
    {
        public int rank, turnReturnBps, cumulativeReturnBps;
        public string playerId;
        public long totalAssets, cumulativeProfit;
        public ReturnPoint[] returnHistory;
        // [실험] 교환 턴 정산: 내가 조종한 계좌와 그 계좌의 손익·수익률, 내가 받은 견제 보너스(가장 많이 떨어뜨린 사람만)
        public string swapAccountId;
        public long swapAccountProfit, swapBonus;
        public int swapAccountReturnBps;
        // 이번 턴 미션 성공 보너스(실패하면 0)
        public long missionBonus;
    }
    [Serializable] public sealed class Scoreboard
    {
        public string schemaVersion = "1.0", phase = Protocol.Result;
        public int turn;
        public bool rankingFinalForTurn = true;
        public ScoreRow[] rows;
        public bool gameComplete;
        public string winnerPlayerId;
    }
    [Serializable] public sealed class AbilityState
    {
        public string[] candidates;
        public string selectedId;
        public int reusableAtTurn, freeInformationCredits;
    }
    [Serializable] public sealed class AuctionState
    {
        public long maximumBid, bidAmount;
        public bool confirmed, useFreePass;
        public string[] seatPreferences;
    }
    // Deliberately excludes bid amounts, private coins, orders and information values.
    [Serializable] public sealed class PublicEvent
    {
        public int turn;
        public string eventId, kind, playerId, targetPlayerId, detail;
    }
    [Serializable] public sealed class RevealedTrade { public int entryDay, exitDay, returnBps; }
    [Serializable] public sealed class RevealedInvestment
    {
        public string playerId;
        public int returnBps;
        public int shares;
        public RevealedTrade[] intervals;
    }
    [Serializable] public sealed class RevealedCompany
    {
        public string companyId;
        public int[] dailyChangesBps;
        public RevealedInvestment[] investments;
    }
    [Serializable] public sealed class HistorySnapshot
    {
        public int turn;
        public PlayerView playerView;
        public Scoreboard scoreboard;
    }
    [Serializable] public sealed class Session
    {
        public string playerId, reconnectToken;
    }
    [Serializable] public sealed class ServerMessage
    {
        public string type, errorCode;
        public Session session;
        public PublicState state;
        public CommandResult response;
        public PlayerView playerView;
        public Scoreboard scoreboard;
        public RevealedCompany[] revealedMarket;
        public HistorySnapshot history;
    }
    [Serializable] public sealed class ClientMessage
    {
        public string type, displayName, reconnectToken;
        public Command command;
    }
}
