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
        public static readonly Color32 UpColor = new Color32(0xFF, 0x4D, 0x4F, 0xFF); // 상승 봉: 빨강(무채색 테마 예외, 검정 바탕용 밝은 빨강)
        public static readonly Color32 DownColor = new Color32(0x3D, 0x8B, 0xFF, 0xFF); // 하락 봉: 파랑(무채색 테마 예외)
        public static readonly Color32 ZeroColor = new Color32(0xA0, 0xA0, 0xA0, 0xFF);
        public static readonly Color32 FlatColor = new Color32(0xA0, 0xA0, 0xA0, 0xFF);
        public static readonly Color32 UnknownOpenColor = new Color32(0x6E, 0x6E, 0x6E, 0xFF);
        public static readonly Color32 SubGridColor = new Color32(0x1C, 0x1C, 0x1C, 0xFF);
        public static readonly Color32 HiddenColor = new Color32(0x8C, 0x8C, 0x8C, 0x50);
        public static readonly Color32 GridColor = new Color32(0x3A, 0x3A, 0x3A, 0xFF);
        public static readonly Color32 BandColor = new Color32(0xFF, 0xFF, 0xFF, 0x30); // 선택 네모칸과 같은 색
        public static readonly Color32 BandEdgeColor = new Color32(0xFF, 0xFF, 0xFF, 0xCC);
        public static readonly Color32 CandidateColor = new Color32(0xFF, 0xFF, 0xFF, 0xFF); // 진입일 후보: 파란색
        public static readonly Color32 PreviewOkColor = new Color32(0xFF, 0xFF, 0xFF, 0x30);   // 구간 미리보기: 파란색
        public static readonly Color32 PreviewBadColor = new Color32(0x8C, 0x8C, 0x8C, 0x22);  // 불가 구간: 회색(빨간색 대신)
        public static readonly Color32 AllowedColor = new Color32(0x22, 0xC5, 0x5E, 0x1A);
        public static readonly Color32 AllowedEdgeColor = new Color32(0x22, 0xC5, 0x5E, 0x8C);
        public static readonly Color32 HoverColor = new Color32(0xFF, 0xFF, 0xFF, 0x14);
        public static readonly Color32 CurrentDayColor = new Color32(0xFF, 0xFF, 0xFF, 0x18);
        public static readonly Color32 CurrentDayEdgeColor = new Color32(0xFF, 0xFF, 0xFF, 0xE0);
        public static readonly Color32 TrendColor = new Color32(0xCF, 0xCF, 0xCF, 0xFF);
        private int? _trendThroughDay;
        public void SetTrendThroughDay(int? day)
        {
            if (day.HasValue) day = Mathf.Clamp(day.Value, 0, 30);
            if (_trendThroughDay == day) return;
            _trendThroughDay = day;
            SetVerticesDirty();
        }
        private int? _currentDay;
        public int? CurrentDay => _currentDay;
        public void SetCurrentDay(int? day)
        {
            if (day.HasValue && (day.Value < 1 || day.Value > 30)) day = null;
            if (_currentDay == day) return;
            _currentDay = day;
            SetVerticesDirty();
        }

        private bool _hiddenTail;
        private Text _hiddenTailLabel;
        public void SetHiddenTail(bool visible)
        {
            if (_hiddenTail == visible) return;
            _hiddenTail = visible;
            if (visible && _hiddenTailLabel == null)
            {
                _hiddenTailLabel = UiKit.Label(transform, "hidden", 14, Palette.SubText, TextAnchor.MiddleCenter, FontStyle.Bold);
                _hiddenTailLabel.name = "HiddenTailLabel";
                _hiddenTailLabel.raycastTarget = false;
                _hiddenTailLabel.resizeTextForBestFit = true;
                _hiddenTailLabel.resizeTextMinSize = 8;
                _hiddenTailLabel.resizeTextMaxSize = 14;
                _hiddenTailLabel.rectTransform.anchorMin = new Vector2(26f / DayAxis.Count, 0);
                _hiddenTailLabel.rectTransform.anchorMax = Vector2.one;
                _hiddenTailLabel.rectTransform.offsetMin = Vector2.zero;
                _hiddenTailLabel.rectTransform.offsetMax = Vector2.zero;
            }
            if (_hiddenTailLabel != null) _hiddenTailLabel.gameObject.SetActive(visible);
            SetVerticesDirty();
        }

        private DayBar[] _bars = new DayBar[0];

        // 시장 공개 연출: 공개된 시세가 0일 → 30일 순서로 금빛 선을 따라 휩쓸며 나타난다.
        // 처음 보는 공개 데이터일 때 한 번만, 모든 차트가 같은 시각 기준으로 움직인다(화면을 다시 그려도 재시작 안 함).
        public const float RevealSeconds = 2.6f;
        public static readonly Color32 SweepColor = new Color32(0xFF, 0xFF, 0xFF, 0xFF);
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
            // 위아래 여백을 크게 둬서 보이는 점들의 최고·최저가 천장·바닥처럼 보이지 않게 한다.
            _verticalRange = ChartVerticalRange.OpenEnded(_bars.Where(b => b.Bps.HasValue).Select(b => b.Bps.Value)
                .Concat(_bars.Where(b => b.Bps.HasValue && b.OpenBps.HasValue).Select(b => b.OpenBps.Value)));
            RefreshAxisLabels();
            SetVerticesDirty();
        }

        // 세로축 % 눈금: 가로 보조선 위치에 '+2%', '0%', '-1%'처럼 0일차 대비 수익률을 글자로 붙인다.
        public const int AxisTickCount = 3; // 눈금은 0%와 한두 개만(위·아래 끝 숫자가 한계처럼 보이지 않게)
        private RectTransform _axisLabelRoot;
        private string _axisKey;
        private void RefreshAxisLabels()
        {
            var ticks = _verticalRange.AxisTicks(AxisTickCount);
            string key = string.Join(",", ticks) + "|" + _verticalRange.MinBps + "|" + _verticalRange.MaxBps;
            if (key == _axisKey) return;
            _axisKey = key;
            if (_axisLabelRoot == null)
            {
                _axisLabelRoot = UiKit.Rect(transform, "AxisLabels");
                UiKit.Stretch(_axisLabelRoot);
            }
            UiKit.Clear(_axisLabelRoot);
            foreach (int tick in ticks)
            {
                float f = (float)_verticalRange.Fraction(tick);
                bool nearTop = f > .85f;
                // 숫자는 왼쪽 끝에(오른쪽 hidden 안개 안에 범위처럼 보이지 않게)
                var label = UiKit.Label(_axisLabelRoot, DisplayFormat.Percent(tick), 9,
                    tick == 0 ? (Color)ZeroColor : Palette.SubText, nearTop ? TextAnchor.UpperLeft : TextAnchor.LowerLeft);
                label.name = "AxisTick_" + tick;
                label.horizontalOverflow = HorizontalWrapMode.Overflow;
                UiKit.Place(label.rectTransform, new Vector2(0, f), new Vector2(0, nearTop ? 1 : 0), new Vector2(2, nearTop ? -1 : 1), new Vector2(48, 12));
            }
        }

        public void SetBands(IEnumerable<Vector2Int> bands)
        {
            _bands.Clear();
            if (bands != null) _bands.AddRange(bands);
            SetVerticesDirty();
        }

        private StockFill[] fills = new StockFill[0];
        private RectTransform _fillMarkerRoot;
        private string _fillMarkerKey;
        public void SetFills(StockFill[] value)
        {
            fills = value ?? System.Array.Empty<StockFill>();
            string key = string.Join(";", fills.Select(f => f.playerId + ":" + f.day + ":" + f.quantity));
            if (key != _fillMarkerKey)
            {
                _fillMarkerKey = key;
                RebuildFillMarkers();
            }
            SetVerticesDirty();
        }

        /// <summary>
        /// 실시간 차트의 체결 지점을 본인 체결만 오각형 배지로 표시합니다.
        /// 매수는 B(빨강), 매도는 S(파랑)으로 표시하며 시장 공개 화면에서는 호출하지 않습니다.
        /// </summary>
        private void RebuildFillMarkers()
        {
            if (_fillMarkerRoot == null)
            {
                _fillMarkerRoot = UiKit.Rect(transform, "FillMarkers");
                UiKit.Stretch(_fillMarkerRoot);
            }
            UiKit.Clear(_fillMarkerRoot);
            Rect chartRect = rectTransform.rect;
            if (chartRect.width <= 0 || chartRect.height <= 0) return;
            // 하루 칸을 넘지 않는 범위에서 배지와 글자를 충분히 크게 만들어 B/S가 보이게 합니다.
            float markerWidth = Mathf.Clamp(DayAxis.SlotWidth(chartRect.width) * 1.35f, 14f, 22f);
            float markerHeight = markerWidth * .82f;
            int markerFontSize = Mathf.Clamp(Mathf.RoundToInt(markerHeight * .78f), 9, 12);
            for (int i = 0; i < fills.Length; i++)
            {
                var fill = fills[i];
                if (fill.day < 0 || fill.day >= DayAxis.Count || fill.quantity == 0) continue;
                bool buy = fill.quantity > 0;
                var label = UiKit.Label(_fillMarkerRoot, buy ? "B" : "S", 10,
                    Color.white, TextAnchor.MiddleCenter, FontStyle.Bold);
                label.fontSize = markerFontSize;
                label.name = (buy ? "BuyMarker_" : "SellMarker_") + i;
                label.raycastTarget = false;
                float x = (fill.day + .5f) / DayAxis.Count;
                float pixelY = FillMarkerY(chartRect, fill, markerHeight, i);
                // 차트와 동일한 로컬 픽셀 좌표를 사용해 차트 크기 변경 때 B/S가 배지와 분리되지 않게 합니다.
                var labelRect = label.rectTransform;
                labelRect.anchorMin = Vector2.zero;
                labelRect.anchorMax = Vector2.zero;
                labelRect.pivot = new Vector2(.5f, .5f);
                labelRect.anchoredPosition = new Vector2(DayAxis.CenterX(fill.day, chartRect.xMin, chartRect.width) - chartRect.xMin,
                    pixelY - chartRect.yMin);
                labelRect.sizeDelta = new Vector2(markerWidth, markerHeight);
            }
        }

        protected override void OnRectTransformDimensionsChange()
        {
            base.OnRectTransformDimensionsChange();
            if (_fillMarkerRoot != null && fills.Length > 0)
            {
                RebuildFillMarkers();
                SetVerticesDirty();
            }
        }

        /// <summary>false면 구간 띠만 그리고 'N일 시가 / N일 종가' 글자는 붙이지 않는다(정보 구매 창 예약용).</summary>
        public bool ShowTradeLabels = true;

        /// <summary>진입·청산 띠/표시, 플레이어 수익률 글자, 매수·매도 체결 표시를 모두 지운다(시장 공개·투자 결과 화면용).</summary>
        public void ClearTradeOverlays()
        {
            _revealedInvestments = new RevealedInvestment[0];
            fills = System.Array.Empty<StockFill>();
            _fillMarkerKey = string.Empty;
            _bands.Clear();
            SetVerticesDirty();
            if (_tradeMarkerKey == "none") return;
            _tradeMarkerKey = "none";
            if (_tradeMarkerRoot != null) UiKit.Clear(_tradeMarkerRoot);
        }

        public void SetTradeIntervals(IEnumerable<Interval> intervals, bool planned = false)
        {
            _revealedInvestments = new RevealedInvestment[0];
            var ranges = (intervals ?? new Interval[0]).Where(i => i != null).ToArray();
            SetBands(ranges.Select(i => new Vector2Int(i.entryDay, i.exitDay)));
            string key = planned + ":" + ShowTradeLabels + ":" + string.Join(";", ranges.Select(i => i.entryDay + "," + i.exitDay));
            if (key == _tradeMarkerKey) return;
            _tradeMarkerKey = key;
            if (_tradeMarkerRoot == null)
            { _tradeMarkerRoot = UiKit.Rect(transform, "TradeMarkers"); UiKit.Stretch(_tradeMarkerRoot); }
            UiKit.Clear(_tradeMarkerRoot);
            for (int i = 0; i < ranges.Length && ShowTradeLabels; i++)
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
            // 26~30일은 날짜별 틈 없이 하나로 이어진 안개로 표시한다.
            if (_hiddenTail)
            {
                float left = DayAxis.LeftX(26, r.xMin, r.width);
                Quad(vh, left, r.yMin, r.xMax, r.yMax, new Color32(0x14, 0x14, 0x14, 0xF0));
            }
            // 미열람 날짜는 칸 전체를 회색 기둥으로 칠하지 않는다(위아래 끝이 있는 상자처럼 보여 천장·바닥을 암시함).
            // 대신 0% 기준선 위에 아주 작은 점만 찍어 '아직 모르는 날'임을 표시한다.
            for (int i = 0; i < _bars.Length; i++)
            {
                if (_bars[i].State != DayBarState.Hidden) continue;
                if (_hiddenTail && _bars[i].Day >= 26) continue;
                float cx0 = DayAxis.CenterX(_bars[i].Day, r.xMin, r.width);
                Quad(vh, cx0 - 1f, zeroY - 1f, cx0 + 1f, zeroY + 1f, HiddenColor);
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
            // 이미 예약된 구간과 똑같은 선택이면 선택 네모칸은 따로 그리지 않는다
            // (예약 직후 바탕을 눌러 선택이 지워져도 모양이 바뀌지 않게).
            bool selectionIsReserved = _informationRange.HasValue && _bands.Contains(_informationRange.Value);
            if (_informationRange.HasValue && !selectionIsReserved)
            {
                float left = DayAxis.LeftX(_informationRange.Value.x, r.xMin, r.width);
                float right = DayAxis.LeftX(_informationRange.Value.y, r.xMin, r.width) + slot;
                // 선택한 구간: 파란색(확정 전 초안)
                var highlight = new Color32(0xFF, 0xFF, 0xFF, 0x30);
                Quad(vh, left, r.yMin, right, r.yMax, highlight); // 테두리는 가로줄·봉 위에 따로(아래 BoxOutline)
            }
            // Current trading day is independent of the user's selected/reserved ranges.
            if (_currentDay.HasValue) {
                float left = DayAxis.LeftX(_currentDay.Value, r.xMin, r.width);
                float right = left + slot;
                Quad(vh, left, r.yMin, right, r.yMax, CurrentDayColor); // 테두리는 맨 위에 따로
            }
            foreach (var b in _bands) Band(vh, r, b.x, b.y, BandColor, false); // 테두리는 맨 위에 네 변 모두(아래)
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

            // % 눈금 보조 가로선(오른쪽 끝에 숫자 표시)과 공개된 0일차 기준선. 기업의 변동 한도는 그리지 않는다.
            foreach (int tick in _verticalRange.AxisTicks(AxisTickCount))
            {
                if (tick == 0) continue;
                float gy = Mathf.Clamp(ValueY(r, tick), r.yMin + 0.5f, r.yMax - 0.5f);
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

            // 주가 캔들 자체가 흐름을 보여 주므로 종가를 연결하는 보조 추세선은 그리지 않는다.
            // _trendThroughDay 값은 기존 호출부 호환을 위해 유지하지만 화면에는 영향을 주지 않는다.

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
                if (!ShowTradeLabels) break; // 시가·종가 글자를 숨길 때는 시가·종가 눈금도 함께 숨김
                Endpoint(vh, r, b.x, false, Palette.Success);
                Endpoint(vh, r, b.y, true, Palette.Accent);
            }
            // 투자 결과의 다른 플레이어는 색상 구간만 표시하고 시작·종료점 선은 표시하지 않는다.

            // 본인 체결 지점은 매수·매도 방향에 따라 빨강 B·파랑 S 오각형으로 표시한다.
            for (int i = 0; i < fills.Length; i++)
            {
                var fill = fills[i];
                if (fill.day < 0 || fill.day >= DayAxis.Count || fill.quantity == 0) continue;
                float x = DayAxis.CenterX(fill.day, r.xMin, r.width);
                float markerWidth = Mathf.Clamp(slot * 1.35f, 14f, 22f);
                float markerHeight = markerWidth * .82f;
                float y = FillMarkerY(r, fill, markerHeight, i);
                FillMarker(vh, x, y, markerWidth, markerHeight, fill.quantity > 0 ? UpColor : DownColor, fill.quantity > 0);
            }
            // 네모칸 테두리는 가로줄·봉·띠보다 나중에(맨 위에) 네 변 모두 그려 온전히 보이게 한다.
            // 예약된 구간도 선택 네모칸과 같은 모양(채움 + 네 변 테두리)으로 그린다.
            foreach (var b in _bands)
                BoxOutline(vh, r, DayAxis.LeftX(Mathf.Clamp(Mathf.Min(b.x, b.y), 0, DayAxis.Count - 1), r.xMin, r.width),
                    DayAxis.LeftX(Mathf.Clamp(Mathf.Max(b.x, b.y), 0, DayAxis.Count - 1) + 1, r.xMin, r.width), 2f, new Color32(0xFF, 0xFF, 0xFF, 0xFF));
            if (_informationRange.HasValue && !selectionIsReserved)
                BoxOutline(vh, r, DayAxis.LeftX(_informationRange.Value.x, r.xMin, r.width),
                    DayAxis.LeftX(_informationRange.Value.y, r.xMin, r.width) + slot, 2f, new Color32(0xFF, 0xFF, 0xFF, 0xFF));
            if (_currentDay.HasValue)
            {
                float left = DayAxis.LeftX(_currentDay.Value, r.xMin, r.width);
                BoxOutline(vh, r, left, left + slot, 2f, CurrentDayEdgeColor);
            }
            // 진입일 후보 표식
            if (_candidate.HasValue && _candidate.Value >= 0 && _candidate.Value < DayAxis.Count)
            {
                float cx = DayAxis.CenterX(_candidate.Value, r.xMin, r.width);
                Quad(vh, cx - 1.5f, r.yMin, cx + 1.5f, r.yMax, CandidateColor);
            }
        }

        private static void BoxOutline(VertexHelper vh, Rect r, float left, float right, float width, Color32 color)
        {
            Quad(vh, left, r.yMin, right, r.yMin + width, color);
            Quad(vh, left, r.yMax - width, right, r.yMax, color);
            Quad(vh, left, r.yMin, left + width, r.yMax, color);
            Quad(vh, right - width, r.yMin, right, r.yMax, color);
        }

        private float FillMarkerY(Rect r, StockFill fill, float markerHeight, int fillIndex)
        {
            bool buy = fill.quantity > 0;
            int stack = 0;
            for (int i = 0; i < fillIndex; i++)
                if (fills[i].day == fill.day && (fills[i].quantity > 0) == buy) stack++;
            if (_bars.Length == 0) return r.center.y;
            int day = Mathf.Clamp(fill.day, 0, _bars.Length - 1);
            int? close = day < _bars.Length ? _bars[day].Bps : null;
            int? open = day < _bars.Length ? _bars[day].OpenBps : null;
            float baseValue = close.HasValue ? close.Value : 0f;
            if (open.HasValue && close.HasValue)
                baseValue = buy ? Mathf.Min(open.Value, close.Value) : Mathf.Max(open.Value, close.Value);
            float y = ValueY(r, Mathf.RoundToInt(baseValue));
            float offset = markerHeight * .65f + 3f + stack * markerHeight * .9f;
            return Mathf.Clamp(y + (buy ? -offset : offset), r.yMin + markerHeight * .5f, r.yMax - markerHeight * .5f);
        }

        private static void FillMarker(VertexHelper vh, float centerX, float centerY, float width, float height, Color32 color, bool buy)
        {
            // 실제 차트의 체결 태그처럼 내부를 채운 오각형 배지로 표시한다.
            // 매수는 아래쪽, 매도는 위쪽을 가리키도록 꼭짓점 방향을 바꾼다.
            float halfWidth = width * .5f, halfHeight = height * .5f;
            Vector3[] points = buy
                ? new[] {
                    new Vector3(centerX - halfWidth, centerY + halfHeight),
                    new Vector3(centerX + halfWidth, centerY + halfHeight),
                    new Vector3(centerX + halfWidth, centerY - halfHeight + height * .2f),
                    new Vector3(centerX, centerY - halfHeight),
                    new Vector3(centerX - halfWidth, centerY - halfHeight + height * .2f)
                }
                : new[] {
                    new Vector3(centerX, centerY + halfHeight),
                    new Vector3(centerX + halfWidth, centerY + halfHeight - height * .2f),
                    new Vector3(centerX + halfWidth, centerY - halfHeight),
                    new Vector3(centerX - halfWidth, centerY - halfHeight),
                    new Vector3(centerX - halfWidth, centerY + halfHeight - height * .2f)
                };
            int start = vh.currentVertCount;
            for (int i = 0; i < points.Length; i++)
            {
                var vertex = UIVertex.simpleVert;
                vertex.color = color;
                vertex.position = points[i];
                vh.AddVert(vertex);
            }
            for (int i = 1; i < points.Length - 1; i++)
                vh.AddTriangle(start, start + i, start + i + 1);
        }

        private static void OutlineSegment(VertexHelper vh, Vector3 a, Vector3 b, float thickness, Color32 color)
        {
            Vector3 direction = b - a;
            if (direction.sqrMagnitude <= 0.001f) return;
            Vector3 normal = new Vector3(-direction.y, direction.x, 0f).normalized * (thickness * .5f);
            int index = vh.currentVertCount;
            var v = UIVertex.simpleVert;
            v.color = color;
            v.position = a - normal; vh.AddVert(v);
            v.position = a + normal; vh.AddVert(v);
            v.position = b + normal; vh.AddVert(v);
            v.position = b - normal; vh.AddVert(v);
            vh.AddTriangle(index, index + 1, index + 2);
            vh.AddTriangle(index + 2, index + 3, index);
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
