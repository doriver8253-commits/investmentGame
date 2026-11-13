// NEXT-D02 마우스로 진입·청산 날짜를 고르는 초안 편집기(화면 객체 의존 없음).
// - 첫 클릭 = 진입일 후보, 둘째 클릭 = 청산일. 합법이면 InvestmentDraft.TryAddInterval로 완성 구간에 넣는다.
// - 불법(같은 날/역순, 보유 4일·11일, 겹침, 3개째, 30일 초과)이면 완성 구간을 바꾸지 않고 이유를 돌려준다.
//   진입일 후보는 남겨 두어 다른 청산일을 다시 고를 수 있다.
// - 우클릭/취소 = 진행 중 후보만 해제. 완성 구간 삭제는 InvestmentDraft.RemoveInterval.
// - 이 클래스는 서버에 아무것도 보내지 않는다. 주문 확정 때만 기존 SUBMIT_INVESTMENT 1회.
// - Editable=false(응답 대기·주문 잠금·단계 종료)면 모든 편집을 거절한다.
using System.Collections.Generic;
using BlackWhale.Shared;

namespace BlackWhale.UI.Presentation
{
    public enum PickOutcome
    {
        CandidateSet,      // 진입일 후보 지정
        IntervalAdded,     // 구간 완성
        CandidateCleared,  // 후보 취소
        Rejected,          // 규칙 위반(무변경)
        Locked             // 편집 불가 상태(무변경)
    }

    public struct PickResult
    {
        public PickOutcome Outcome;
        public string ErrorCode;  // Rejected일 때 공통 오류 코드
        public string Message;    // 화면 안내 문구

        public bool Changed => Outcome == PickOutcome.CandidateSet || Outcome == PickOutcome.IntervalAdded || Outcome == PickOutcome.CandidateCleared;
    }

    public sealed class IntervalPicker
    {
        private readonly Dictionary<string, int> _candidates = new Dictionary<string, int>();

        public InvestmentDraft Draft { get; private set; }
        public bool Editable { get; set; } = true;

        public IntervalPicker(InvestmentDraft draft) => Draft = draft;

        /// <summary>턴 전환·선택 기업 변경 시 새 초안으로 바꾸고 모든 후보를 버린다.</summary>
        public void Reset(InvestmentDraft draft)
        {
            Draft = draft;
            _candidates.Clear();
        }

        public int? CandidateOf(string companyId)
            => companyId != null && _candidates.TryGetValue(companyId, out var d) ? d : (int?)null;

        public PickResult Click(string companyId, int day)
        {
            if (!Editable || Draft == null) return Locked();
            if (companyId != Draft.FirstCompanyId && companyId != Draft.SecondCompanyId)
                return Reject(ErrorCodes.CompanyNotSelected);
            if (day < UiRules.MinDay || day > UiRules.MaxDay) return Reject(ErrorCodes.InvalidDay);

            var intervals = Draft.IntervalsOf(companyId);
            int minimumHoldingDays = Draft.EntryAtOpen ? UiRules.StockMinHoldingDays : UiRules.MinHoldingDays;
            int maximumHoldingDays = Draft.EntryAtOpen ? UiRules.StockMaxHoldingDays : UiRules.MaxHoldingDays;
            if (!_candidates.TryGetValue(companyId, out var entry))
            {
                if (intervals.Count >= UiRules.MaxIntervalsPerCompany) return Reject(ErrorCodes.TooManyIntervals);
                if (day > UiRules.MaxDay - minimumHoldingDays)
                    return Reject(ErrorCodes.InvalidHoldingDays, day + "일에 진입하면 30일 안에 최소 " + minimumHoldingDays + "일을 보유할 수 없습니다.");
                _candidates[companyId] = day;
                return new PickResult
                {
                    Outcome = PickOutcome.CandidateSet,
                    ErrorCode = "",
                    Message = "진입 " + day + "일 · 청산일을 클릭하세요(" + (day + minimumHoldingDays) + "~"
                              + System.Math.Min(day + maximumHoldingDays, UiRules.MaxDay) + "일). 우클릭으로 취소"
                };
            }

            if (day <= entry) return Reject(ErrorCodes.InvalidDay, "청산일은 진입일(" + entry + "일)보다 뒤여야 합니다.");
            string error = Draft.TryAddInterval(companyId, entry, day); // 실패하면 초안 무변경
            if (error.Length > 0) return Reject(error);

            _candidates.Remove(companyId);
            return new PickResult
            {
                Outcome = PickOutcome.IntervalAdded,
                ErrorCode = "",
                Message = entry + "일 → " + day + "일 (보유 " + (day - entry) + "일) 구간을 추가했습니다."
            };
        }

        public PickResult Cancel(string companyId)
        {
            if (!Editable) return Locked();
            if (companyId == null || !_candidates.Remove(companyId))
                return new PickResult { Outcome = PickOutcome.Rejected, ErrorCode = "", Message = "취소할 진입일 후보가 없습니다." };
            return new PickResult { Outcome = PickOutcome.CandidateCleared, ErrorCode = "", Message = "진입일 후보를 취소했습니다." };
        }

        public bool Remove(string companyId, int index)
        {
            if (!Editable || Draft == null) return false;
            var list = Draft.IntervalsOf(companyId);
            if (index < 0 || index >= list.Count) return false;
            Draft.RemoveInterval(companyId, index);
            return true;
        }

        /// <summary>지금 클릭해서 고를 수 있는 날짜 범위(포함). 열람 여부와 무관하다(미열람 날짜도 포함).
        /// 후보 없음 → 진입 가능 0~25일, 후보 있음 → 청산 가능 진입+5~진입+10일(30일 이하). 더 고를 수 없으면 null.</summary>
        public UnityEngine.Vector2Int? AllowedRange(string companyId)
        {
            if (!Editable || Draft == null) return null;
            if (companyId != Draft.FirstCompanyId && companyId != Draft.SecondCompanyId) return null;
            var entry = CandidateOf(companyId);
            if (entry.HasValue)
            {
                int minimumHoldingDays = Draft.EntryAtOpen ? UiRules.StockMinHoldingDays : UiRules.MinHoldingDays;
                int maximumHoldingDays = Draft.EntryAtOpen ? UiRules.StockMaxHoldingDays : UiRules.MaxHoldingDays;
                return new UnityEngine.Vector2Int(entry.Value + minimumHoldingDays,
                    System.Math.Min(entry.Value + maximumHoldingDays, UiRules.MaxDay));
            }
            if (Draft.IntervalsOf(companyId).Count >= UiRules.MaxIntervalsPerCompany) return null;
            int min = Draft.EntryAtOpen ? UiRules.StockMinHoldingDays : UiRules.MinHoldingDays;
            return new UnityEngine.Vector2Int(UiRules.MinDay, UiRules.MaxDay - min);
        }

        /// <summary>마우스를 올린 청산일 미리보기가 합법인지(화면 색 표시용).</summary>
        public bool PreviewIsValid(string companyId, int hoverDay)
        {
            var entry = CandidateOf(companyId);
            if (!entry.HasValue || Draft == null || hoverDay <= entry.Value) return false;
            var candidate = new List<Interval>(Draft.IntervalsOf(companyId))
            {
                new Interval { entryDay = entry.Value, exitDay = hoverDay }
            };
            return OrderInputValidator.ValidateIntervals(candidate).Length == 0;
        }

        private static PickResult Locked()
            => new PickResult { Outcome = PickOutcome.Locked, ErrorCode = "", Message = "지금은 주문을 바꿀 수 없습니다." };

        private static PickResult Reject(string code, string message = null)
            => new PickResult { Outcome = PickOutcome.Rejected, ErrorCode = code, Message = message ?? ErrorMessages.ToKorean(code) };
    }
}
