// 서버 오류 코드 -> 사용자 안내 문구. 코드 값은 BlackWhale.Shared.ErrorCodes와 같다.
using BlackWhale.Shared;

namespace BlackWhale.UI.Presentation
{
    public static class ErrorMessages
    {
        public static string ToKorean(string errorCode)
        {
            switch (errorCode)
            {
                case "DRAFT_SHARE_LIMIT": return "한 차례에는 새로 매수하는 수량을 합쳐 최대 15주까지 투자할 수 있습니다.";
                case "INVALID_WINDOW": return "1~30일 안에서 시작일과 끝일을 선택하세요. 공용 공개는 연속 5일입니다.";
                case ErrorCodes.InformationWindowNotFiveDays: return "정보는 한 번에 연속 5일만 구매할 수 있습니다.";
                case ErrorCodes.InformationWindowTooClose: return "같은 기업 정보는 연속해서 구매할 수 없습니다. 최소 4일의 공백이 필요합니다.";
                case "PUBLIC_SELECTION_PENDING": return "4명의 공용 공개를 마친 뒤 개인 탐색을 시작합니다.";
                case "PUBLIC_SELECTION_COMPLETE": return "공용 공개가 모두 끝났습니다.";
                case "PUBLIC_WINDOW_OVERLAP": return "이미 공용 공개된 날짜와 겹칩니다. 다른 5일을 선택하세요.";
                case "NO_INFORMATION_TARGET": return "효과를 적용할 유료 구매 정보가 없습니다. 아이템은 소모되지 않았습니다.";
                case "CATEGORY_ALREADY_USED": return "이번 턴에 같은 분류의 아이템을 이미 사용했습니다.";
                case "INVALID_ITEM": return "사용할 수 있는 보유 아이템이 아닙니다.";
                case ErrorCodes.NotEnoughCoins: return "코인이 부족합니다.";
                case "MARKET_UNAVAILABLE": return "시장 정보를 불러오지 못했습니다. 코인은 차감되지 않았습니다.";
                case ErrorCodes.AlreadyVisible: return "이미 볼 수 있는 정보입니다. 코인은 차감되지 않았습니다.";
                case ErrorCodes.InvalidBundle: return "존재하지 않는 정보 묶음입니다.";
                case ErrorCodes.InvalidCompany: return "존재하지 않는 기업입니다.";
                case ErrorCodes.WrongPhase: return "지금 단계에서는 할 수 없는 행동입니다.";
                case ErrorCodes.StaleTurn: return "이미 지난 턴의 요청입니다.";
                case ErrorCodes.CompanyFull: return "이 기업은 남은 자리가 없습니다.";
                case ErrorCodes.SelectionComplete: return "이미 기업 두 개를 모두 선택했습니다.";
                case ErrorCodes.DuplicateCompany: return "이미 선택한 기업입니다.";
                case ErrorCodes.NotYourTurn: return "지금은 내 차례가 아닙니다.";
                case ErrorCodes.InvalidHoldingDays: return "청산일과 진입일의 차이는 최소 7일이어야 합니다.";
                case ErrorCodes.TooManyIntervals: return "기업마다 거래 구간은 최대 2개입니다.";
                case ErrorCodes.OverlappingIntervals: return "거래 구간이 겹칩니다. 시가 진입에서는 청산한 다음 날부터 다시 진입하세요.";
                case ErrorCodes.InvalidRatio: return "투자 비율은 5:5, 6:4, 7:3 중 하나여야 합니다.";
                case ErrorCodes.InvalidDay: return "날짜는 0~30일 정수이며 청산일이 진입일보다 뒤여야 합니다.";
                case ErrorCodes.CompanyNotSelected: return "선택한 두 기업에만 투자할 수 있습니다.";
                case ErrorCodes.MissingInterval: return "기업마다 거래 구간을 1개 이상 입력하세요.";
                case ErrorCodes.RequestIdConflict: return "요청이 중복되었습니다. 다시 시도하세요.";
                case ErrorCodes.RateLimited: return "요청이 너무 많습니다. 잠시 후 다시 시도하세요.";
                case ErrorCodes.InvalidRequest: return "잘못된 요청입니다.";
                case ErrorCodes.NotConnected: return "서버에 연결되어 있지 않습니다.";
                case ErrorCodes.InvalidConnection: return "연결 정보가 올바르지 않습니다.";
                case ErrorCodes.RoomFull: return "방이 가득 찼습니다(4명).";
                case ErrorCodes.RoomEnded: return "게임이 종료된 방입니다.";
                case ErrorCodes.GameStarted: return "이미 시작된 게임입니다.";
                case ErrorCodes.InvalidName: return "이름은 1~32자로 입력하세요.";
                case ErrorCodes.InvalidReconnectToken: return "재접속 정보가 만료되었습니다.";
                case ErrorCodes.HostOnly: return "방장만 할 수 있습니다.";
                case ErrorCodes.NotAllReady: return "4명이 모두 준비해야 시작할 수 있습니다.";
                case ErrorCodes.DeadlineExceeded: return "제한 시간이 지나 요청이 처리되지 않았습니다.";
                case ErrorCodes.OrderLocked: return "이미 주문이 확정되었습니다.";
                case ErrorCodes.GamePaused: return "게임이 일시정지되었습니다. 방장이 재개하면 진행할 수 있습니다.";
                case "HOST_DISCONNECTED": return "방장의 연결이 끊겨 게임이 종료되었습니다.";
                case "GAME_COMPLETE": return "게임이 끝났습니다.";
                case null:
                case "": return "";
                default: return "요청을 처리하지 못했습니다. (" + errorCode + ")";
            }
        }
    }
}
