// 저장소가 받은 메시지 객체를 그대로 붙잡고 있지 않도록 깊은 복사한다.
// (같은 프로세스 안의 호스트는 방송 메시지 객체를 여러 참가자에게 같이 넘길 수 있다.)
using System.Linq;
using BlackWhale.Shared;

namespace BlackWhale.UI.State
{
    public static class DtoCopy
    {
        public static Interval Copy(Interval s)
            => s == null ? null : new Interval { entryDay = s.entryDay, exitDay = s.exitDay };

        public static Investment Copy(Investment s)
            => s == null ? null : new Investment
            {
                companyId = s.companyId,
                weightBps = s.weightBps,
                intervals = s.intervals?.Select(Copy).ToArray()
            };

        public static Investment[] Copy(Investment[] s) => s?.Select(Copy).ToArray();

        public static VisibleBundle Copy(VisibleBundle s)
            => s == null ? null : new VisibleBundle
            {
                bundleIndex = s.bundleIndex,
                startDay = s.startDay,
                source = s.source,
                dailyChangesBps = (int[])s.dailyChangesBps?.Clone()
            };

        public static VisibleCompany Copy(VisibleCompany s)
            => s == null ? null : new VisibleCompany
            {
                companyId = s.companyId,
                visibleBundles = s.visibleBundles?.Select(Copy).ToArray(),
                visibleWindows = s.visibleWindows?.Select(Copy).ToArray()
            };

        public static PlayerView Copy(PlayerView s)
            => s == null ? null : new PlayerView
            {
                schemaVersion = s.schemaVersion,
                recipientPlayerId = s.recipientPlayerId,
                phase = s.phase,
                turn = s.turn,
                coins = s.coins,
                entryAtOpen = s.entryAtOpen,
                assets = s.assets,
                selectedCompanies = (string[])s.selectedCompanies?.Clone(),
                ownInvestments = Copy(s.ownInvestments),
                reservedInvestments = Copy(s.reservedInvestments),
                portfolio = Copy(s.portfolio),
                companies = s.companies?.Select(Copy).ToArray(),
                ability = Copy(s.ability), auction = Copy(s.auction), inventory = (string[])s.inventory?.Clone()
            };

        public static CompanySeat Copy(CompanySeat s)
            => s == null ? null : new CompanySeat { companyId = s.companyId, remainingSeats = s.remainingSeats };

        public static Player Copy(Player s)
            => s == null ? null : new Player { playerId = s.playerId, displayName = s.displayName, connected = s.connected, ready = s.ready, isBot = s.isBot, botDifficulty = s.botDifficulty, botPersonality = s.botPersonality, augments = (string[])s.augments?.Clone() };

        public static PublicState Copy(PublicState s)
            => s == null ? null : new PublicState
            {
                schemaVersion = s.schemaVersion,
                roomId = s.roomId,
                phase = s.phase,
                currentPlayerId = s.currentPlayerId,
                turn = s.turn,
                selectionIndex = s.selectionIndex,
                paused = s.paused, developerMode = s.developerMode,
                developerTimerRunning = s.developerTimerRunning, developerCanGoBack = s.developerCanGoBack, developerRevision = s.developerRevision,
                serverElapsedSeconds = s.serverElapsedSeconds,
                phaseEndsAtServerSeconds = s.phaseEndsAtServerSeconds,
                selectionDeadlineAtServerSeconds = s.selectionDeadlineAtServerSeconds,
                players = s.players?.Select(Copy).ToArray(),
                companySeats = s.companySeats?.Select(Copy).ToArray(),
                initialPlayerOrder = (string[])s.initialPlayerOrder?.Clone(), seatPlayerIds = (string[])s.seatPlayerIds?.Clone(),
                botDifficulty = s.botDifficulty, events = s.events?.Select(Copy).ToArray(),
                lobbySettings = s.lobbySettings?.Copy(),
                windowInformation = s.windowInformation, publicSelectionActive = s.publicSelectionActive,
                publicSelectionIndex = s.publicSelectionIndex, publicSelectionPlayerId = s.publicSelectionPlayerId,
                publicSelectionDeadlineAtServerSeconds = s.publicSelectionDeadlineAtServerSeconds,
                publicInformation = s.publicInformation?.Select(c => new PublicInformationChoice { playerId = c.playerId,
                    companyId = c.companyId, startDay = c.startDay, automatic = c.automatic, days = (int[])c.days?.Clone() }).ToArray(),
                stockSystem = s.stockSystem, totalTurns = s.totalTurns, draftOrder = (string[])s.draftOrder?.Clone(),
                shareMarket = s.shareMarket?.Select(c => new CompanyShareState { companyId = c.companyId, totalShares = c.totalShares, fills = c.fills?.ToArray(),
                    owners = c.owners?.Select(p => new ShareOwner { playerId = p.playerId, shares = p.shares, reserved = p.reserved }).ToArray() }).ToArray(),
                swapTurn = s.swapTurn, swapControllerIds = (string[])s.swapControllerIds?.Clone(), swapAccountIds = (string[])s.swapAccountIds?.Clone(),
                swapTurns = (int[])s.swapTurns?.Clone(), swapBonusPercent = s.swapBonusPercent,
                news = s.news?.Select(n => n == null ? null : new MarketNews { day = n.day, eventDay = n.eventDay, companyId = n.companyId, kind = n.kind,
                    text = n.text, direction = n.direction, moveBps = n.moveBps, accuracyPercent = n.accuracyPercent }).ToArray(),
                mission = s.mission == null ? null : new TurnMission { id = s.mission.id, companyId = s.mission.companyId,
                    title = s.mission.title, description = s.mission.description, rewardBps = s.mission.rewardBps },
                liveMarket = s.liveMarket,
                moods = s.moods?.Select(m => m == null ? null : new CompanyMood { companyId = m.companyId, text = m.text, percent = m.percent }).ToArray()
            };

        public static ReturnPoint Copy(ReturnPoint s)
            => s == null ? null : new ReturnPoint { turn = s.turn, returnBps = s.returnBps };

        public static ScoreRow Copy(ScoreRow s)
            => s == null ? null : new ScoreRow
            {
                rank = s.rank,
                turnReturnBps = s.turnReturnBps,
                cumulativeReturnBps = s.cumulativeReturnBps,
                playerId = s.playerId,
                totalAssets = s.totalAssets,
                cumulativeProfit = s.cumulativeProfit,
                returnHistory = s.returnHistory?.Select(Copy).ToArray(),
                swapAccountId = s.swapAccountId, swapAccountProfit = s.swapAccountProfit, swapBonus = s.swapBonus, swapAccountReturnBps = s.swapAccountReturnBps,
                missionBonus = s.missionBonus
            };

        public static Scoreboard Copy(Scoreboard s)
            => s == null ? null : new Scoreboard
            {
                schemaVersion = s.schemaVersion,
                phase = s.phase,
                turn = s.turn,
                rankingFinalForTurn = s.rankingFinalForTurn,
                gameComplete = s.gameComplete, winnerPlayerId = s.winnerPlayerId,
                rows = s.rows?.Select(Copy).ToArray()
            };

        public static RevealedCompany Copy(RevealedCompany s)
            => s == null ? null : new RevealedCompany { companyId = s.companyId, dailyChangesBps = (int[])s.dailyChangesBps?.Clone(),
                investments = s.investments?.Select(i => new RevealedInvestment { playerId = i.playerId, returnBps = i.returnBps, shares = i.shares,
                    intervals = i.intervals?.Select(v => new RevealedTrade { entryDay = v.entryDay, exitDay = v.exitDay, returnBps = v.returnBps }).ToArray() }).ToArray() };
        public static AbilityState Copy(AbilityState s) => s == null ? null : new AbilityState {
            candidates = (string[])s.candidates?.Clone(), selectedId = s.selectedId,
            reusableAtTurn = s.reusableAtTurn, freeInformationCredits = s.freeInformationCredits };
        public static AuctionState Copy(AuctionState s) => s == null ? null : new AuctionState {
            maximumBid = s.maximumBid, bidAmount = s.bidAmount, confirmed = s.confirmed, useFreePass = s.useFreePass,
            seatPreferences = (string[])s.seatPreferences?.Clone() };
        public static PublicEvent Copy(PublicEvent s) => s == null ? null : new PublicEvent {
            eventId = s.eventId, turn = s.turn, kind = s.kind, playerId = s.playerId, targetPlayerId = s.targetPlayerId, detail = s.detail };
        public static HistorySnapshot Copy(HistorySnapshot s) => s == null ? null : new HistorySnapshot {
            turn = s.turn, playerView = Copy(s.playerView), scoreboard = Copy(s.scoreboard) };
        public static VisibleInformationWindow Copy(VisibleInformationWindow s) => s == null ? null : new VisibleInformationWindow {
            recordId = s.recordId, source = s.source, companyId = s.companyId, startDay = s.startDay, paidCoins = s.paidCoins,
            dailyChangesBps = (int[])s.dailyChangesBps?.Clone(), dailyOpenBps = (int[])s.dailyOpenBps?.Clone() };
        public static PortfolioState Copy(PortfolioState s) => s == null ? null : new PortfolioState {
            processedDay = s.processedDay,
            capacity = s.capacity, usedSlots = s.usedSlots, informationDiscount = s.informationDiscount, draftSharesRemaining = s.draftSharesRemaining,
            augmentChosen = s.augmentChosen, priorityUsed = s.priorityUsed, concentrationCompany = s.concentrationCompany,
            augments = (string[])s.augments?.Clone(), candidates = (string[])s.candidates?.Clone(),
            holdings = s.holdings?.Select(v => new PortfolioHolding { companyId = v.companyId, shares = v.shares, activeShares = v.activeShares, consecutiveTurns = v.consecutiveTurns }).ToArray(),
            insights = s.insights?.Select(v => new StockInsight { companyId = v.companyId, message = v.message, probabilityBps = v.probabilityBps }).ToArray() };
    }
}
