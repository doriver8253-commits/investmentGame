// 남은 시간 '표시' 계산. 단계 진행·마감 판정은 A(서버)가 한다. D는 단계를 넘기지 않는다.
// 서버 경과초를 받은 순간의 로컬 시각을 기억해 두고, 이후 로컬 시간이 흐른 만큼 더해 추정한다.
using System;
using BlackWhale.Shared;

namespace BlackWhale.UI.Presentation
{
    public sealed class PhaseClock
    {
        private double _serverSecondsAtSync;
        private double _localSecondsAtSync;
        private bool _synced;

        /// <param name="serverElapsedSeconds">서버가 보낸 경과초</param>
        /// <param name="localNowSeconds">수신 순간의 로컬 시각(예: Time.realtimeSinceStartup)</param>
        public void Sync(double serverElapsedSeconds, double localNowSeconds)
        {
            _serverSecondsAtSync = serverElapsedSeconds;
            _localSecondsAtSync = localNowSeconds;
            _synced = true;
        }

        public double EstimatedServerSeconds(double localNowSeconds)
            => _synced ? _serverSecondsAtSync + (localNowSeconds - _localSecondsAtSync) : 0;

        /// <summary>선택 단계면 현재 차례 4초 마감, 아니면 단계 마감 기준.</summary>
        public static double DeadlineOf(PublicState s)
        {
            if (s == null) return 0;
            if (s.publicSelectionActive) return s.publicSelectionDeadlineAtServerSeconds;
            if (s.phase == Protocol.Selection && s.selectionDeadlineAtServerSeconds > 0)
                return s.selectionDeadlineAtServerSeconds;
            return s.phaseEndsAtServerSeconds;
        }

        /// <summary>대기실·종료처럼 시간이 없는 단계면 false.</summary>
        public static bool HasDeadline(PublicState s)
            => s != null && s.phase != Protocol.Lobby && s.phase != Protocol.Ended && DeadlineOf(s) > 0;

        /// <summary>남은 시간(소수 포함). 막대가 부드럽게 줄어드는 연출용. 마감이 없으면 0.</summary>
        public double RemainingExact(PublicState s, double localNowSeconds)
        {
            if (!HasDeadline(s) || !_synced) return 0;
            double remain = DeadlineOf(s) - (s.paused ? s.serverElapsedSeconds : EstimatedServerSeconds(localNowSeconds));
            return remain <= 0 ? 0 : remain;
        }

        public int RemainingSeconds(PublicState s, double localNowSeconds)
        {
            if (!HasDeadline(s) || !_synced) return 0;
            double remain = DeadlineOf(s) - (s.paused ? s.serverElapsedSeconds : EstimatedServerSeconds(localNowSeconds));
            return remain <= 0 ? 0 : (int)Math.Ceiling(remain - 1e-6);
        }
    }
}
