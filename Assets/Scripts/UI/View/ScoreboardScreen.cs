// NEXT-D05 10턴 전광판 화면.
// - 진행 중: 턴 진행 띠(1~10, 3·6·9 입찰 표시) / 순위표(순위 변화·총자산·이번 턴·누적) / 턴 수익률 추이 / 공개 이벤트
// - 최종: 순위별 카드(우승 강조)·최고 단일 턴 수익률·추이·동점 처리 안내
// - 모델(ScoreboardModel)만 그린다. 현재 계약의 SCOREBOARD는 ScoreboardModel.FromScoreboard로 바로 변환할 수 있다.
// - 타인의 코인·구매 정보·투자 기업은 표시하지 않는다.
using System.Collections.Generic;
using System.Linq;
using BlackWhale.UI.Presentation;
using UnityEngine;
using UnityEngine.UI;

namespace BlackWhale.UI.View
{
    public sealed class ScoreboardScreen
    {
        private readonly RectTransform _root;
        private int _turnCount = 10;
        private bool _stockMode;
        private bool IsSpecialTurn(int turn) => _stockMode ? turn == 1 || turn == 3 || turn == 5 : ScoreboardModel.IsAuctionTurn(turn);

        public ScoreboardScreen(RectTransform root) => _root = root;

        public void Show(ScoreboardModel m)
        {
            UiKit.Clear(_root);
            if (m == null) return;
            _turnCount = m.GameTurns; _stockMode = m.StockSystem;
            if (m.Final) BuildFinal(m);
            else BuildLive(m);
        }

        // ------------------------------------------------------------------
        // 진행 중
        // ------------------------------------------------------------------

        private void BuildLive(ScoreboardModel m)
        {
            ScreenKit.Title(_root, "<b>전광판</b>   <size=15><color=#6B7280>" + m.Turn + "턴 / " + _turnCount + "턴</color></size>");
            BuildTurnStrip(m.Turn, false);

            var left = UiKit.Rect(_root, "Ranking");
            UiKit.Anchor(left, 0, 0, 0.56f, 1);
            left.offsetMax = new Vector2(-6, -46);
            BuildRankingTable(left, m);

            var trend = UiKit.Rect(_root, "Trend");
            UiKit.Anchor(trend, 0.56f, 0.42f, 1, 1);
            trend.offsetMin = new Vector2(6, 4);
            trend.offsetMax = new Vector2(0, -46);
            BuildTrend(trend, m, "턴 수익률 추이");

            var events = UiKit.Rect(_root, "Events");
            UiKit.Anchor(events, 0.56f, 0, 1, 0.42f);
            events.offsetMin = new Vector2(6, 0);
            events.offsetMax = new Vector2(0, -4);
            BuildEvents(events, m);
        }

        /// <summary>1~10턴 진행 띠. 지난 턴=회색, 현재=주황, 3·6·9=입찰 표시.</summary>
        private void BuildTurnStrip(int current, bool allDone)
        {
            const float cell = 40f, gap = 4f;
            var strip = UiKit.Rect(_root, "TurnStrip");
            UiKit.Place(strip, new Vector2(1, 1), new Vector2(1, 1), new Vector2(-4, 0), new Vector2(_turnCount * (cell + gap), 42));
            for (int t = 1; t <= _turnCount; t++)
            {
                bool done = allDone || t < current;
                bool now = !allDone && t == current;
                Color border = now ? ScreenKit.Amber : done ? Palette.Disabled : Palette.CardBorder;
                Color fill = now ? ScreenKit.AmberSoft : done ? Palette.Disabled : Palette.Card;
                var frame = ScreenKit.Card(strip, "Turn" + t, border, fill, out var inner, now ? 2f : 1f);
                UiKit.Place(frame, new Vector2(0, 1), new Vector2(0, 1), new Vector2((t - 1) * (cell + gap), 0), new Vector2(cell, 28));
                var label = UiKit.Label(inner, t.ToString(), 13, done ? Palette.DisabledText : Palette.Text, TextAnchor.MiddleCenter, now ? FontStyle.Bold : FontStyle.Normal);
                UiKit.Stretch(label.rectTransform);
                if (IsSpecialTurn(t))
                {
                    var tag = UiKit.Label(strip, _stockMode ? "증강" : "입찰", 9, ScreenKit.Gold, TextAnchor.UpperCenter, FontStyle.Bold);
                    UiKit.Place(tag.rectTransform, new Vector2(0, 1), new Vector2(0, 1), new Vector2((t - 1) * (cell + gap), -29), new Vector2(cell, 12));
                }
            }
        }

        private void BuildRankingTable(RectTransform area, ScoreboardModel m)
        {
            var frame = ScreenKit.Card(area, "Table", out var inner);
            UiKit.Stretch(frame);

            var head = ScreenKit.TopStrip(inner, "Head", 8, 22, 14, 14);
            Column(head, "순위", 0.00f, 0.15f, TextAnchor.MiddleLeft, 12, Palette.SubText);
            Column(head, "플레이어", 0.15f, 0.42f, TextAnchor.MiddleLeft, 12, Palette.SubText);
            Column(head, "총자산", 0.42f, 0.62f, TextAnchor.MiddleRight, 12, Palette.SubText);
            Column(head, "이번 턴", 0.62f, 0.80f, TextAnchor.MiddleRight, 12, Palette.SubText);
            Column(head, "누적", 0.80f, 1.00f, TextAnchor.MiddleRight, 12, Palette.SubText);

            const float rowTop = 36, rowStep = 68;
            var ranked = m.Ranked.ToList();
            var movedUp = new List<RectTransform>();
            for (int index = 0; index < ranked.Count; index++)
            {
                var p = ranked[index];
                float y = rowTop + index * rowStep;
                var row = ScreenKit.TopStrip(inner, "Row_" + p.Id, y, 62, 8, 8);
                var bg = UiKit.Panel(row, "Bg", p.IsMe ? Palette.AccentSoft : (p.Rank == 1 ? ScreenKit.AmberSoft : Palette.Card), rounded: true);
                UiKit.Stretch(bg.rectTransform);
                var cols = UiKit.Rect(row, "Cols");
                UiKit.Stretch(cols, 6, 0, 6, 0);

                // 순위 + 변화
                string delta = p.RankDelta > 0 ? "<color=#DC2626>▲" + p.RankDelta + "</color>"
                    : p.RankDelta < 0 ? "<color=#2563EB>▼" + (-p.RankDelta) + "</color>"
                    : "<color=#9CA3AF>-</color>";
                Column(cols, "<size=24><b>" + p.Rank + "</b></size>  <size=12>" + delta + "</size>", 0.00f, 0.15f, TextAnchor.MiddleLeft, 14, Palette.Text);

                var band = UiKit.Panel(cols, "PlayerColor", Palette.Player(p.Id));
                band.rectTransform.anchorMin = new Vector2(0.15f, 0.2f);
                band.rectTransform.anchorMax = new Vector2(0.15f, 0.8f);
                band.rectTransform.offsetMin = new Vector2(-8, 0);
                band.rectTransform.offsetMax = new Vector2(-4, 0);

                Column(cols, "<b>" + p.Name + "</b>" + (p.IsMe ? "  <size=11><color=#2563EB>나</color></size>" : ""), 0.15f, 0.42f, TextAnchor.MiddleLeft, 16, Palette.Text);
                var assets = Column(cols, DisplayFormat.Money(p.TotalAssets), 0.42f, 0.62f, TextAnchor.MiddleRight, 16, Palette.Text);
                var turnReturn = Column(cols, ScreenKit.Signed(p.TurnReturnBps), 0.62f, 0.80f, TextAnchor.MiddleRight, 16, Palette.Text);
                Column(cols, ScreenKit.Signed(p.CumulativeReturnBps) + "\n<size=11><color=#6B7280>" + DisplayFormat.SignedMoney(p.CumulativeProfit) + "</color></size>",
                    0.80f, 1.00f, TextAnchor.MiddleRight, 16, Palette.Text);

                // 정산 연출: 이전 순위 자리에서 새 자리로 이동 + 숫자 세기 + 상승 강조
                float delay = index * 0.08f;
                if (p.PrevRank > 0 && p.PrevRank != p.Rank)
                {
                    float fromY = rowTop + (p.PrevRank - 1) * rowStep;
                    UiAnim.Slide(row, new Vector2(0, -fromY), new Vector2(0, -y), UiAnim.SlideSeconds, delay);
                    if (p.RankDelta > 0)
                    {
                        movedUp.Add(row);
                        UiAnim.Pulse(row, delay + UiAnim.SlideSeconds * 0.8f, 1.04f, bg, ScreenKit.AmberSoft);
                    }
                }
                if (p.PrevTotalAssets.HasValue)
                    UiAnim.Count(assets, p.PrevTotalAssets.Value, p.TotalAssets, v => DisplayFormat.Money(v), UiAnim.CountSeconds, delay);
                UiAnim.Count(turnReturn, 0, p.TurnReturnBps, v => ScreenKit.Signed((int)v), UiAnim.CountSeconds, delay);
            }
            foreach (var r in movedUp) r.SetAsLastSibling(); // 올라오는 줄이 위에 보이게

            var note = UiKit.Label(inner, "순위는 총자산 기준 · 다른 사람의 코인·투자 기업·구매 정보는 표시하지 않습니다.", 11, Palette.SubText, TextAnchor.LowerLeft);
            UiKit.Stretch(note.rectTransform, 14, 0, 14, 10);
        }

        private static Text Column(RectTransform parent, string text, float x0, float x1, TextAnchor align, int size, Color color)
        {
            var t = UiKit.Label(parent, text, size, color, align);
            t.rectTransform.anchorMin = new Vector2(x0, 0);
            t.rectTransform.anchorMax = new Vector2(x1, 1);
            t.rectTransform.offsetMin = new Vector2(2, 0);
            t.rectTransform.offsetMax = new Vector2(-2, 0);
            return t;
        }

        private void BuildTrend(RectTransform area, ScoreboardModel m, string title)
        {
            var frame = ScreenKit.Card(area, "TrendCard", out var inner);
            UiKit.Stretch(frame);
            var head = UiKit.Label(inner, "<b>" + title + "</b>", 14, Palette.Text, TextAnchor.MiddleLeft);
            var headRt = ScreenKit.TopStrip(inner, "Head", 4, 24, 12, 12);
            head.rectTransform.SetParent(headRt, false);
            UiKit.Stretch(head.rectTransform);

            int maxAbs = 1000;
            foreach (var p in m.Players)
                foreach (var h in p.History)
                    maxAbs = Mathf.Max(maxAbs, Mathf.Abs(h.Value));
            float limit = Mathf.Ceil(maxAbs / 1000f) * 10f; // % 단위, 10% 단위로 올림

            var chartRt = UiKit.Rect(inner, "Chart");
            UiKit.Stretch(chartRt, 48, 34, 14, 40);
            var chart = chartRt.gameObject.AddComponent<LineChartGraphic>();
            chart.SetRange(0, _turnCount, -limit, limit);
            for (int t = 0; t <= _turnCount; t++) chart.AddVLine(t, IsSpecialTurn(t) ? new Color32(0xFD, 0xE6, 0x8A, 0xFF) : new Color32(0xEE, 0xF0, 0xF3, 0xFF));
            chart.AddHLine(0, Palette.Grid, 1.5f);
            foreach (var p in m.Players)
            {
                if (p.History.Count == 0) continue;
                var pts = p.History.Where(h => h.Key > 0).Select(h => new Vector2(h.Key, h.Value / 100f)).ToList();
                pts.Insert(0, Vector2.zero); // 시작 지점: 0턴 0%
                chart.AddSeries(pts, Palette.Player(p.Id), p.IsMe ? 3.5f : 2f, p.IsMe ? 7f : 5f);
            }

            Axis(inner, "+" + limit + "%", new Vector2(0, 1), new Vector2(4, -34));
            Axis(inner, "0%", new Vector2(0, 0.5f), new Vector2(4, 3));
            Axis(inner, "-" + limit + "%", new Vector2(0, 0), new Vector2(4, 48));
            for (int t = 0; t <= _turnCount; t++)
            {
                var tick = UiKit.Label(chartRt, t == 0 ? "시작" : t + "턴", 9, Palette.SubText, TextAnchor.UpperCenter);
                UiKit.Place(tick.rectTransform, new Vector2(t / (float)Mathf.Max(1, _turnCount), 0), new Vector2(0.5f, 1), new Vector2(0, -2), new Vector2(36, 12));
            }

            // 범례
            var legend = UiKit.Rect(inner, "Legend");
            legend.anchorMin = new Vector2(0, 0);
            legend.anchorMax = new Vector2(1, 0);
            legend.pivot = new Vector2(0.5f, 0);
            legend.offsetMin = new Vector2(48, 4);
            legend.offsetMax = new Vector2(-14, 20);
            var ordered = m.Players.OrderBy(p => p.Id).ToList();
            for (int i = 0; i < ordered.Count; i++)
            {
                var p = ordered[i];
                var item = UiKit.Rect(legend, "Legend_" + p.Id);
                UiKit.Anchor(item, i / (float)ordered.Count, 0, (i + 1) / (float)ordered.Count, 1);
                var dot = UiKit.Panel(item, "Dot", Palette.Player(p.Id), rounded: true);
                UiKit.Place(dot.rectTransform, new Vector2(0, 0.5f), new Vector2(0, 0.5f), Vector2.zero, new Vector2(10, 10));
                var name = UiKit.Label(item, p.Name + (p.IsMe ? " (나)" : ""), 11, Palette.Text, TextAnchor.MiddleLeft, p.IsMe ? FontStyle.Bold : FontStyle.Normal);
                UiKit.Stretch(name.rectTransform, 14, 0, 0, 0);
            }
        }

        private static void Axis(RectTransform parent, string text, Vector2 anchor, Vector2 pos)
        {
            var t = UiKit.Label(parent, text, 10, Palette.SubText, TextAnchor.MiddleRight);
            UiKit.Place(t.rectTransform, anchor, new Vector2(0, 0.5f), pos, new Vector2(40, 14));
        }

        private void BuildEvents(RectTransform area, ScoreboardModel m)
        {
            var frame = ScreenKit.Card(area, "EventCard", out var inner);
            UiKit.Stretch(frame);
            var title = UiKit.Label(inner, "<b>공개 이벤트</b>  <size=11><color=#6B7280>입찰 결과·능력 발동·공격 대상만 공개</color></size>", 14, Palette.Text, TextAnchor.MiddleLeft);
            UiKit.Place(title.rectTransform, new Vector2(0, 1), new Vector2(0, 1), new Vector2(12, -4), new Vector2(520, 24));
            title.rectTransform.anchorMax = new Vector2(1, 1);
            title.rectTransform.sizeDelta = new Vector2(-24, 24);

            var list = m.Events.OrderByDescending(e => e.Turn).Take(6).ToList();
            if (list.Count == 0)
            {
                var empty = UiKit.Label(inner, "아직 공개된 이벤트가 없습니다.", 13, Palette.SubText);
                UiKit.Stretch(empty.rectTransform, 12, 30, 12, 10);
                return;
            }
            float y = 32;
            foreach (var e in list)
            {
                var row = ScreenKit.TopStrip(inner, "Event", y, 24, 12, 12);
                ScreenKit.Chip(row, BoardEvent.KindName(e.Kind), ScreenKit.EventColor(e.Kind), Color.white,
                    new Vector2(0, 0.5f), new Vector2(0, 0.5f), Vector2.zero, 40, 20);
                var turn = UiKit.Label(row, e.Turn + "턴", 11, Palette.SubText, TextAnchor.MiddleLeft);
                UiKit.Place(turn.rectTransform, new Vector2(0, 0.5f), new Vector2(0, 0.5f), new Vector2(46, 0), new Vector2(36, 20));
                var text = UiKit.Label(row, e.Text, 13, Palette.Text, TextAnchor.MiddleLeft);
                UiKit.Stretch(text.rectTransform, 84, 0, 0, 0);
                y += 27;
            }
        }

        // ------------------------------------------------------------------
        // 최종 결과
        // ------------------------------------------------------------------

        private void BuildFinal(ScoreboardModel m)
        {
            ScreenKit.Title(_root, "<b>최종 결과</b>   <size=15><color=#6B7280>" + _turnCount + "턴 종료</color></size>");
            BuildTurnStrip(_turnCount, true);

            var podium = UiKit.Rect(_root, "Podium");
            UiKit.Anchor(podium, 0, 0.42f, 1, 1);
            podium.offsetMax = new Vector2(0, -50);

            var ranked = m.Ranked.ToList();
            int n = Mathf.Max(1, ranked.Count);
            for (int i = 0; i < ranked.Count; i++)
            {
                var p = ranked[i];
                bool winner = string.IsNullOrEmpty(m.WinnerId) ? p.Rank == 1 : p.Id == m.WinnerId;
                float height = winner ? 1f : Mathf.Max(0.62f, 0.9f - 0.1f * i);
                var col = UiKit.Rect(podium, "Place_" + p.Id);
                UiKit.Anchor(col, i / (float)n, 0, (i + 1) / (float)n, height, 6, 0);
                col.offsetMin = new Vector2(6, 0);

                var frame = ScreenKit.Card(col, "Card", winner ? ScreenKit.Gold : p.IsMe ? Palette.Accent : Palette.CardBorder,
                    winner ? ScreenKit.AmberSoft : Palette.Card, out var inner, winner || p.IsMe ? 3f : 1f);
                UiKit.Stretch(frame);

                var band = UiKit.Panel(inner, "PlayerColor", Palette.Player(p.Id), rounded: true);
                band.rectTransform.anchorMin = new Vector2(0, 1);
                band.rectTransform.anchorMax = new Vector2(1, 1);
                band.rectTransform.pivot = new Vector2(0.5f, 1);
                band.rectTransform.offsetMin = new Vector2(10, -8);
                band.rectTransform.offsetMax = new Vector2(-10, -4);

                string best = p.BestTurnBps.HasValue ? ScreenKit.Signed(p.BestTurnBps.Value) : "-";
                var text = UiKit.Label(inner,
                    (winner ? "<size=13><color=#CA8A04><b>우승</b></color></size>\n" : "") +
                    "<size=34><b>" + p.Rank + "위</b></size>\n" +
                    "<size=20><b>" + p.Name + "</b></size>" + (p.IsMe ? " <size=12><color=#2563EB>나</color></size>" : "") + "\n\n" +
                    "<size=12><color=#6B7280>최종 자산</color></size>\n<size=20><b>" + DisplayFormat.Money(p.TotalAssets) + "</b></size>\n" +
                    "<size=12><color=#6B7280>누적 수익률</color></size>  " + ScreenKit.Signed(p.CumulativeReturnBps) + "\n" +
                    "<size=12><color=#6B7280>최고 단일 턴</color></size>  " + best,
                    14, Palette.Text, TextAnchor.UpperCenter);
                UiKit.Stretch(text.rectTransform, 8, 16, 8, 8);

                // 4위부터 차례로 올라오고 우승자는 마지막에 크게 강조
                float delay = (ranked.Count - 1 - i) * 0.3f + (winner ? 0.25f : 0f);
                UiAnim.Rise(col, delay, winner ? 70f : 40f);
                if (winner) UiAnim.Pulse(col, delay + 0.55f, 1.06f);
            }

            var trend = UiKit.Rect(_root, "Trend");
            UiKit.Anchor(trend, 0, 0, 1, 0.42f);
            trend.offsetMin = new Vector2(0, 22);
            trend.offsetMax = new Vector2(0, -8);
            BuildTrend(trend, m, _turnCount + "턴 동안의 턴 수익률");

            var note = UiKit.Label(_root, string.IsNullOrEmpty(m.FinalNote)
                    ? "동점이면 최종 자산 → 남은 코인 → 최고 단일 턴 수익률 → 시작 추첨 순서로 순위를 정합니다(판정은 서버)."
                    : m.FinalNote, 11, Palette.SubText, TextAnchor.LowerLeft);
            UiKit.Stretch(note.rectTransform, 4, 0, 4, 2);
        }
    }
}
