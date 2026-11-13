// NEXT-D06 고유능력 선택 화면(게임 시작 전) + 내 능력 상태 카드.
// - 서버가 준 무작위 후보 3개 중 1개를 고른다(카드 클릭 = 로컬 후보, '능력 확정' = 의도 전달).
// - 시간 안에 고르지 않으면 서버가 첫 후보를 자동 선택한다는 안내를 항상 보인다.
using System;
using System.Linq;
using BlackWhale.UI.Presentation;
using UnityEngine;
using UnityEngine.UI;

namespace BlackWhale.UI.View
{
    public sealed class AbilityPickScreen
    {
        private static readonly Color Violet = new Color32(0x7C, 0x3A, 0xED, 0xFF);
        private static readonly Color VioletSoft = new Color32(0xED, 0xE9, 0xFE, 0xFF);

        private readonly RectTransform _root;
        private Text _clock;
        private bool _timeLimited;

        public Action<string> Picked;
        public Action Confirm;

        public AbilityPickScreen(RectTransform root) => _root = root;

        public void Show(AbilityPickModel m, AbilityStatusModel status = null)
        {
            UiKit.Clear(_root);
            if (m == null) return;

            ScreenKit.Title(_root, "<b>고유능력 선택</b>   <size=14><color=#6B7280>후보 3개 중 1개 · 게임 내내 유지</color></size>");
            _clock = null;
            _timeLimited = m.TimeLimited;
            if (!m.Confirmed)
                _clock = ScreenKit.Chip(_root, m.TimeLimited ? "남은 시간 " + DisplayFormat.Clock(m.SecondsLeft) : "시간 제한 없음", ScreenKit.Ink, Color.white,
                    new Vector2(1, 1), new Vector2(1, 1), new Vector2(-4, -4), 130, 28, 14).GetComponentInChildren<Text>();

            var cards = UiKit.Rect(_root, "Candidates");
            UiKit.Stretch(cards, 0, 48, 0, status != null ? 150 : 72);
            // 카탈로그에 없는 후보는 빼고, 남은 카드끼리 빈칸 없이 붙여서 나눈다
            var defs = m.Candidates.Select(AbilityCatalog.Get).Where(d => d != null).ToArray();
            int n = Mathf.Max(1, defs.Length);
            for (int i = 0; i < defs.Length; i++)
            {
                var def = defs[i];
                bool selected = m.Selected == def.Id;
                bool dimmed = m.Confirmed && !selected;

                var cell = UiKit.Rect(cards, "Ability_" + def.Id);
                UiKit.Anchor(cell, i / (float)n, 0, (i + 1) / (float)n, 1, 8, 0);
                var frame = ScreenKit.Card(cell, "Frame", selected ? (m.Confirmed ? Palette.Accent : ScreenKit.Amber) : Palette.CardBorder,
                    dimmed ? Palette.Disabled : Palette.Card, out var inner, selected ? 3f : 1f);
                UiKit.Stretch(frame);

                var hit = UiKit.Button(inner, "", 12, Color.clear, Palette.Text, () => Picked?.Invoke(def.Id));
                UiKit.Stretch((RectTransform)hit.transform);
                hit.name = "ChooseAbility_" + LiveScreenModels.ServerAbilityId(def.Id);
                hit.interactable = !m.Confirmed && !m.Pending;
                var colors = hit.colors;
                colors.disabledColor = Color.clear;
                colors.highlightedColor = new Color(0.96f, 0.96f, 0.98f, 1f);
                hit.colors = colors;

                var emblem = UiKit.Panel(inner, "Emblem", dimmed ? Palette.DisabledText : Violet, rounded: true);
                UiKit.Place(emblem.rectTransform, new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(0, -26), new Vector2(84, 84));
                var em = UiKit.Label(emblem.transform, def.Name.Substring(0, 1), 40, Color.white, TextAnchor.MiddleCenter, FontStyle.Bold);
                UiKit.Stretch(em.rectTransform);

                string state = m.Confirmed ? (selected ? "<color=#2563EB><b>확정</b></color>" : "")
                    : selected ? "<color=#B45309><b>선택됨</b></color>"
                    : i == 0 && m.TimeLimited ? "<size=11><color=#6B7280>시간 초과 시 자동 선택</color></size>" : "";
                var text = UiKit.Label(inner,
                    "<size=24><b>" + def.Name + "</b></size>\n<size=14><color=#7C3AED>" + def.Short + "</color></size>\n\n" + def.Effect + "\n\n" + state,
                    14, dimmed ? Palette.DisabledText : Palette.Text, TextAnchor.UpperCenter);
                UiKit.Stretch(text.rectTransform, 18, 124, 18, 12);

                // 후보가 처음 뜰 때 한 장씩 뒤집히며 공개(확정 화면에서는 생략)
                if (!m.Confirmed) UiAnim.FlipIn(frame, "ability:" + string.Join(",", m.Candidates) + ":" + i, 0.15f + i * 0.22f);
            }

            if (!m.Confirmed)
            {
                var confirm = UiKit.Button(_root, "능력 확정", 16, Palette.Accent, Color.white, () => Confirm?.Invoke());
                confirm.name = "ConfirmAbility";
                UiKit.Place((RectTransform)confirm.transform, new Vector2(1, 0), new Vector2(1, 0), new Vector2(-8, 10), new Vector2(220, 46));
                bool can = !m.Pending && !string.IsNullOrEmpty(m.Selected) && m.Candidates.Contains(m.Selected);
                UiKit.SetButton(confirm, m.Pending ? "확정 요청 중…" : "능력 확정", can, can ? Palette.Accent : Palette.Disabled, can ? Color.white : Palette.DisabledText);

                var hint = UiKit.Label(_root, can ? "확정하면 이번 게임 동안 바꿀 수 없습니다. 다른 카드를 누르면 후보가 바뀝니다."
                        : "카드를 눌러 후보를 고르세요. " + (m.TimeLimited ? "시간 초과 시 첫 후보가 자동 선택됩니다." : "전원 선택 또는 방장의 진행을 기다립니다."),
                    13, Palette.SubText, TextAnchor.MiddleLeft);
                UiKit.Place(hint.rectTransform, new Vector2(0, 0), new Vector2(0, 0), new Vector2(8, 18), new Vector2(760, 30));
            }

            if (!string.IsNullOrEmpty(m.Notice))
            {
                var notice = UiKit.Label(_root, m.Notice, 11, Palette.SubText, TextAnchor.LowerLeft);
                UiKit.Place(notice.rectTransform, Vector2.zero, Vector2.zero, new Vector2(8, 0), new Vector2(960, 20));
            }
            if (status != null) BuildStatus(status);
        }

        public void UpdateSeconds(int seconds)
        { if (_clock != null && _timeLimited) _clock.text = "남은 시간 " + DisplayFormat.Clock(seconds); }

        /// <summary>게임 중 내 능력 상태(정치인 재사용 턴 등). 서버 상태를 그대로 문구로 보인다.</summary>
        private void BuildStatus(AbilityStatusModel s)
        {
            var def = AbilityCatalog.Get(s.AbilityId);
            var area = UiKit.Rect(_root, "Status");
            area.anchorMin = new Vector2(0, 0);
            area.anchorMax = new Vector2(1, 0);
            area.pivot = new Vector2(0.5f, 0);
            area.offsetMin = new Vector2(8, 4);
            area.offsetMax = new Vector2(-8, 138);
            var frame = ScreenKit.Card(area, "StatusCard", Violet, VioletSoft, out var inner, 2f);
            UiKit.Stretch(frame);

            var emblem = UiKit.Panel(inner, "Emblem", Violet, rounded: true);
            UiKit.Place(emblem.rectTransform, new Vector2(0, 0.5f), new Vector2(0, 0.5f), new Vector2(16, 0), new Vector2(70, 70));
            var em = UiKit.Label(emblem.transform, def == null ? "?" : def.Name.Substring(0, 1), 32, Color.white, TextAnchor.MiddleCenter, FontStyle.Bold);
            UiKit.Stretch(em.rectTransform);

            var text = UiKit.Label(inner,
                "<size=12><color=#6B7280>내 고유능력 (게임 중 상태 카드)</color></size>\n<size=20><b>" + (def?.Name ?? s.AbilityId) + "</b></size>   "
                + "<color=#7C3AED><b>" + s.State + "</b></color>\n" + s.Detail + "\n<size=12><color=#6B7280>" + (def?.Effect ?? "") + "</color></size>",
                14, Palette.Text, TextAnchor.MiddleLeft);
            UiKit.Stretch(text.rectTransform, 100, 6, 16, 6);
        }
    }
}
