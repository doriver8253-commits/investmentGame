// [실험] 교환(견제) 턴 연출. 값은 서버 결과 그대로이고, 보이는 효과만 담당한다.
// - Intro: 교환 턴이 시작되면 화면 전체를 덮는 카드로 '이번 턴 당신은 P4의 계좌를 운용합니다'를 크게 보여 준다.
//          클릭하거나 7초가 지나면 닫힌다.
// - Ceremony: 교환 턴 정산 때 '견제왕 발표'. 누가 누구를 몇 % 떨어뜨렸는지 한 줄씩(덜 떨어뜨린 사람부터) 공개하고,
//             마지막에 1등(견제왕)과 보너스 수익률을 발표한다. 클릭하거나 끝나고 6초 뒤 닫힌다.
using System.Linq;
using BlackWhale.Shared;
using BlackWhale.UI.Presentation;
using UnityEngine;
using UnityEngine.UI;

namespace BlackWhale.UI.View
{
    public static class SwapFx
    {
        private static readonly Color Red = new Color32(0xFF, 0x6B, 0x6B, 0xFF);
        private const string RedHex = "#FF6B6B";

        private static RectTransform Overlay(RectTransform screen, string name, float autoClose)
        {
            var old = screen.Find(name);
            if (old != null) Object.Destroy(old.gameObject);
            var dim = UiKit.Panel(screen, name, new Color(0, 0, 0, 0.92f), raycast: true);
            UiKit.Stretch(dim.rectTransform);
            dim.transform.SetAsLastSibling();
            var root = dim.rectTransform;
            UiKit.Click(dim.gameObject, () => { if (root != null) Object.Destroy(root.gameObject); });
            if (Application.isPlaying) Object.Destroy(root.gameObject, autoClose);
            return root;
        }

        private static Text Line(RectTransform parent, string text, int size, float y, float height, FontStyle style = FontStyle.Normal)
        {
            var box = UiKit.Rect(parent, "Line");
            UiKit.Place(box, new Vector2(.5f, .5f), new Vector2(.5f, .5f), new Vector2(0, y), new Vector2(1100, height));
            var label = UiKit.Label(box, text, size, Color.white, TextAnchor.MiddleCenter, style);
            return label;
        }

        public static void Intro(RectTransform screen, string target, string hunter, int bonusPercent)
        {
            if (screen == null) return;
            var root = Overlay(screen, "SwapIntro", (float)SwapRules.IntroSeconds);
            var top = UiKit.Panel(root, "TopLine", Red); UiKit.Place(top.rectTransform, new Vector2(.5f, .5f), new Vector2(.5f, .5f), new Vector2(0, 175), new Vector2(760, 3));
            var bottom = UiKit.Panel(root, "BottomLine", Red); UiKit.Place(bottom.rectTransform, new Vector2(.5f, .5f), new Vector2(.5f, .5f), new Vector2(0, -175), new Vector2(760, 3));
            var title = Line(root, "<color=" + RedHex + ">교환 턴</color>", 72, 115, 100, FontStyle.Bold);
            var main = Line(root, "이번 턴, 당신은 <color=" + RedHex + "><b>" + target + "</b></color>의 계좌를 운용합니다", 34, 25, 60);
            var goal = Line(root, "목표: <b>" + target + "의 돈을 최대한 떨어뜨리기</b>", 30, -35, 50);
            var rule = Line(root, "가장 많이 떨어뜨린 1명(견제왕)이 그 하락률의 " + bonusPercent + "%를 수익률로 받습니다", 20, -90, 36);
            var mine = Line(root, "<color=#9A9A9A>내 계좌는 " + hunter + "가 운용합니다 · 화면을 누르면 시작</color>", 18, -135, 30);
            UiAnim.Pulse(title.rectTransform.parent as RectTransform, 0f, 1.15f);
            UiAnim.Rise(main.rectTransform.parent as RectTransform, 0.3f);
            UiAnim.Rise(goal.rectTransform.parent as RectTransform, 0.8f);
            UiAnim.Rise(rule.rectTransform.parent as RectTransform, 1.3f);
            UiAnim.Rise(mine.rectTransform.parent as RectTransform, 1.6f);
            UiSound.Play(UiSound.Kind.Turn);
        }

        public static void Ceremony(RectTransform screen, ScoreRow[] rows, string me)
        {
            if (screen == null || rows == null) return;
            var swaps = rows.Where(r => r != null && !string.IsNullOrEmpty(r.swapAccountId))
                .OrderByDescending(r => r.swapAccountReturnBps).ThenBy(r => r.playerId).ToArray(); // 덜 떨어뜨린 사람부터
            if (swaps.Length == 0) return;
            const float step = 1.3f;
            float end = 1.2f + swaps.Length * step + 1.0f;
            var root = Overlay(screen, "SwapCeremony", end + 7f);
            var title = Line(root, "<color=" + RedHex + ">견제왕 발표</color>", 60, 230, 90, FontStyle.Bold);
            UiAnim.Pulse(title.rectTransform.parent as RectTransform, 0f, 1.12f);
            Line(root, "<color=#9A9A9A>누가 누구를 얼마나 떨어뜨렸을까?</color>", 20, 170, 34);
            System.Func<string, string> name = id => id == me ? id + "(나)" : id;
            for (int i = 0; i < swaps.Length; i++)
            {
                var r = swaps[i];
                bool aboutMe = r.swapAccountId == me, byMe = r.playerId == me;
                string pct = DisplayFormat.Percent(r.swapAccountReturnBps);
                string text = aboutMe ? "<b>" + r.playerId + "</b>가 <b>나</b>를 <color=" + RedHex + "><b>" + pct + "</b></color> 떨어뜨렸다"
                    : "<b>" + name(r.playerId) + "</b> → <b>" + name(r.swapAccountId) + "</b> 계좌  <color=" + RedHex + "><b>" + pct + "</b></color>";
                if (r.swapAccountReturnBps >= 0) text = "<b>" + name(r.playerId) + "</b> → <b>" + name(r.swapAccountId) + "</b> 계좌  <color=#9A9A9A>" + pct + " (못 떨어뜨림)</color>";
                var line = Line(root, text, aboutMe || byMe ? 30 : 26, 100 - i * 52, 48);
                float delay = 1.2f + i * step;
                UiAnim.Rise(line.rectTransform.parent as RectTransform, delay);
                DelayedAction.Run(root, delay, () => UiSound.Play(UiSound.Kind.Sell));
            }
            var winners = swaps.Where(r => r.swapBonus > 0).ToArray();
            string crown = winners.Length == 0 ? "아무도 떨어뜨리지 못해 견제왕이 없습니다"
                : "견제왕  <color=" + RedHex + ">" + string.Join(", ", winners.Select(w => name(w.playerId))) + "</color>";
            string bonus = winners.Length == 0 ? "" : string.Join("   ", winners.Select(w => name(w.playerId) + " 보너스 수익률 <b>+"
                + DisplayFormat.Percent(-w.swapAccountReturnBps * SwapRules.BonusPercent / 100, false) + "</b>  (+" + DisplayFormat.Money(w.swapBonus) + ")"));
            var crownLine = Line(root, crown, 46, -130, 70, FontStyle.Bold);
            var bonusLine = Line(root, bonus, 24, -190, 40);
            float last = 1.2f + swaps.Length * step + 0.4f;
            UiAnim.Rise(crownLine.rectTransform.parent as RectTransform, last, 60f, 0.7f);
            UiAnim.Rise(bonusLine.rectTransform.parent as RectTransform, last + 0.5f);
            DelayedAction.Run(root, last, () => { UiSound.Play(UiSound.Kind.Coin); UiSound.Play(UiSound.Kind.Turn); });
            DelayedAction.Run(root, last + 0.2f, () => { if (crownLine != null) UiAnim.Pulse(crownLine.rectTransform.parent as RectTransform, 0f, 1.18f); });
        }
    }
}
