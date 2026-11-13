namespace BlackWhale.Shared
{
    // Existing rules-v2 codes and additive A transport/flow errors; one shared vocabulary.
    public static class ErrorCodes
    {
        public const string NotEnoughCoins = "NOT_ENOUGH_COINS", AlreadyVisible = "ALREADY_VISIBLE";
        public const string InvalidBundle = "INVALID_BUNDLE", InvalidCompany = "INVALID_COMPANY";
        public const string WrongPhase = "WRONG_PHASE", StaleTurn = "STALE_TURN";
        public const string CompanyFull = "COMPANY_FULL", DuplicateCompany = "DUPLICATE_COMPANY", NotYourTurn = "NOT_YOUR_TURN";
        public const string SelectionComplete = "SELECTION_COMPLETE";
        public const string InvalidHoldingDays = "INVALID_HOLDING_DAYS", TooManyIntervals = "TOO_MANY_INTERVALS";
        public const string OverlappingIntervals = "OVERLAPPING_INTERVALS", InvalidRatio = "INVALID_RATIO", InvalidDay = "INVALID_DAY";
        public const string CompanyNotSelected = "COMPANY_NOT_SELECTED", MissingInterval = "MISSING_INTERVAL";
        public const string RequestIdConflict = "REQUEST_ID_CONFLICT", RateLimited = "RATE_LIMITED";
        public const string InvalidRequest = "INVALID_REQUEST", NotConnected = "NOT_CONNECTED", InvalidConnection = "INVALID_CONNECTION";
        public const string RoomFull = "ROOM_FULL", RoomEnded = "ROOM_ENDED", GameStarted = "GAME_STARTED";
        public const string InvalidName = "INVALID_NAME", InvalidReconnectToken = "INVALID_RECONNECT_TOKEN";
        public const string HostOnly = "HOST_ONLY", NotAllReady = "NOT_ALL_READY", DeadlineExceeded = "DEADLINE_EXCEEDED";
        public const string OrderLocked = "ORDER_LOCKED";
        public const string GamePaused = "GAME_PAUSED";
        public const string InvalidAbility = "INVALID_ABILITY", AbilityLocked = "ABILITY_LOCKED";
        public const string InvalidBid = "INVALID_BID", BidLocked = "BID_LOCKED";
        public const string InvalidItem = "INVALID_ITEM", NotEnoughAssets = "NOT_ENOUGH_ASSETS";
        public const string InformationWindowNotFiveDays = "INFORMATION_WINDOW_NOT_FIVE_DAYS";
        public const string InformationWindowTooClose = "INFORMATION_WINDOW_TOO_CLOSE";
    }
}
