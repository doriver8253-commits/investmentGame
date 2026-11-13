// NEXT-D01 일봉(캔들) 그래프(uGUI Graphic). 실제 주식 차트처럼 하루 = 봉 1개.
// - 세로축 = 0일차 대비 %. 몸통은 시가(전날 종가)→종가(그날 값) 사이에 뜬다(0% 기준선에 붙지 않음).
// - 양봉(종가>시가) 빨강, 음봉(종가<시가) 파랑, 보합은 가는 가로선. 색과 함께 툴팁의 '양봉/음봉' 문구로도 구분된다.
// - 하루 값이 1개뿐이라 고가·저가가 없으므로 꼬리는 그리지 않는다(값을 지어내지 않음).
// - 전날이 미열람이면 시가를 모르므로 종가 위치에 회색 표식만 그린다. 미열람 날짜는 칸 전체 음영(값 없음).
// - 날짜 좌표는 DayAxis(31등분)를 쓰며, 같은 규칙으로 클릭 날짜를 계산한다(ChartPointerLayer).
// - 선택 구간·진입일 후보·미리보기·마우스 위치 강조도 날짜 기준으로 그린다(값이 없는 날짜도 가능).
using System.Collections.Generic;
using System.Linq;
using BlackWhale.Shared;
using BlackWhale.UI.Presentation;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace BlackWhale.UI.View
{
    [RequireComponent(typeof(CanvasRenderer))]
    public sealed class BarChartGraphic : MaskableGraphic
    {
        public static readonly Color32 UpColor = new Color32(0xDC, 0x26, 0x26, 0xFF);
        public static readonly Color32 DownColor = new Color32(0x25, 0x63, 0xEB, 0xFF);
        public static readonly Color32 ZeroColor = new Color32(0x37, 0x41, 0x51, 0xFF);
        public static readonly Color32 FlatColor = new Color32(0x37, 0x41, 0x51, 0xFF);
        public static readonly Color32 UnknownOpenColor = new Color32(0x6B, 0x72, 0x80, 0xFF);
        public static readonly Color32 SubGridColor = new Color32(0xE5, 0xE7, 0xEB, 0xFF);
        public static readonly Color32 HiddenColor = new Color32(0x9C, 0xA3, 0xAF, 0x38);
        public static readonly Color32 GridColor = new Color32(0xCB, 0xD0, 0xD8, 0xFF);
        public static readonly Color32 BandColor = new Color32(0x25, 0x63, 0xEB, 0x2E);
        public static readonly Color32 BandEdgeColor = new Color32(0x25, 0x63, 0xEB, 0xCC);
        public static readonly Color32 CandidateColor = new Color32(0x25, 0x63, 0xEB, 0xFF); // 진입일 후보: 파란색
        public static readonly Color32 PreviewOkColor = new Color32(0x25, 0x63, 0xEB, 0x30);   // 구간 미리보기: 파란색
        public static readonly Color32 PreviewBadColor = new Color32(0x6B, 0x72, 0x80, 0x22);  // 불가 구간: 회색(빨간색 대신)
        public static readonly Color32 AllowedColor = new Color32(0x05, 0x96, 0x69, 0x1F);
        public static readonly Color32 AllowedEdgeColor = new Color32(0x05, 0x96, 0x69, 0x8C);
        public static readonly Color32 HoverColor = new Color32(0x11, 0x18, 0x27, 0x22);

        private DayBar[] _bars = new DayBar[0];

        // 시장 공개 연출: 공개된 시세가 0일 → 30일 순서로 금빛 선을 따라 휩쓸며 나타난다.
        // 처음 보는 공개 데이터일 때 한 번만, 모든 차트가 같은 시각 기준으로 움직인다(화면을 다시 그려도 재시작 안 함).
        public const float RevealSeconds = 2.6f;
        public static readonly Color32 SweepColor = new Color32(0xF5, 0xB0, 0x0B, 0xFF);
        private static readonly HashSet<int> s_seenReveals = new HashSet<int>();
        private static float s_revealStart = -100f;
        private bool _hasRevealed;

        private float RevealProgressDay
            => !_hasRevealed || !Application.isPlaying ? 999f : (Time.unscaledTime - s_revealStart) / RevealSeconds * DayAxis.Count;

        private void Update()
        {
            if (_hasRevealed && RevealProgressDay <= DayAxis.Count + 2) SetVerticesDirty();
        }

        private void NoteReveal()
        {
            _hasRevealed = false;
            int key = 17, count = 0;
            foreach (var b in _bars)
                if (b.Source == DailyBarModel.RevealedSource && b.Bps.HasValue) { key = key * 31 + b.Day * 7919 + b.Bps.Value; count++; }
            if (count == 0) return;
            _hasRevealed = true;
            if (!Application.isPlaying) return;
            if (s_seenReveals.Add(key) && Time.unscaledTime - s_revealStart > RevealSeconds + 1f)
                s_revealStart = Time.unscaledTime;
        }
        private ChartVerticalRange _verticalRange = ChartVerticalRange.FromVisibleValues(null);
        private readonly List<Vector2Int> _bands = new List<Vector2Int>();
        private RevealedInvestment[] _revealedInvestments = new RevealedInvestment[0];
        private int? _candidate;
        private Vector2Int? _preview;
        private bool _previewOk;
        private int? _hover;
        private Vector2Int? _allowed;
        private Vector2Int? _informationRange;
        public void SetInformationWindow(int? start) => SetInformationRange(start.HasValue ? new Vector2Int(start.Value, start.Value + 4) : (Vector2Int?)null);
        public void SetInformationRange(Vector2Int? range) { _informationRange = range; SetVerticesDirty(); }
        private RectTransform _tradeMarkerRoot;
        private string _tradeMarkerKey;

        protected override void Awake()
        {
            base.Awake();
            // 입력이 필요하면 ChartPointerLayer가 켠다. 이미 붙어 있으면 끄지 않는다(AddComponent 순서 무관).
            raycastTarget = GetComponent<ChartPointerLayer>() != null;
        }

        public void SetInteractive(bool on) => raycastTarget = on;

        public void SetData(DayBar[] bars, int limitBps) => SetData(bars); // v2 caller compatibility; limits are not display bounds
        public void SetData(DayBar[] bars)
        {
            _bars = bars ?? new DayBar[0];
            NoteReveal();
            _verticalRange = ChartVerticalRange.FromVisibleValues(_bars.Where(b => b.Bps.HasValue).Select(b => b.Bps.Value)
                .Concat(_bars.Where(b => b.Bps.HasValue && b.OpenBps.HasValue).Select(b => b.OpenBps.Value)));
            SetVerticesDirty();
        }

        public void SetBands(IEnumerable<Vector2Int> bands)
        {
            _bands.Clear();
            if (bands != null) _bands.AddRange(bands);
            SetVerticesDirty();
        }

        public void SetTradeIntervals(IEnumerable<Interval> intervals, bool planned = false)
        {
            _revealedInvestments = new RevealedInvestment[0];
            var ranges = (intervals ?? new Interval[0]).Where(i => i != null).ToArray();
            SetBands(ranges.Select(i => new Vector2Int(i.entryDay, i.exitDay)));
            string key = planned + ":" + string.Join(";", ranges.Select(i => i.entryDay + "," + i.exitDay));
            if (key == _tradeMarkerKey) return;
            _tradeMarkerKey = key;
            if (_tradeMarkerRoot == null)
            { _tradeMarkerRoot = UiKit.Rect(transform, "TradeMarkers"); UiKit.Stretch(_tradeMarkerRoot); }
            UiKit.Clear(_tradeMarkerRoot);
            for (int i = 0; i < ranges.Length; i++)
            {
                TradeMarker("EntryMarker_" + i, ranges[i].entryDay + "일 시가", ranges[i].entryDay, (i % 2) * 2, Palette.Success, false);
                TradeMarker("ExitMarker_" + i, ranges[i].exitDay + "일 종가", ranges[i].exitDay, (i % 2) * 2 + 1, Palette.Accent, true);
            }
        }
        public void SetRevealedInvestments(IEnumerable<RevealedInvestment> investments)
        {
            _bands.Clear();
            _revealedInvestments = (investments ?? new RevealedInvestment[0]).ToArray();
            string key = "reveal:" + string.Join(";", _revealedInvestments.Select(i => i.playerId + ":" + i.returnBps + ":" +
                string.Join(",", i.intervals.Select(v => v.entryDay + "-" + v.exitDay + "=" + v.returnBps))));
            SetVerticesDirty();
            if (key == _tradeMarkerKey) return;
            _tradeMarkerKey = key;
            if (_tradeMarkerRoot == null)
            { _tradeMarkerRoot = UiKit.Rect(transform, "TradeMarkers"); UiKit.Stretch(_tradeMarkerRoot); }
            UiKit.Clear(_tradeMarkerRoot);
            for (int i = 0; i < _revealedInvestments.Length; i++)
            {
                var p = _revealedInvestments[i];
                var label = UiKit.Label(_tradeMarkerRoot, "■ " + p.playerId + "  " + DisplayFormat.Percent(p.returnBps), 10,
                    Palette.Player(p.playerId), TextAnchor.UpperLeft, FontStyle.Bold);
                label.name = "RevealedPlayer_" + p.playerId;
                UiKit.Place(label.rectTransform, new Vector2(0, 1), new Vector2(0, 1), new Vector2(3, -i * 14), new Vector2(128, 14));
            }
        }
        private void TradeMarker(string name, string text, int day, int row, Color color, bool exit)
        {
            var label = UiKit.Label(_tradeMarkerRoot, text, 9, color, exit ? TextAnchor.UpperRight : TextAnchor.UpperLeft, FontStyle.Bold);
            label.name = name;
            float anchor = (day + (exit ? 1f : 0f)) / DayAxis.Count;
            float width = rectTransform.rect.width;
            float inset = width <= 0 ? 0 : exit ? Mathf.Max(0, 64 - width * anchor) : Mathf.Min(0, width * (1 - anchor) - 64);
            UiKit.Place(label.rectTransform, new Vector2(anchor, 1), new Vector2(exit ? 1 : 0, 1),
                new Vector2(inset, -row * 13), new Vector2(64, 13));
        }

        public void SetCandidate(int? day)
        {
            _candidate = day;
            SetVerticesDirty();
        }

        public void SetPreview(Vector2Int? range, bool ok)
        {
            _preview = range;
            _previewOk = ok;
            SetVerticesDirty();
        }

        /// <summary>지금 클릭할 수 있는 날짜 범위(포함)를 옅은 초록으로 표시. 미열람 날짜도 포함된다.</summary>
        public void SetAllowed(Vector2Int? range)
        {
            if (_allowed == range) return;
            _allowed = range;
            SetVerticesDirty();
        }

        public void SetHover(int? day)
        {
            if (_hover == day) return;
            _hover = day;
            SetVerticesDirty();
        }

        /// <summary>이 그래프의 로컬 x → 날짜(그리기와 같은 좌표).</summary>
        public int? DayAtLocalX(float localX)
        {
            var r = rectTransform.rect;
            return DayAxis.DayAt(localX, r.xMin, r.width);
        }

        private float ValueY(Rect r, int bps)
        {
            return r.yMin + (float)_verticalRange.Fraction(bps) * r.height;
        }

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            var r = rectTransform.rect; // DayAxis와 같은 사각형(클릭 판정과 일치)
            if (r.width <= 0 || r.height <= 0) return;
            float slot = DayAxis.SlotWidth(r.width);
            float zeroY = ValueY(r, 0);

            float sweep = RevealProgressDay;
            // 미열람 날짜: 칸 전체 음영(공개 연출 중 아직 선이 지나가지 않은 공개 날짜도 가려 둔다)
            for (int i = 0; i < _bars.Length; i++)
            {
                bool waiting = _bars[i].Source == DailyBarModel.RevealedSource && _bars[i].Day + 0.5f > sweep;
                if (_bars[i].State != DayBarState.Hidden && !waiting) continue;
                float x0 = DayAxis.LeftX(_bars[i].Day, r.xMin, r.width);
                Quad(vh, x0 + 0.5f, r.yMin, x0 + slot - 0.5f, r.yMax, HiddenColor);
            }

            // 클릭 가능한 날짜(진입 가능 범위 또는 청산 가능 범위). 미열람 음영 위에 겹쳐 그린다.
            if (_allowed.HasValue)
            {
                int a = Mathf.Clamp(_allowed.Value.x, 0, DayAxis.Count - 1);
                int b = Mathf.Clamp(_allowed.Value.y, 0, DayAxis.Count - 1);
                if (b >= a)
                {
                    float ax = DayAxis.LeftX(a, r.xMin, r.width);
                    float bx = DayAxis.LeftX(b, r.xMin, r.width) + slot;
                    Quad(vh, ax, r.yMin, bx, r.yMax, AllowedColor);
                    Quad(vh, ax, r.yMin, bx, r.yMin + 2f, AllowedEdgeColor);
                }
            }

            // 시가 진입부터 종가 청산까지 양 끝 날짜의 칸 전체를 포함한다.
            if (_informationRange.HasValue)
            {
                float left = DayAxis.LeftX(_informationRange.Value.x, r.xMin, r.width);
                float right = DayAxis.LeftX(_informationRange.Value.y, r.xMin, r.width) + slot;
                // 선택한 구간: 파란색(확정 전 초안)
                var highlight = new Color32(0x25, 0x63, 0xEB, 0x30);
                var edge = new Color32(0x25, 0x63, 0xEB, 0xFF);
                Quad(vh, left, r.yMin, right, r.yMax, highlight);
                Quad(vh, left, r.yMax - 3, right, r.yMax, edge);
                Quad(vh, left, r.yMin, left + 2, r.yMax, edge);
                Quad(vh, right - 2, r.yMin, right, r.yMax, edge);
            }
            foreach (var b in _bands) Band(vh, r, b.x, b.y, BandColor, true);
            for (int p = 0; p < _revealedInvestments.Length; p++)
            {
                var position = _revealedInvestments[p];
                Color32 edge = Palette.Player(position.playerId); var fill = edge; fill.a = 18;
                foreach (var trade in position.intervals)
                {
                    Band(vh, r, trade.entryDay, trade.exitDay, fill, false);
                    float left = DayAxis.LeftX(trade.entryDay, r.xMin, r.width);
                    float right = DayAxis.LeftX(trade.exitDay + 1, r.xMin, r.width);
                    float lane = r.yMin + 3 + p * 8;
                    Quad(vh, left, lane, right, lane + 4, edge);
                    Quad(vh, left, lane, left + 2, r.yMax - p * 2, edge);
                    Quad(vh, right - 2, lane, right, r.yMax - p * 2, edge);
                }
            }
            if (_preview.HasValue) Band(vh, r, _preview.Value.x, _preview.Value.y, _previewOk ? PreviewOkColor : PreviewBadColor, false);

            // 마우스 위치
            if (_hover.HasValue && _hover.Value >= 0 && _hover.Value < DayAxis.Count)
            {
                float x0 = DayAxis.LeftX(_hover.Value, r.xMin, r.width);
                Quad(vh, x0, r.yMin, x0 + slot, r.yMax, HoverColor);
            }

            // 숫자 없는 보조 가로선과 공개된 0일차 기준선. 기업의 변동 한도는 그리지 않는다.
            foreach (float f in new[] { .25f, .5f, .75f })
            {
                float gy = r.yMin + f * r.height;
                gy = Mathf.Clamp(gy, r.yMin + 0.5f, r.yMax - 0.5f);
                Quad(vh, r.xMin, gy - 0.5f, r.xMax, gy + 0.5f, SubGridColor);
            }
            Quad(vh, r.xMin, zeroY - 0.5f, r.xMax, zeroY + 0.5f, GridColor);

            // 일봉
            float bodyW = Mathf.Max(1f, slot * 0.62f);
            for (int i = 0; i < _bars.Length; i++)
            {
                var bar = _bars[i];
                if (!bar.Bps.HasValue) continue;
                bool revealedBar = bar.Source == DailyBarModel.RevealedSource;
                if (revealedBar && bar.Day + 0.5f > sweep) continue; // 아직 공개 선이 안 지나감
                float cx = DayAxis.CenterX(bar.Day, r.xMin, r.width);
                float closeY = ValueY(r, bar.Bps.Value);
                if (revealedBar && sweep - (bar.Day + 0.5f) < 2f && bar.OpenBps.HasValue)
                {
                    // 방금 드러난 봉: 금빛 후광
                    float glow = 1f - (sweep - (bar.Day + 0.5f)) / 2f;
                    var halo = SweepColor; halo.a = (byte)(90 * glow);
                    float oy = ValueY(r, bar.OpenBps.Value);
                    Quad(vh, cx - bodyW * 0.85f, Mathf.Min(oy, closeY) - 4f, cx + bodyW * 0.85f, Mathf.Max(oy, closeY) + 4f, halo);
                }
                byte alpha = bar.Source == DailyBarModel.RevealedSource ? (byte)0x8C : (byte)0xFF;

                if (bar.Move == CandleMove.Unknown || !bar.OpenBps.HasValue)
                {
                    // 시가 모름: 종가 위치에 짧은 회색 표식(몸통 없음)
                    var c = UnknownOpenColor; c.a = alpha;
                    float w = bodyW * 0.7f;
                    Quad(vh, cx - w * 0.5f, closeY - 1.5f, cx + w * 0.5f, closeY + 1.5f, c);
                    continue;
                }

                if (bar.Move == CandleMove.Flat)
                {
                    var c = FlatColor; c.a = alpha;
                    Quad(vh, cx - bodyW * 0.5f, closeY - 1f, cx + bodyW * 0.5f, closeY + 1f, c);
                    continue;
                }

                float openY = ValueY(r, bar.OpenBps.Value);
                var color = bar.Move == CandleMove.Rise ? UpColor : DownColor;
                color.a = alpha;
                float y0 = Mathf.Min(openY, closeY), y1 = Mathf.Max(openY, closeY);
                if (y1 - y0 < 2f) { float m = (y0 + y1) * 0.5f; y0 = m - 1f; y1 = m + 1f; } // 너무 얇은 몸통도 보이게
                Quad(vh, cx - bodyW * 0.5f, y0, cx + bodyW * 0.5f, y1, color);
            }

            // 공개 선(지나가는 동안만)
            if (sweep < DayAxis.Count)
            {
                float sx = r.xMin + sweep / DayAxis.Count * r.width;
                var trail = SweepColor; trail.a = 0x26;
                Quad(vh, sx - 18f, r.yMin, sx, r.yMax, trail);
                Quad(vh, sx - 1.5f, r.yMin, sx + 1.5f, r.yMax, SweepColor);
            }

            foreach (var b in _bands)
            {
                Endpoint(vh, r, b.x, false, Palette.Success);
                Endpoint(vh, r, b.y, true, Palette.Accent);
            }
            foreach (var p in _revealedInvestments)
                foreach (var v in p.intervals)
                { Endpoint(vh, r, v.entryDay, false, Palette.Player(p.playerId)); Endpoint(vh, r, v.exitDay, true, Palette.Player(p.playerId)); }

            // 진입일 후보 표식
            if (_candidate.HasValue && _candidate.Value >= 0 && _candidate.Value < DayAxis.Count)
            {
                float cx = DayAxis.CenterX(_candidate.Value, r.xMin, r.width);
                Quad(vh, cx - 1.5f, r.yMin, cx + 1.5f, r.yMax, CandidateColor);
            }
        }

        private static void Band(VertexHelper vh, Rect r, int entry, int exit, Color32 color, bool edges)
        {
            int a = Mathf.Clamp(Mathf.Min(entry, exit), 0, DayAxis.Count - 1);
            int b = Mathf.Clamp(Mathf.Max(entry, exit), 0, DayAxis.Count - 1);
            float x0 = DayAxis.LeftX(a, r.xMin, r.width);
            float x1 = DayAxis.LeftX(b + 1, r.xMin, r.width);
            Quad(vh, x0, r.yMin, x1, r.yMax, color);
            if (!edges) return;
            Quad(vh, x0, r.yMin, x0 + 2f, r.yMax, BandEdgeColor);
            Quad(vh, x1 - 2f, r.yMin, x1, r.yMax, BandEdgeColor);
        }

        private void Endpoint(VertexHelper vh, Rect r, int day, bool exit, Color32 color)
        {
            if (day < 0 || day >= _bars.Length || !_bars[day].Bps.HasValue) return;
            int? value = exit ? _bars[day].Bps : _bars[day].OpenBps;
            if (!value.HasValue) return;
            float edge = DayAxis.LeftX(day + (exit ? 1 : 0), r.xMin, r.width);
            float center = DayAxis.CenterX(day, r.xMin, r.width);
            float y = ValueY(r, value.Value);
            Quad(vh, Mathf.Min(edge, center), y - 1.5f, Mathf.Max(edge, center), y + 1.5f, color);
            Quad(vh, Mathf.Clamp(edge - 2, r.xMin, r.xMax - 4), y - 3, Mathf.Clamp(edge - 2, r.xMin, r.xMax - 4) + 4, y + 3, color);
        }

        private static void Quad(VertexHelper vh, float x0, float y0, float x1, float y1, Color32 color)
        {
            int i = vh.currentVertCount;
            var v = UIVertex.simpleVert;
            v.color = color;
            v.position = new Vector3(x0, y0); vh.AddVert(v);
            v.position = new Vector3(x0, y1); vh.AddVert(v);
            v.position = new Vector3(x1, y1); vh.AddVert(v);
            v.position = new Vector3(x1, y0); vh.AddVert(v);
            vh.AddTriangle(i, i + 1, i + 2);
            vh.AddTriangle(i + 2, i + 3, i);
        }
    }
}
