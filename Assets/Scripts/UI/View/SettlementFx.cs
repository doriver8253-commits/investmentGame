// 정산·마감 연출(D). 값·진행은 서버 결과 그대로이고, 보이는 효과만 담당한다.
// - SettlementFx.Play: 턴 정산 결과가 오면 가운데 카드가 튀어나와 내 수익률·자산 변화를 세어 올리고,
//   수익이면 화면 가장자리가 금빛으로, 손실이면 붉게 번쩍이고 카드가 흔들린다. 약 3초 뒤 사라진다.
// - UrgencyEdges: 단계 마감 10초 전부터 화면 가장자리가 살짝 붉게 숨 쉬듯 물든다(시장 공개·정산·대기실·종료 제외).
using System.Collections.Generic;
using BlackWhale.UI.Presentation;
using UnityEngine;
using UnityEngine.UI;

namespace BlackWhale.UI.View
{
    public static class SettlementFx
    {
        public const float Seconds = 3.2f;

        public static void Play(RectTransform screen, int turn, bool final, int rank, int turnReturnBps, long prevAssets, long nowAssets)
        {
            if (screen == null) return;
            var old = screen.Find("SettlementFx");
            if (old != null) Object.Destroy(old.gameObject);

            var root = UiKit.Rect(screen, "SettlementFx");
            UiKit.Stretch(root);
            bool gain = turnReturnBps > 0, loss = turnReturnBps < 0;
            Color tone = gain ? Palette.Up : loss ? Palette.Down : Palette.SubText;

            // 가장자리 번쩍임(수익: 금빛, 손실: 붉은빛). 화면 전체는 덮지 않는다.
            if (gain || loss)
                EdgeFlash.Play(root, gain ? new Color(0.98f, 0.75f, 0.14f) : new Color(0.86f, 0.12f, 0.12f), 0.9f);

            // 가운데 카드
            var card = UiKit.Panel(root, "Card", ScreenKit.Ink, rounded: true);
            UiKit.Place(card.rectTransform, new Vector2(.5f, .5f), new Vector2(.5f, .5f), new Vector2(0, 10), new Vector2(430, 200));
            card.raycastTarget = false;
            var border = card.gameObject.AddComponent<Outline>();
            border.effectColor = gain ? ScreenKit.Amber : tone; border.effectDistance = new Vector2(3, -3);

            string head = final ? "최종 정산" : turn + "턴 정산";
            string rankText = rank > 0 ? (rank == 1 ? "<color=#FBBF24><b>1위!</b></color>" : rank + "위") : "";
            var title = UiKit.Label(card.transform, "<b>" + head + "</b>   <size=16>" + rankText + "</size>", 20, Color.white, TextAnchor.UpperCenter);
            UiKit.Stretch(title.rectTransform, 10, 16, 10, 0);

            var pct = UiKit.Label(card.transform, "", 46, tone, TextAnchor.MiddleCenter, FontStyle.Bold);
            UiKit.Stretch(pct.rectTransform, 10, 50, 10, 60);
            UiAnim.Count(pct, 0, turnReturnBps, v => DisplayFormat.Percent((int)v), 1.2f, 0.25f);

            var assets = UiKit.Label(card.transform, "", 18, new Color(0.9f, 0.92f, 0.95f), TextAnchor.LowerCenter);
            UiKit.Stretch(assets.rectTransform, 10, 0, 10, 18);
            UiAnim.Count(assets, prevAssets, nowAssets, v => "자산 " + DisplayFormat.Money(prevAssets) + "  →  <b>" + DisplayFormat.Money(v) + "</b>", 1.4f, 0.35f);

            UiAnim.Rise(card.rectTransform, 0f, 50f, 0.35f);
            UiAnim.Pulse(card.rectTransform, 0.35f, gain ? 1.12f : 1.05f);
            if (loss) Shake.Play(card.rectTransform, 0.4f, 0.45f, 9f);

            UiAnim.FadeOut(card.rectTransform, Seconds - 0.5f, 0.5f);
            if (Application.isPlaying) Object.Destroy(root.gameObject, Seconds + 0.3f);
        }
    }

    /// <summary>delay초 뒤에 한 번 실행(연출 순서 맞추기용). 부모가 사라지면 함께 취소된다.</summary>
    public sealed class DelayedAction : MonoBehaviour
    {
        private float _left;
        private System.Action _action;

        public static void Run(RectTransform parent, float delay, System.Action action)
        {
            if (action == null) return;
            if (parent == null || !Application.isPlaying || delay <= 0f) { action(); return; }
            var go = new GameObject("DelayedAction");
            go.transform.SetParent(parent, false);
            var d = go.AddComponent<DelayedAction>();
            d._left = delay; d._action = action;
        }

        private void Update()
        {
            _left -= Time.unscaledDeltaTime;
            if (_left > 0f) return;
            var a = _action; _action = null;
            Destroy(gameObject);
            a?.Invoke();
        }
    }

    /// <summary>결과 발표: 정산 결과가 도착한 순간(차트 공개를 다 본 뒤) 화면 가운데 큰 제목이 튀어나왔다가 사라진다. 매 턴 1번.</summary>
    public static class ResultAnnouncement
    {
        public static void Play(RectTransform screen, int turn, bool final)
        {
            if (screen == null) return;
            var old = screen.Find("ResultAnnouncement");
            if (old != null) Object.Destroy(old.gameObject);
            var root = UiKit.Rect(screen, "ResultAnnouncement");
            UiKit.Stretch(root);

            EdgeFlash.Play(root, new Color(0.98f, 0.75f, 0.14f), 1.2f);
            var band = UiKit.Panel(root, "Band", new Color(0.07f, 0.09f, 0.15f, 0.92f));
            band.raycastTarget = false;
            band.rectTransform.anchorMin = new Vector2(0, .5f); band.rectTransform.anchorMax = new Vector2(1, .5f);
            band.rectTransform.sizeDelta = new Vector2(0, 150);
            var title = UiKit.Label(band.transform, (final ? "최종" : turn + "턴") + " 결과 발표", 54, new Color32(0xFB, 0xBF, 0x24, 0xFF), TextAnchor.MiddleCenter, FontStyle.Bold);
            UiKit.Stretch(title.rectTransform, 0, 0, 0, 34);
            var sub = UiKit.Label(band.transform, "이번 턴 수익과 순위가 확정되었습니다", 18, Color.white, TextAnchor.LowerCenter);
            UiKit.Stretch(sub.rectTransform, 0, 0, 0, 16);

            UiAnim.Pulse(title.rectTransform, 0.05f, 1.25f);
            UiAnim.FadeOut(band.rectTransform, 1.8f, 0.45f);
            if (Application.isPlaying) Object.Destroy(root.gameObject, 2.6f);
        }
    }

    /// <summary>역전 표시: 정산 결과 순위가 바뀌면 화면 위쪽에 '역전!' 띠가 내려와 누가 누구를 제쳤는지 알린다.</summary>
    public static class ReversalFx
    {
        public static void Play(RectTransform screen, string text, bool mine, float delay = 0.6f)
        {
            if (screen == null || string.IsNullOrEmpty(text)) return;
            var root = UiKit.Rect(screen, "ReversalFx");
            UiKit.Stretch(root);
            var band = UiKit.Panel(root, "Band", mine ? new Color32(0xDC, 0x26, 0x26, 0xF0) : new Color32(0x7C, 0x3A, 0xED, 0xF0), rounded: true);
            band.raycastTarget = false;
            UiKit.Place(band.rectTransform, new Vector2(.5f, 1), new Vector2(.5f, 1), new Vector2(0, -90), new Vector2(560, 70));
            var label = UiKit.Label(band.transform, "<size=30><b>역전!</b></size>   " + text, 18, Color.white, TextAnchor.MiddleCenter);
            UiKit.Stretch(label.rectTransform, 10, 0, 10, 0);
            UiAnim.Rise(band.rectTransform, delay, -40f, 0.35f); // 위에서 내려오기
            UiAnim.Pulse(band.rectTransform, delay + 0.35f, 1.12f);
            Shake.Play(band.rectTransform, delay + 0.5f, 0.35f, 6f);
            UiAnim.FadeOut(band.rectTransform, delay + 3.0f, 0.4f);
            if (Application.isPlaying) Object.Destroy(root.gameObject, delay + 3.6f);
        }

        /// <summary>직전 순위와 새 순위를 비교해 '누가 누구를 제쳤는지' 문구를 만든다. 역전이 없으면 null.
        /// 가장 높은 순위에서 일어난 역전을 고른다.</summary>
        public static string Describe(IDictionary<string, int> before, IEnumerable<KeyValuePair<string, int>> after, System.Func<string, string> name, out bool involvesMe, string me)
        {
            involvesMe = false;
            if (before == null || before.Count == 0) return null;
            var now = new Dictionary<string, int>();
            foreach (var p in after) now[p.Key] = p.Value;
            string bestUp = null, bestPassed = null; int bestRank = int.MaxValue;
            foreach (var up in now)
            {
                if (!before.TryGetValue(up.Key, out int was) || up.Value >= was) continue;
                foreach (var down in now)
                {
                    if (down.Key == up.Key || !before.TryGetValue(down.Key, out int downWas)) continue;
                    // 전에는 down이 앞이었는데 이제 up이 앞
                    if (downWas < was && down.Value > up.Value && up.Value < bestRank)
                    { bestRank = up.Value; bestUp = up.Key; bestPassed = down.Key; }
                }
            }
            if (bestUp == null) return null;
            involvesMe = bestUp == me || bestPassed == me;
            return name(bestUp) + " → " + bestRank + "위 · " + name(bestPassed) + " 추월 (" + before[bestUp] + "위→" + bestRank + "위)";
        }
    }

    /// <summary>흔들림(손실 연출).</summary>
    public sealed class Shake : MonoBehaviour
    {
        private RectTransform _rt;
        private Vector2 _base;
        private float _t, _delay, _seconds, _power;

        public static void Play(RectTransform rt, float delay, float seconds, float power)
        {
            if (rt == null || !Application.isPlaying) return;
            var s = rt.gameObject.AddComponent<Shake>();
            s._rt = rt; s._delay = delay; s._seconds = seconds; s._power = power;
        }

        private void Update()
        {
            if (_rt == null) return;
            if (_delay > 0f) { _delay -= Time.unscaledDeltaTime; if (_delay <= 0f) _base = _rt.anchoredPosition; return; }
            _t += Time.unscaledDeltaTime;
            float k = 1f - _t / _seconds;
            if (k <= 0f) { _rt.anchoredPosition = _base; Destroy(this); return; }
            _rt.anchoredPosition = _base + new Vector2(Mathf.Sin(_t * 70f) * _power * k, 0);
        }
    }

    /// <summary>가장자리만 물드는 효과(가운데는 그대로). SetLevel(0~1)로 세기를 정하면 부드럽게 따라가며 숨 쉰다.</summary>
    public sealed class UrgencyEdges
    {
        private const int Layers = 6;
        private const float LayerWidth = 7f;
        private readonly List<Image> _images = new List<Image>();
        private readonly RectTransform _root;
        private readonly Color _color;
        private readonly float _maxAlpha, _breathSpeed;
        private float _level, _target;

        public UrgencyEdges(RectTransform screen) : this(screen, new Color(0.86f, 0.12f, 0.12f), 0.16f, 1.2f) { }

        /// <param name="breathSpeed">초당 깜박임 횟수 비슷한 값. 0이면 깜박이지 않음</param>
        public UrgencyEdges(RectTransform screen, Color color, float maxAlpha, float breathSpeed)
        {
            _color = color; _maxAlpha = maxAlpha; _breathSpeed = breathSpeed;
            _root = UiKit.Rect(screen, "UrgencyEdges");
            UiKit.Stretch(_root);
            for (int layer = 0; layer < Layers; layer++)
                foreach (bool horizontal in new[] { true, false })
                    foreach (int side in new[] { 0, 1 })
                    {
                        var img = UiKit.Panel(_root, "Edge" + layer, Color.clear);
                        img.raycastTarget = false;
                        var rt = img.rectTransform;
                        float inset = layer * LayerWidth;
                        rt.anchorMin = horizontal ? new Vector2(0, side) : new Vector2(side, 0);
                        rt.anchorMax = horizontal ? new Vector2(1, side) : new Vector2(side, 1);
                        rt.pivot = horizontal ? new Vector2(.5f, side) : new Vector2(side, .5f);
                        // 가로 띠는 좌우 모서리를 세로 띠와 겹치지 않게 안쪽으로
                        rt.sizeDelta = horizontal ? new Vector2(-2 * (inset + LayerWidth) , LayerWidth) : new Vector2(LayerWidth, -2 * inset);
                        rt.anchoredPosition = horizontal ? new Vector2(0, side == 0 ? inset : -inset) : new Vector2(side == 0 ? inset : -inset, 0);
                        _images.Add(img);
                    }
            _root.gameObject.SetActive(false);
        }

        public RectTransform Root => _root;

        /// <summary>0이면 꺼짐, 1이면 가장 진함.</summary>
        public void SetLevel(float level) => _target = Mathf.Clamp01(level);

        /// <summary>천천히 따라가지 않고 바로 이 세기로(번쩍임 연출용).</summary>
        public void SetLevelNow(float level) { _target = _level = Mathf.Clamp01(level); }

        public void Update()
        {
            _level = Mathf.MoveTowards(_level, _target, Time.unscaledDeltaTime * 1.5f);
            bool on = _level > 0.001f;
            if (_root.gameObject.activeSelf != on) _root.gameObject.SetActive(on);
            if (!on) return;
            float breath = _breathSpeed <= 0f ? 1f : 0.75f + 0.25f * Mathf.Sin(Time.unscaledTime * Mathf.PI * _breathSpeed);
            for (int i = 0; i < _images.Count; i++)
            {
                int layer = i / 4;
                float falloff = 1f - layer / (float)Layers;            // 바깥이 진하고 안쪽으로 옅어짐
                _images[i].color = new Color(_color.r, _color.g, _color.b, _maxAlpha * falloff * falloff * _level * breath);
            }
        }
    }

    /// <summary>가장자리만 한 번 번쩍였다 사라지는 효과.</summary>
    public sealed class EdgeFlash : MonoBehaviour
    {
        private UrgencyEdges _edges;
        private float _t, _seconds;

        public static void Play(RectTransform parent, Color color, float seconds)
        {
            if (parent == null || !Application.isPlaying) return;
            var edges = new UrgencyEdges(parent, color, 0.45f, 0f);
            var driver = edges.Root.gameObject.AddComponent<EdgeFlash>();
            driver._edges = edges; driver._seconds = seconds;
            edges.SetLevelNow(1f);
            edges.Update();
        }

        private void Update()
        {
            if (_edges == null) return;
            _t += Time.unscaledDeltaTime;
            _edges.SetLevelNow(1f - _t / _seconds);
            _edges.Update();
            if (_t >= _seconds) Destroy(gameObject);
        }
    }
}
