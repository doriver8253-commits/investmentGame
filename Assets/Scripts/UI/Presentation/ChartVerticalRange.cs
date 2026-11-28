using System;
using System.Collections.Generic;

namespace BlackWhale.UI.Presentation
{
    // Supply only visible opens/closes. Hidden market bounds must never enter this calculation.
    public struct ChartVerticalRange
    {
        public double MinBps, MaxBps;
        public double Span => MaxBps - MinBps;
        public double Fraction(int bps) => Math.Max(0, Math.Min(1, (bps - MinBps) / Span));
        public static ChartVerticalRange FromVisibleValues(IEnumerable<int> values)
        {
            double min = 0, max = 0; // the public day-zero baseline stays visible
            foreach (int value in values ?? new int[0]) { min = Math.Min(min, value); max = Math.Max(max, value); }
            double span = Math.Max(50, max - min), center = (min + max) * .5;
            return new ChartVerticalRange { MinBps = center - span * .62, MaxBps = center + span * .62 };
        }

        /// <summary>
        /// 게임 차트용: 열람값을 세로 가운데 절반쯤에만 그리고 위아래에 큰 여백을 둔다.
        /// 보이는 점들의 최고·최저가 곧 그래프의 천장·바닥처럼 보이지 않게 하려는 것.
        /// 실시간으로 범위를 넘으면 다음 갱신 때 축이 그만큼 넓어진다(값이 가장자리에 붙어 멈추지 않음).
        /// </summary>
        public static ChartVerticalRange OpenEnded(IEnumerable<int> values)
        {
            double min = 0, max = 0;
            foreach (int value in values ?? new int[0]) { min = Math.Min(min, value); max = Math.Max(max, value); }
            double span = Math.Max(80, max - min), center = (min + max) * .5;
            return new ChartVerticalRange { MinBps = center - span * HeadroomHalfSpan, MaxBps = center + span * HeadroomHalfSpan };
        }
        /// <summary>OpenEnded의 반쪽 높이 배율. 0.95면 열람값이 세로의 약 53%만 차지한다.</summary>
        public const double HeadroomHalfSpan = .95;

        // 세로축 % 눈금용 '깔끔한' 간격(bps). 0.1%, 0.2%, 0.25%, 0.5%, 1%, 2%, 2.5%, 5%, 10% ...
        private static readonly int[] NiceSteps = { 10, 20, 25, 50, 100, 200, 250, 500, 1000, 2000, 2500, 5000, 10000, 20000, 25000, 50000, 100000 };

        /// <summary>
        /// 세로축에 숫자로 표시할 눈금 값(bps, 오름차순). 항상 0(0일차 기준)을 포함하고,
        /// 범위 안에 들어가는 깔끔한 간격의 배수만 고른다. 눈금 수는 maxTicks 이하.
        /// </summary>
        public int[] AxisTicks(int maxTicks = 5)
        {
            if (maxTicks < 2) maxTicks = 2;
            double span = Span > 0 ? Span : 1;
            int step = NiceSteps[NiceSteps.Length - 1];
            foreach (int candidate in NiceSteps)
                if (span / candidate <= maxTicks - 1) { step = candidate; break; }
            var ticks = new List<int>();
            long first = (long)Math.Ceiling(MinBps / step), last = (long)Math.Floor(MaxBps / step);
            for (long k = first; k <= last && ticks.Count < maxTicks; k++) ticks.Add((int)(k * step));
            if (!ticks.Contains(0) && MinBps <= 0 && MaxBps >= 0) { ticks.Add(0); ticks.Sort(); }
            return ticks.ToArray();
        }
    }
}
