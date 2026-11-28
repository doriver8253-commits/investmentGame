// 태블릿형 게임 화면(REQ-018). 배치는 팀 스케치 기준:
//  - 위 가장자리: 없음(아이템이 기획에서 빠져 상점 열기를 제거)
//  - 왼쪽 가장자리: 매수 창으로 넘김 / 오른쪽 가장자리: 정보 구매 창으로 넘김
//  - 아래 가장자리: 없음(아이템이 기획에서 빠져 보유 아이템 창 열기를 제거)
//  - 상단 중앙: 전광판 주요 알림(5초 표시 후 사라짐)
//  - 왼쪽 열: 1~4등 칸. 마우스를 올리면 그 플레이어의 이번 턴 수익률·누적 손익·추이(주변 암전)
//  - 상단 오른쪽: 남은 시간 / 오른쪽: 완료 버튼 / 오른쪽 아래: 내 능력 상태 카드
// 화면은 저장소(ClientStateStore) 값만 표시하고, 버튼은 GameClientController로 의도만 보낸다.
using System.Collections.Generic;
using System.Linq;
using BlackWhale.Shared;
using BlackWhale.UI.Presentation;
using BlackWhale.UI.State;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace BlackWhale.UI.View
{
    public sealed class TabletView : MonoBehaviour
    {
        private const float EdgeSize = 34f;
        private const float BannerSeconds = 5f;

        private GameClientController _c;
        private LobbyActions _lobby;
        private ProgressionPanel _features;
        private Text _abilityLabel, _abilityTitle;
        private ClientStateStore Store => _c.Store;

        public RectTransform Root { get; private set; }
        public RectTransform ScreenRect { get; private set; }

        // 페이지
        private RectTransform _pager;
        private RectTransform _pageViewport, _rightColumn, _timerBox;
        private float _pageT, _pageTarget;
        private InvestmentPage _investmentPage;
        private InfoPurchasePage _infoPage;
        private Text _pageHint;

        // 전광판
        private CanvasGroup _bannerGroup;
        private Text _bannerText;
        private Image _bannerLine, _turnNoticeLine;
        private RectTransform _augmentList;
        private string _augmentKey;

        /// <summary>글자 폭만큼의 밑줄을 영역 아래 가운데에 맞춘다.</summary>
        private static void FitUnderline(Image line, Text text)
        {
            if (line == null || text == null) return;
            var rt = line.rectTransform;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0); rt.pivot = new Vector2(0.5f, 0);
            float max = ((RectTransform)line.transform.parent).rect.width - 24f;
            float w = text.preferredWidth;
            rt.sizeDelta = new Vector2(Mathf.Clamp(w, 8f, max > 8f ? max : w), 1.5f);
            rt.anchoredPosition = new Vector2(0, 2f);
        }
        private readonly Queue<string> _bannerQueue = new Queue<string>();
        // [실험] 교환 턴 안내 띠: 일반 전광판이 비어 있을 때 같은 자리에 항상 보인다.
        private CanvasGroup _swapGroup;
        private Text _swapText;
        private Image _swapLine;
        private const string SwapColor = "#FF6B6B";
        private float _bannerLeft;

        // 순위
        private readonly List<RankSlot> _slots = new List<RankSlot>();
        private Text _turnLabel;

        // 타이머
        private readonly PhaseClock _clock = new PhaseClock();
        private UrgencyEdges _urgency;
        private readonly Dictionary<string, long> _fxAssets = new Dictionary<string, long>(); // 정산 연출용 직전 총자산
        private readonly Dictionary<string, int> _fxRanks = new Dictionary<string, int>();    // 역전 판단용 직전 순위
        private string _clockKey = "";
        private PublicState _renderedPublic;
        private PlayerView _renderedView;
        private int _viewRefreshTurn;
        private float _missingViewSince = -1;
        private Text _timerText;
        private RectTransform _turnHighlight, _turnNotice;
        private Text _turnNoticeText;
        private Image _turnNoticeBackground;
        private string _lastPublicSelector = "";
        private Button _doneButton;
        private Button _developerTimer, _developerPrevious, _developerReset;
        private Button _pauseButton;
        private RectTransform _pauseOverlay;

        // 오버레이
        private Image _dim;
        private RectTransform _popup;

        // 알림 감지용 이전 값
        private string _lastPhaseKey = "";
        private int? _lastDay;
        private int _lastCoins = -1;
        private int _lastActiveShares = -1;
        private bool _lastLocked;
        private int _lastBoardTurn = -1;
        private string _lastSelector = "";

        public static TabletView Create(RectTransform parent, GameClientController controller, LobbyActions lobby = null)
        {
            var root = UiKit.Rect(parent, "TabletView");
            UiKit.Stretch(root);
            var view = root.gameObject.AddComponent<TabletView>();
            view._lobby = lobby;
            view.Init(root, controller);
            return view;
        }

        private void Init(RectTransform root, GameClientController controller)
        {
            _c = controller;
            Root = root;
            Build();
            Store.Changed += Refresh;
            Store.ErrorReceived += OnError;
            Store.AutomaticActionReceived += OnAutomatic;
            Refresh();
        }

        private void OnDestroy()
        {
            if (_c == null) return;
            Store.Changed -= Refresh;
            Store.ErrorReceived -= OnError;
            Store.AutomaticActionReceived -= OnAutomatic;
        }

        // ------------------------------------------------------------------
        // 구성
        // ------------------------------------------------------------------

        private void Build()
        {
            var bg = UiKit.Panel(Root, "Background", Palette.Background);
            UiKit.Stretch(bg.rectTransform);

            BuildEdges();

            var bezel = UiKit.Panel(Root, "Bezel", Palette.Bezel, rounded: true);
            UiKit.Stretch(bezel.rectTransform, EdgeSize + 8, EdgeSize + 4, EdgeSize + 8, EdgeSize + 4);
            var screen = UiKit.Panel(bezel.transform, "Screen", Palette.Screen, rounded: true);
            UiKit.Stretch(screen.rectTransform, 12, 12, 12, 12);
            ScreenRect = screen.rectTransform;

            BuildRankColumn();
            BuildPages();
            BuildRightColumn();
            BuildTimer();
            BuildBanner();
            BuildSwapRibbon();
            _urgency = null; // 주변 테두리 효과(마감 10초 전 붉은 가장자리)는 쓰지 않음
            BuildOverlays();
            BuildTurnIndicator();
            _features = ProgressionPanel.Create(Root, _c, _lobby);
            BuildPauseControls();
            if (_lobby?.Leave != null)
            {
                var leave = UiKit.Button(Root, "방 나가기", 12, Palette.Card, Palette.Text, _lobby.Leave);
                leave.name = "LeaveRoom";
                UiKit.Place((RectTransform)leave.transform, Vector2.zero, Vector2.zero, new Vector2(42, 2), new Vector2(100, 30));
            }
            // 결과 발표·정산 카드·역전 연출 전용 맨 위 레이어.
            // 결과 단계에서는 '전광판' 창(ProgressionPanel)이 자동으로 열려 화면 전체를 덮기 때문에, 연출을 화면(Screen) 안에 그리면 그 창 뒤에 가려진다.
            _fxLayer = UiKit.Rect(Root, "ResultFxLayer");
            UiKit.Stretch(_fxLayer, EdgeSize + 20, EdgeSize + 16, EdgeSize + 20, EdgeSize + 16);
        }

        private RectTransform _fxLayer;
        /// <summary>연출을 그릴 곳: 항상 모든 창보다 위(맨 마지막 자식).</summary>
        private RectTransform FxLayer
        {
            get
            {
                if (_fxLayer == null) return ScreenRect;
                _fxLayer.SetAsLastSibling();
                return _fxLayer;
            }
        }

        private void BuildEdges()
        {
            Edge("Edge_Left", 0, 0, 0, 1, new Vector2(0, EdgeSize), new Vector2(EdgeSize, -EdgeSize), "매수 창으로", () => ShowPage(0));
            Edge("Edge_Right", 1, 0, 1, 1, new Vector2(-EdgeSize, EdgeSize), new Vector2(0, -EdgeSize), "정보 구매 창으로", () => ShowPage(1));
        }

        private void BuildPauseControls()
        {
            _pauseOverlay = UiKit.Panel(Root, "GamePausedOverlay", new Color(0f, 0f, 0f, .85f), raycast: true).rectTransform;
            UiKit.Stretch(_pauseOverlay, 42, 40, 42, 40);
            var box = UiKit.Panel(_pauseOverlay, "PauseMessage", Palette.Card, rounded: true).rectTransform;
            UiKit.Place(box, new Vector2(.5f, .5f), new Vector2(.5f, .5f), Vector2.zero, new Vector2(480, 180));
            var message = UiKit.Label(box, "<b>게임 일시정지</b>\n\n모든 참가자의 타이머와 게임 진행이 멈췄습니다.\n방장이 재개하면 이어서 진행합니다.", 17, Palette.Text);
            UiKit.Stretch(message.rectTransform, 16, 12, 16, 60);
            if (_c.IsHost && _lobby?.SetPaused != null)
            {
                var resume = UiKit.Button(box, "게임 재개", 15, Palette.Accent, Color.white, () => _lobby.SetPaused(false));
                resume.name = "ResumeGame";
                UiKit.Place((RectTransform)resume.transform, new Vector2(.5f, 0), new Vector2(.5f, 0), new Vector2(0, 14), new Vector2(160, 34));
                _pauseButton = UiKit.Button(Root, "일시정지", 12, Palette.Card, Palette.Text, () => _lobby.SetPaused(!Store.Public.paused));
                _pauseButton.name = "PauseGame";
                UiKit.Place((RectTransform)_pauseButton.transform, new Vector2(0, 1), new Vector2(0, 1), new Vector2(42, -3), new Vector2(100, 30));
            }
            _pauseOverlay.gameObject.SetActive(false);
        }

        private void Edge(string name, float aMinX, float aMinY, float aMaxX, float aMaxY,
            Vector2 offMin, Vector2 offMax, string purpose, System.Action onClick)
        {
            // 보이지 않는 클릭 영역. 색은 투명(알파 0)이지만 raycastTarget이 켜져 있어 클릭을 받는다.
            var img = UiKit.Panel(Root, name, Color.clear, raycast: true);
            var rt = img.rectTransform;
            rt.anchorMin = new Vector2(aMinX, aMinY);
            rt.anchorMax = new Vector2(aMaxX, aMaxY);
            rt.offsetMin = offMin;
            rt.offsetMax = offMax;
            UiKit.Click(img.gameObject, onClick);
        }

        private void BuildRankColumn()
        {
            var col = UiKit.Rect(ScreenRect, "RankColumn");
            col.anchorMin = new Vector2(0, 0);
            col.anchorMax = new Vector2(0, 1);
            col.pivot = new Vector2(0, 1);
            col.offsetMin = new Vector2(12, 12);
            col.offsetMax = new Vector2(100, -12);

            _turnLabel = UiKit.Label(col, "", 13, Palette.Text, TextAnchor.UpperCenter, FontStyle.Bold);
            UiKit.Place(_turnLabel.rectTransform, new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(0, -6), new Vector2(88, 50));

            for (int i = 0; i < 4; i++)
            {
                var slot = new RankSlot(col, i, this);
                _slots.Add(slot);
            }

            var sep = UiKit.Panel(ScreenRect, "Separator", Palette.CardBorder);
            sep.rectTransform.anchorMin = new Vector2(0, 0);
            sep.rectTransform.anchorMax = new Vector2(0, 1);
            sep.rectTransform.offsetMin = new Vector2(106, 16);
            sep.rectTransform.offsetMax = new Vector2(107, -16);
        }

        private void BuildPages()
        {
            var viewport = UiKit.Rect(ScreenRect, "PageViewport");
            _pageViewport = viewport;
            UiKit.Stretch(viewport, 116, 76, 168, 10);
            viewport.gameObject.AddComponent<RectMask2D>();

            _pager = UiKit.Rect(viewport, "Pager");
            _pager.anchorMin = new Vector2(0, 0);
            _pager.anchorMax = new Vector2(2, 1);
            _pager.offsetMin = Vector2.zero;
            _pager.offsetMax = Vector2.zero;

            var invest = UiKit.Rect(_pager, "Page_Investment");
            UiKit.Anchor(invest, 0, 0, 0.5f, 1, 4, 0);
            var info = UiKit.Rect(_pager, "Page_Info");
            UiKit.Anchor(info, 0.5f, 0, 1, 1, 4, 0);

            _investmentPage = new InvestmentPage(invest, _c, _lobby);
            // 기업 선택 차례의 서버 마감으로 남은 시간을 표시한다. 진행 판정은 서버.
            _investmentPage.RemainingSeconds = () => _clock.RemainingSeconds(Store.Public, Time.realtimeSinceStartup);
            _investmentPage.RemainingExact = () => _clock.RemainingExact(Store.Public, Time.realtimeSinceStartup);
            _infoPage = new InfoPurchasePage(info, _c, ScreenRect);
            // 진입·청산(매매) 화면에서 확대했으면, 축소할 때 다시 매매 화면으로 돌아간다.
            _investmentPage.OpenStockPlan = id => { _zoomFromTrade = true; ShowPage(1); _infoPage.OpenCompany(id); };
            _infoPage.Shrunk = () => { if (!_zoomFromTrade) return; _zoomFromTrade = false; ShowPage(0); };

            _pageHint = UiKit.Label(ScreenRect, "", 11, Palette.SubText, TextAnchor.LowerCenter);
            _pageHint.rectTransform.anchorMin = new Vector2(0, 0);
            _pageHint.rectTransform.anchorMax = new Vector2(1, 0);
            _pageHint.rectTransform.offsetMin = new Vector2(116, -2);
            _pageHint.rectTransform.offsetMax = new Vector2(-168, 12);
        }

        private void BuildRightColumn()
        {
            var col = UiKit.Rect(ScreenRect, "RightColumn");
            _rightColumn = col;
            col.anchorMin = new Vector2(1, 0);
            col.anchorMax = new Vector2(1, 1);
            col.pivot = new Vector2(1, 0.5f);
            col.offsetMin = new Vector2(-160, 12);
            col.offsetMax = new Vector2(-12, -76);

            _doneButton = UiKit.Button(col, "완료", 16, Palette.Accent, Color.white, OnDoneClicked);
            UiKit.Place((RectTransform)_doneButton.transform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0, -12), new Vector2(140, 44));
            _developerTimer = UiKit.Button(col, "현재 일차부터 타이머 적용", 11, Palette.AccentSoft, Palette.Accent, () => _c.DeveloperControl("DEV_TIMER"));
            _developerPrevious = UiKit.Button(col, "이전 단계", 13, Palette.AccentSoft, Palette.Accent, () => _c.DeveloperControl("DEV_PREVIOUS"));
            _developerReset = UiKit.Button(col, "현재 단계 초기화", 13, Palette.AccentSoft, Palette.Accent, () => _c.DeveloperControl("DEV_RESET"));
            _developerTimer.name = "DeveloperTimer"; _developerPrevious.name = "DeveloperPrevious"; _developerReset.name = "DeveloperReset";
            UiKit.Place((RectTransform)_developerTimer.transform, new Vector2(.5f, 1), new Vector2(.5f, 1), new Vector2(0, -100), new Vector2(146, 38));
            UiKit.Place((RectTransform)_developerPrevious.transform, new Vector2(.5f, 1), new Vector2(.5f, 1), new Vector2(0, -146), new Vector2(140, 36));
            UiKit.Place((RectTransform)_developerReset.transform, new Vector2(.5f, 1), new Vector2(.5f, 1), new Vector2(0, -190), new Vector2(140, 36));

            // 본인 직업 카드
            // 네모 칸 대신 흰 밑줄만(방송 화면 스타일). 투명 바탕은 클릭·마우스 판정용.
            var job = UiKit.Panel(col, "OwnJob", Color.clear, raycast: true);
            UiKit.Place(job.rectTransform, new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(0, 0), new Vector2(120, 120));
            var underline = UiKit.Panel(job.transform, "Underline", Color.white);
            underline.rectTransform.anchorMin = Vector2.zero; underline.rectTransform.anchorMax = new Vector2(1, 0);
            underline.rectTransform.offsetMin = new Vector2(6, 0); underline.rectTransform.offsetMax = new Vector2(-6, 2);
            // 왼쪽 세로줄을 더해 'ㄴ'자로 감싼다
            var leftLine = UiKit.Panel(job.transform, "LeftLine", Color.white);
            leftLine.rectTransform.anchorMin = Vector2.zero; leftLine.rectTransform.anchorMax = new Vector2(0, 0.5f); // 세로줄은 아래 절반만
            leftLine.rectTransform.offsetMin = new Vector2(6, 0); leftLine.rectTransform.offsetMax = new Vector2(8, 0);
            var inner = UiKit.Rect(job.transform, "Inner");
            UiKit.Stretch(inner, 0, 0, 0, 6);
            // 제목은 위에 고정, 목록은 아래부터 쌓인다(새 증강이 생기면 기존 것 위에 하나씩 추가).
            _abilityTitle = UiKit.Label(inner, "", 14, Palette.Text, TextAnchor.UpperCenter, FontStyle.Bold);
            UiKit.Stretch(_abilityTitle.rectTransform, 4, 4, 4, 4);
            _abilityLabel = UiKit.Label(inner, "", 13, Palette.Text, TextAnchor.LowerLeft);
            UiKit.Stretch(_abilityLabel.rectTransform, 10, 26, 6, 4);
            // 주식 모드 증강 목록: 새 증강이 맨 아래, 기존은 위로 밀림.
            _augmentList = UiKit.Rect(inner, "AugmentList");
            UiKit.Stretch(_augmentList, 12, 26, 6, 6);
            UiKit.Hover(job.gameObject, () => ShowAllPlayersPopup(job.rectTransform), HidePopup);
            UiKit.Click(job.gameObject, () => { HidePopup(); _features?.OpenAbility(); });
        }

        private void BuildTimer()
        {
            var timerBox = UiKit.Panel(ScreenRect, "Timer", Palette.Card, rounded: true);
            _timerBox = timerBox.rectTransform;
            // 가운데를 오른쪽 열(완료 버튼)의 가운데에 맞춘다(오른쪽 열 폭은 모드마다 달라서 갱신 때 다시 맞춤)
            UiKit.Place(_timerBox, Vector2.one, new Vector2(0.5f, 1f), new Vector2(-86, -8), new Vector2(148, 60));
            _timerText = UiKit.Label(timerBox.transform, "", 14, Palette.Text);
            UiKit.Stretch(_timerText.rectTransform, 6, 4, 6, 4);
        }

        private void BuildBanner()
        {
            var banner = UiKit.Panel(ScreenRect, "Banner", Color.clear); // 네모 없이 글자 + 밑줄
            var rt = banner.rectTransform;
            rt.anchorMin = new Vector2(0, 1);
            rt.anchorMax = new Vector2(1, 1);
            rt.pivot = new Vector2(0.5f, 1);
            rt.offsetMin = new Vector2(116, -64);
            rt.offsetMax = new Vector2(-168, -12);
            _bannerText = UiKit.Label(rt, "", 16, Palette.Text);
            UiKit.Stretch(_bannerText.rectTransform, 12, 4, 12, 8);
            _bannerLine = UiKit.Panel(rt, "Underline", Palette.Text);
            _bannerGroup = banner.gameObject.AddComponent<CanvasGroup>();
            _bannerGroup.alpha = 0f;
            _bannerGroup.blocksRaycasts = false;
        }

        private void BuildSwapRibbon()
        {
            var ribbon = UiKit.Panel(ScreenRect, "SwapRibbon", Color.clear);
            var rt = ribbon.rectTransform;
            rt.anchorMin = new Vector2(0, 1); rt.anchorMax = new Vector2(1, 1); rt.pivot = new Vector2(0.5f, 1);
            rt.offsetMin = new Vector2(116, -66); rt.offsetMax = new Vector2(-168, -8);
            _swapText = UiKit.Label(rt, "", 20, Palette.Text, TextAnchor.MiddleCenter);
            _swapText.name = "SwapRibbonText";
            UiKit.Stretch(_swapText.rectTransform, 12, 4, 12, 8);
            _swapLine = UiKit.Panel(rt, "Underline", new Color32(0xFF, 0x6B, 0x6B, 0xFF));
            _swapGroup = ribbon.gameObject.AddComponent<CanvasGroup>();
            _swapGroup.alpha = 0f; _swapGroup.blocksRaycasts = false;
        }

        /// <summary>
        /// 화면 위쪽 안내 띠(전광판 알림이 없을 때만 보임).
        /// - 교환 턴: 누구 계좌를 조종 중인지, 내 계좌는 누가 조종 중인지
        /// - 평소 턴: 이번 턴 미션
        /// 둘째 줄에는 가장 최근 뉴스(루머·속보)가 있으면 그것을 보여 준다.
        /// </summary>
        private void RefreshSwapRibbon()
        {
            var s = Store.Public;
            bool live = s != null && s.stockSystem && (s.phase == Protocol.Info || s.phase == Protocol.Investment || s.phase == Protocol.Reveal);
            if (!live || !s.swapTurn && s.mission == null && (s.news == null || s.news.Length == 0)) { _swapText.text = ""; return; }
            var latest = s.news != null && s.news.Length > 0 ? s.news[s.news.Length - 1] : null;
            string newsLine = latest == null ? null : "<size=14>" + NewsColored(latest) + "</size>";
            string text;
            if (s.swapTurn)
            {
                string me = _c.LocalPlayerId, target = SwapRules.ActingAccount(s, me), hunter = SwapRules.ControllerOf(s, me);
                text = "<color=" + SwapColor + "><b>교환 턴</b></color>  지금 <color=" + SwapColor + "><b>" + target + "</b></color>의 계좌를 운용 중 · 목표: <b>" + target + "를 최대한 떨어뜨리기</b>"
                    + "\n" + (newsLine ?? "<size=14><color=#BBBBBB>견제왕(가장 많이 떨어뜨린 1명)은 그 하락률의 " + s.swapBonusPercent + "%를 수익률로 받음 · 내 계좌는 " + hunter + "가 운용 중</color></size>");
            }
            else
            {
                string head = s.mission != null
                    ? "<color=" + MissionColor + "><b>이번 턴 미션</b></color>  " + s.mission.title + "  <size=14><color=#BBBBBB>(성공 시 +" + s.mission.rewardBps / 100 + "%)</color></size>"
                    : "<color=" + MissionColor + "><b>뉴스</b></color>";
                text = head + "\n" + (newsLine ?? "<size=14><color=#BBBBBB>" + s.mission.description + "</color></size>");
            }
            if (_swapText.text != text)
            {
                _swapText.text = text;
                _swapLine.color = s.swapTurn ? (Color)new Color32(0xFF, 0x6B, 0x6B, 0xFF) : (Color)new Color32(0xFF, 0xD1, 0x66, 0xFF);
                FitUnderline(_swapLine, _swapText);
            }
        }
        private const string MissionColor = "#FFD166";
        /// <summary>뉴스 한 줄. 루머는 노란색, 속보는 오르면 빨강·내리면 파랑(한국 증시 색).</summary>
        private static string NewsColored(MarketNews n)
        {
            if (n == null) return "";
            string color = n.kind == NewsRules.Rumor || n.kind == NewsRules.Warning ? "#FFD166" : n.direction > 0 ? "#FF6B6B" : "#6BA8FF";
            return "<color=" + color + "><b>" + n.text + "</b></color>";
        }

        private void BuildOverlays()
        {
            // 암전 + 팝업
            _dim = UiKit.Panel(ScreenRect, "Dim", Palette.Dim);
            UiKit.Stretch(_dim.rectTransform);
            _dim.gameObject.SetActive(false);

            _popup = UiKit.Rect(ScreenRect, "Popup");
            _popup.gameObject.SetActive(false);
        }

        private void BuildTurnIndicator()
        {
            _turnHighlight = UiKit.Rect(ScreenRect, "MyTurnHighlight"); UiKit.Stretch(_turnHighlight);
            foreach (bool horizontal in new[] { true, false })
                foreach (int side in new[] { 0, 1 })
                {
                    var edge = UiKit.Panel(_turnHighlight, "TurnEdge", Palette.Accent).rectTransform;
                    edge.anchorMin = horizontal ? new Vector2(0, side) : new Vector2(side, 0);
                    edge.anchorMax = horizontal ? new Vector2(1, side) : new Vector2(side, 1);
                    edge.pivot = horizontal ? new Vector2(.5f, side) : new Vector2(side, .5f);
                    edge.sizeDelta = horizontal ? new Vector2(0, 5) : new Vector2(5, 0);
                    edge.anchoredPosition = Vector2.zero;
                }
            _turnNoticeBackground = UiKit.Panel(ScreenRect, "PublicTurnNotice", Color.clear); // 네모 없이 글자 + 밑줄
            _turnNotice = _turnNoticeBackground.rectTransform;
            _turnNotice.anchorMin = new Vector2(0, 1); _turnNotice.anchorMax = Vector2.one;
            _turnNotice.offsetMin = new Vector2(116, -68); _turnNotice.offsetMax = new Vector2(-168, -8);
            _turnNoticeText = UiKit.Label(_turnNotice, "", 20, Palette.Text, TextAnchor.MiddleCenter, FontStyle.Bold);
            _turnNoticeText.name = "PublicTurnNoticeText";
            UiKit.Stretch(_turnNoticeText.rectTransform, 12, 4, 12, 8);
            _turnNoticeLine = UiKit.Panel(_turnNotice, "Underline", Palette.Text);
            _turnHighlight.gameObject.SetActive(false); _turnNotice.gameObject.SetActive(false);
        }

        private void RefreshTurnIndicator()
        {
            var state = Store.Public;
            bool active = state?.publicSelectionActive == true && !state.paused && string.IsNullOrEmpty(Store.EndReason);
            bool mine = active && state.publicSelectionPlayerId == _c.LocalPlayerId;
            _turnHighlight.gameObject.SetActive(false); _turnNotice.gameObject.SetActive(active); // 화면 테두리 강조는 쓰지 않음
            if (!active) return;
            _turnNoticeBackground.color = Color.clear;
            _turnNoticeText.color = mine ? Palette.Text : Palette.SubText;
            _turnNoticeText.text = mine
                ? "<size=23>지금 내 차례입니다!</size>  공용 정보 선택\n<size=13>공개할 기업을 선택하세요.</size>"
                : "<size=20>" + state.publicSelectionPlayerId + " 님의 공용 정보 선택 차례</size>\n<size=13>다음 차례까지 공개되는 정보를 확인하세요.</size>";
            FitUnderline(_turnNoticeLine, _turnNoticeText);
            _turnHighlight.SetAsLastSibling(); _turnNotice.SetAsLastSibling();
        }

        // ------------------------------------------------------------------
        // 갱신
        // ------------------------------------------------------------------

        public void Refresh()
        {
            var s = Store.Public;
            bool stock = s?.stockSystem == true && s.phase != Protocol.Lobby;
            bool stockDraft = stock && s.phase == Protocol.Selection;
            UiKit.Stretch(_pageViewport, 116, stock ? 70 : 76, stockDraft ? 12 : stock ? 136 : 168, 10);
            _rightColumn.gameObject.SetActive(!stockDraft);
            _pageHint.gameObject.SetActive(!stockDraft);
            _rightColumn.offsetMin = new Vector2(stock ? -140 : -160, 12);
            _rightColumn.offsetMax = new Vector2(stock ? -2 : -12, -76);
            ((RectTransform)_doneButton.transform).sizeDelta = new Vector2(stock ? 130 : 140, 44);
            // 타이머를 완료 버튼(오른쪽 열) 가운데에 맞춤
            if (_timerBox != null) _timerBox.anchoredPosition = new Vector2((_rightColumn.offsetMin.x + _rightColumn.offsetMax.x) * 0.5f, -8);
            // Sync before rebuilding clickable cards so a new turn uses its current deadline.
            if (s != null)
            {
                string key = s.turn + "|" + s.phase + "|" + s.selectionIndex + "|" + s.publicSelectionIndex + "|" + s.serverElapsedSeconds + "|" + s.paused;
                if (key != _clockKey)
                {
                    _clockKey = key;
                    _clock.Sync(s.serverElapsedSeconds, Time.realtimeSinceStartup);
                }
            }
            DetectNotifications();
            bool paused = s?.paused == true && string.IsNullOrEmpty(Store.EndReason) && s.phase != Protocol.Ended;
            _pauseOverlay.gameObject.SetActive(paused);
            if (_pauseButton != null)
            {
                bool can = s != null && s.phase != Protocol.Lobby && s.phase != Protocol.Ended && string.IsNullOrEmpty(Store.EndReason);
                UiKit.SetButton(_pauseButton, paused ? "게임 재개" : "일시정지", can, paused ? Palette.AccentSoft : Palette.Card, Palette.Text);
            }

            _turnLabel.text = s == null ? "" : s.turn + (s.stockSystem ? "/" + s.totalTurns : "") + "턴\n<size=11>" + PhaseNames.ForState(s) + "</size>";
            RefreshRanks();
            _investmentPage.Refresh();
            _infoPage.Refresh();
            RefreshTurnIndicator();
            RefreshSwapRibbon();
            var ability = LiveScreenModels.AbilityId(Store.View?.ability?.selectedId);
            _abilityTitle.text = s?.stockSystem == true ? (s.swapTurn ? SwapRules.ActingAccount(s, _c.LocalPlayerId) + " 증강" : "내 증강") : "내 고유능력";
            _abilityLabel.text = (AbilityCatalog.Get(ability)?.Name ?? "선택 대기") + "\n<size=11>클릭하여 상태 보기</size>";
            // 새로 얻은 증강이 맨 아래에 붙고, 기존 증강은 위로 밀려 올라간다. 왼쪽 정렬 + 앞에 '-'.
            if (s?.stockSystem == true) { _abilityLabel.text = ""; RefreshAugmentList(Store.View?.portfolio?.augments ?? new string[0]); }
            else if (_augmentList != null && _augmentList.childCount > 0) { UiKit.Clear(_augmentList); _augmentKey = null; }
            UpdateTimer();
            _renderedPublic = Store.Public; _renderedView = Store.View;
        }

        private void DetectNotifications()
        {
            var s = Store.Public;
            if (s != null)
            {
                string phaseKey = s.turn + "|" + s.phase + "|" + s.developerRevision;
                var day = _c.HasCurrentPlayerView ? StockRules.CurrentDay(s, Store.View?.portfolio) : null;
                bool contextChanged = phaseKey != _lastPhaseKey || day != _lastDay;
                // 위쪽 안내 띠(교환 턴·미션·뉴스)가 있는 턴에는 매일 뜨던 'N일차' 알림을 띄우지 않는다(일차는 매매 화면 위에 항상 표시됨).
                bool ribbonTurn = s.stockSystem && (s.swapTurn || s.mission != null || (s.news?.Length ?? 0) > 0);
                if (phaseKey != _lastPhaseKey || contextChanged && !ribbonTurn) ClearBanners();
                if (phaseKey != _lastPhaseKey)
                {
                    _lastCoins = -1;
                    _lastActiveShares = -1;
                    _lastSelector = "";
                    _lastPhaseKey = phaseKey;
                    if (s.phase == Protocol.Lobby) ShowBanner("대기실에 입장했습니다. 준비를 누르세요.");
                    else if (s.phase == Protocol.Ended && !string.IsNullOrEmpty(Store.EndReason)) ShowBanner(ErrorMessages.ToKorean(Store.EndReason));
                    else if (s.phase != Protocol.Ended && !day.HasValue && !(s.swapTurn && s.phase == Protocol.Info)) ShowBanner("<b>" + s.turn + "턴 · " + PhaseNames.ForState(s) + "</b> 단계가 시작되었습니다.");
                    if (s.phase != Protocol.Lobby && s.phase != Protocol.Ended) UiSound.Play(UiSound.Kind.Turn); // 새 단계 시작음
                    // [실험] 교환 턴 시작: 화면 전체 안내 카드(누르거나 7초 뒤 닫힘)
                    if (s.swapTurn && s.phase == Protocol.Info)
                        SwapFx.Intro(FxLayer, SwapRules.ActingAccount(s, _c.LocalPlayerId), SwapRules.ControllerOf(s, _c.LocalPlayerId), s.swapBonusPercent);
                    // 교환 턴 바로 다음 턴: 다시 내 계좌로 돌아왔다고 알려 준다.
                    if (!s.swapTurn && s.phase == Protocol.Info && s.swapTurns != null && s.swapTurns.Contains(s.turn - 1))
                        ShowBannerAlways("<color=" + SwapColor + "><b>교환 턴 끝!</b></color> 이번 턴부터 다시 <b>내 계좌</b>를 운용합니다.");
                    // 1턴: 실시간 시장의 기업 성격 안내(한 번)
                    if (s.phase == Protocol.Info && s.turn == 1 && s.mission != null)
                        ShowBannerAlways("<b>기업 성격</b>  AUTO 패턴대로 · FOOD 사람 많을수록 출렁 · <color=#FF6B6B>AI 몰리면 붕괴</color> · <color=#7CFFB2>BIO 쌓이면 급등</color> · SPACE 며칠씩 추세");
                    // 이번 턴 미션 안내
                    if (s.phase == Protocol.Info && s.mission != null)
                        ShowBannerAlways("<color=" + MissionColor + "><b>이번 턴 미션 · " + s.mission.title + "</b></color>  " + s.mission.description);
                    if (s.phase == Protocol.Info || s.phase == Protocol.Reveal) ShowPage(1);
                    if (s.phase == Protocol.Lobby || s.phase == Protocol.Selection || s.phase == StockRules.AugmentPhase || s.phase == Protocol.Investment || s.phase == Protocol.Result) ShowPage(0);
                }
                if (s.phase == Protocol.Selection && s.currentPlayerId != _lastSelector)
                {
                    ClearBanners();
                    _lastSelector = s.currentPlayerId;
                    if (s.currentPlayerId == _c.LocalPlayerId) UiSound.Play(UiSound.Kind.Turn); // 내 차례 알림음
                    if (s.currentPlayerId == _c.LocalPlayerId) ShowBanner("<color=#FFFFFF><b>내 차례입니다.</b></color> " + (s.stockSystem ? "주식 보유량을 정하세요." : "기업을 선택하세요."));
                }
                string publicSelector = s.publicSelectionActive ? s.turn + "|" + s.publicSelectionIndex + "|" + s.publicSelectionPlayerId : "";
                if (publicSelector != _lastPublicSelector)
                {
                    ClearBanners();
                    _lastPublicSelector = publicSelector;
                    if (s.publicSelectionActive)
                    {
                        if (s.publicSelectionPlayerId == _c.LocalPlayerId) ShowPage(1);
                    }
                }
                if (contextChanged && day.HasValue && !ribbonTurn) // 교환 턴·미션 턴에는 일차 알림 대신 위쪽 안내 띠를 계속 보여 준다
                    ShowBanner("<b>" + s.turn + "턴 · 현재 " + day.Value + "일차</b> 주가 공개");
                _lastDay = day;

                // 실시간 뉴스: 새로 뜬 루머·속보를 전광판에 크게 띄우고 효과음
                string newsKey = s.turn + "|" + s.developerRevision;
                int newsCount = s.news?.Length ?? 0;
                if (newsKey != _lastNewsKey) { _lastNewsKey = newsKey; _lastNewsCount = s.phase == Protocol.Investment ? 0 : newsCount; }
                if (newsCount > _lastNewsCount)
                {
                    ClearBanners(); // 쌓인 알림보다 뉴스가 먼저
                    for (int i = _lastNewsCount; i < newsCount; i++) ShowBannerAlways(NewsColored(s.news[i]));
                    UiSound.Play(s.news[newsCount - 1].kind == NewsRules.Breaking || s.news[newsCount - 1].kind == NewsRules.Event ? UiSound.Kind.Turn : UiSound.Kind.Coin);
                }
                _lastNewsCount = newsCount;
            }

            var v = Store.View;
            if (v != null)
            {
                // 코인으로 정보를 살 때마다 뜨던 알림은 너무 자주 나와서 띄우지 않는다(남은 코인은 정보 구매 창 위쪽에 항상 표시됨)
                // 효과음: 같은 단계 안에서 코인이 줄면(정보 구매) 짤랑, 보유 주식이 늘면 매수음·줄면 매도음
                if (_lastCoins >= 0 && v.coins < _lastCoins) UiSound.Play(UiSound.Kind.Coin);
                _lastCoins = v.coins;
                int activeShares = v.portfolio?.holdings?.Sum(h => h.activeShares) ?? 0;
                if (_lastActiveShares >= 0 && activeShares != _lastActiveShares)
                    UiSound.Play(activeShares > _lastActiveShares ? UiSound.Kind.Buy : UiSound.Kind.Sell);
                _lastActiveShares = activeShares;
            }

            if (Store.EndReason != _lastEndReason)
            {
                _lastEndReason = Store.EndReason;
                if (!string.IsNullOrEmpty(_lastEndReason)) { ClearBanners(); ShowBanner(Palette.BadText(ErrorMessages.ToKorean(_lastEndReason))); }
            }

            if (Store.OrderLocked && !_lastLocked && (s?.phase == Protocol.Investment || s?.phase == Protocol.Reveal)) ShowBanner(Palette.GoodText("주문이 확정되었습니다."));
            _lastLocked = Store.OrderLocked;

            var board = Store.Scoreboard;
            if (board != null && board.turn != _lastBoardTurn && board.rows != null && board.rows.Length > 0
                && board.turn == s?.turn && (s.phase == Protocol.Result || s.phase == Protocol.Ended))
            {
                _lastBoardTurn = board.turn;
                var top = board.rows.OrderBy(r => r.rank).First();
                var mine = board.rows.FirstOrDefault(r => r.playerId == _c.LocalPlayerId);
                // 차트(시장 공개)를 다 본 뒤 최종 결과가 나오는 순간: 매 턴 크게 '결과 발표' → 이어서 내 정산 카드·역전 표시
                var fx = FxLayer;
                // [실험] 교환 턴: 먼저 '견제왕 발표'를 보여 주고, 끝난 뒤 평소 결과 연출을 이어서 보여 준다.
                bool swapBoard = board.rows.Any(r => r != null && !string.IsNullOrEmpty(r.swapAccountId));
                float lead = swapBoard ? SwapCeremonySeconds(board) : 0f;
                if (swapBoard) SwapFx.Ceremony(fx, board.rows, _c.LocalPlayerId);
                int announceTurn = board.turn; bool announceFinal = board.gameComplete;
                DelayedAction.Run(fx, lead, () => ResultAnnouncement.Play(FxLayer, announceTurn, announceFinal));
                float afterAnnouncement = lead + 1.8f;
                if (mine != null)
                {
                    long before = _fxAssets.TryGetValue(mine.playerId, out var b) ? b : mine.totalAssets - mine.cumulativeProfit;
                    int turnNo = board.turn, rank = mine.rank, ret = mine.turnReturnBps; long now = mine.totalAssets; bool complete = board.gameComplete;
                    DelayedAction.Run(fx, afterAnnouncement, () => SettlementFx.Play(FxLayer, turnNo, complete, rank, ret, before, now));
                }
                // 역전: 직전 순위와 비교해 누가 누구를 제쳤는지
                var reversal = ReversalFx.Describe(_fxRanks, board.rows.Where(r => r != null).Select(r => new KeyValuePair<string, int>(r.playerId, r.rank)),
                    id => id == _c.LocalPlayerId ? id + "(나)" : id, out bool reversalMine, _c.LocalPlayerId);
                if (reversal != null) ReversalFx.Play(fx, reversal, reversalMine, afterAnnouncement + 0.8f);
                foreach (var row in board.rows) if (row != null) { _fxAssets[row.playerId] = row.totalAssets; _fxRanks[row.playerId] = row.rank; }
                ShowBanner(board.gameComplete ? (s.totalTurns > 0 ? s.totalTurns : 10) + "턴 최종 승자 · " + board.winnerPlayerId :
                    board.turn + "턴 정산 완료 · 1위 " + top.playerId + " (" + DisplayFormat.Percent(top.turnReturnBps) + ")");
                if (mine != null && mine.missionBonus > 0)
                    ShowBannerAlways("<color=" + MissionColor + "><b>미션 성공!</b></color> 보너스 +" + DisplayFormat.Money(mine.missionBonus));
                // 교환 턴 예고: 다음 턴이 교환 턴이면 미리 알려 준다(갑자기 바뀌는 느낌 줄이기)
                if (!board.gameComplete && s.swapTurns != null && s.swapTurns.Contains(board.turn + 1))
                    ShowBannerAlways("<color=" + SwapColor + "><b>다음 턴은 교환 턴!</b></color> 옆 사람 계좌를 대신 운용합니다. 그 계좌를 <b>떨어뜨릴수록</b> 유리해요.");
            }
        }

        private string _lastEndReason = "";
        private string _lastNewsKey = "";
        private int _lastNewsCount;

        private void OnAutomatic(string resultType)
        {
            if (resultType == CommandTypes.ResultOf(CommandTypes.SelectCompany) && Store.Public?.phase == Protocol.Selection)
                ShowBanner("<color=#D0D0D0>시간이 초과되어 기업이 자동 선택되었습니다.</color>");
            else if (resultType == CommandTypes.ResultOf(CommandTypes.SubmitInvestment) && (Store.Public?.phase == Protocol.Investment || Store.Public?.phase == Protocol.Reveal))
                ShowBanner("<color=#D0D0D0>투자 시간이 끝나 주문이 자동 확정되었습니다. 적용된 구간과 비중은 확정 주문에서 확인하세요.</color>");
        }

        private void OnError(ClientError error)
        {
            ShowBannerAlways("<color=#FFFFFF>" + ErrorMessages.ToKorean(error.ErrorCode) + "</color>");
        }

        // 순위 칸 애니메이션용: 직전에 보여 준 순서와 총자산
        private List<string> _shownOrder = new List<string>();
        private readonly Dictionary<string, long> _shownAssets = new Dictionary<string, long>();

        /// <summary>정산 결과로 순서가 바뀌면 각 칸이 이전 자리에서 새 자리로 미끄러지듯 이동하고,
        /// 총자산은 이전 값에서 새 값으로 세어 올라간다(표시 효과만, 값은 서버 결과 그대로).</summary>
        private void RefreshRanks()
        {
            var rows = RankRows();
            var order = rows.Select(r => r.PlayerId).ToList();
            bool reordered = _shownOrder.Count > 0 && rows.Any(r => r.Rank > 0) && !order.SequenceEqual(_shownOrder);
            for (int i = 0; i < _slots.Count; i++)
            {
                var row = i < rows.Count ? rows[i] : null;
                int from = reordered && row != null ? _shownOrder.IndexOf(row.PlayerId) : -1;
                long? prevAssets = null;
                if (row?.Score != null && _shownAssets.TryGetValue(row.PlayerId, out var before)) prevAssets = before;
                _slots[i].Set(row, _c.LocalPlayerId, from, prevAssets, i * 0.08f);
            }
            _shownOrder = order;
            foreach (var r in rows)
                if (r.Score != null) _shownAssets[r.PlayerId] = r.Score.totalAssets;
        }

        /// <summary>정산 결과가 있으면 순위대로, 없으면 참가자 순서대로.</summary>
        internal List<RankRow> RankRows()
        {
            var board = Store.Scoreboard;
            if (board?.rows != null && board.rows.Length > 0)
                return board.rows.OrderBy(r => r.rank).ThenBy(r => r.playerId)
                    .Select(r => new RankRow { PlayerId = r.playerId, Rank = r.rank, Score = r }).ToList();

            var players = Store.Public?.players;
            if (players == null) return new List<RankRow>();
            return players.Select(p => new RankRow { PlayerId = p.playerId, Rank = 0, Connected = p.connected }).ToList();
        }

        // ------------------------------------------------------------------
        // 매 프레임: 타이머·전광판·슬라이드
        // ------------------------------------------------------------------

        private void Update()
        {
            if (_c == null) return;
            // A missed change callback must not leave the new turn using an old timer/UI.
            if (!ReferenceEquals(_renderedPublic, Store.Public) || !ReferenceEquals(_renderedView, Store.View)) Refresh();
            if (Store.Public?.turn > 0 && !_c.HasCurrentPlayerView && string.IsNullOrEmpty(Store.EndReason))
            {
                if (_missingViewSince < 0) _missingViewSince = Time.realtimeSinceStartup;
                if (Time.realtimeSinceStartup - _missingViewSince >= .5f && _viewRefreshTurn != Store.Public.turn)
                { _viewRefreshTurn = Store.Public.turn; _c.RefreshPlayerView(); }
            }
            else _missingViewSince = -1;
            UpdateTimer();
            UpdateUrgency();
            ClearDraftsOnOutsideClick();
            if (Store.Public?.phase == Protocol.Selection) _investmentPage.Tick();
            UpdateBanner();
            if (_swapGroup != null) // 교환 턴 안내 띠: 전광판 알림·공용 정보 차례 안내가 없을 때만 보인다
                _swapGroup.alpha = string.IsNullOrEmpty(_swapText.text) || _turnNotice.gameObject.activeSelf ? 0f : 1f - _bannerGroup.alpha;

            _pageT = Mathf.MoveTowards(_pageT, _pageTarget, Time.unscaledDeltaTime * 4f);
            float e = Mathf.SmoothStep(0, 1, _pageT);
            _pager.anchorMin = new Vector2(-e, 0);
            _pager.anchorMax = new Vector2(2 - e, 1);
            _pageHint.text = ""; // 화면 아래 안내 문구는 표시하지 않음(가장자리 클릭 이동은 그대로 동작)


        }

        private readonly List<RaycastResult> _clickHits = new List<RaycastResult>();

        /// <summary>차트·버튼이 아닌 곳을 누르면 확정(예약·구매)하지 않은 구간 선택 표시를 지운다.</summary>
        private void ClearDraftsOnOutsideClick()
        {
            var mouse = UnityEngine.InputSystem.Mouse.current;
            if (_infoPage == null || mouse == null || !mouse.leftButton.wasPressedThisFrame || EventSystem.current == null) return;
            var data = new PointerEventData(EventSystem.current) { position = mouse.position.ReadValue() };
            _clickHits.Clear();
            EventSystem.current.RaycastAll(data, _clickHits);
            if (_clickHits.Count > 0 && _clickHits[0].gameObject != null)
            {
                var hit = _clickHits[0].gameObject;
                if (hit.GetComponentInParent<Selectable>() != null || hit.GetComponentInParent<ChartPointerLayer>() != null
                    || hit.GetComponentInParent<BarChartGraphic>() != null) return; // 차트·버튼·입력칸을 누른 것
            }
            _infoPage.ClearUnconfirmed();
        }

        /// <summary>단계 마감 10초 전부터 가장자리를 붉게(남을수록 진하게). 시장 공개·정산·대기실·종료·일시정지는 제외.</summary>
        private void UpdateUrgency()
        {
            if (_urgency == null) return;
            var s = Store.Public;
            float level = 0f;
            if (s != null && !s.paused && s.phaseEndsAtServerSeconds > 0
                && s.phase != Protocol.Lobby && s.phase != Protocol.Ended && s.phase != Protocol.Reveal && s.phase != Protocol.Result)
            {
                double remain = s.phaseEndsAtServerSeconds - _clock.EstimatedServerSeconds(Time.realtimeSinceStartup);
                if (remain > 0 && remain <= 10) level = 0.45f + 0.55f * (float)(1 - remain / 10);
            }
            _urgency.SetLevel(level);
            _urgency.Update();
        }

        private void UpdateTimer()
        {
            var s = Store.Public;
            bool developer = s?.developerMode == true && s.stockSystem && _c.IsHost && s.turn > 0 && s.phase != Protocol.Ended;
            _developerTimer.gameObject.SetActive(developer && s.phase == Protocol.Investment);
            _developerPrevious.gameObject.SetActive(developer); _developerReset.gameObject.SetActive(developer);
            UiKit.SetButton(_developerTimer, s?.developerTimerRunning == true ? "타이머 중지 · 수동 전환" : "현재 일차부터 타이머 적용",
                developer && !s.paused, Palette.AccentSoft, Palette.Accent);
            _developerPrevious.interactable = developer && !s.paused && s.developerCanGoBack;
            _developerReset.interactable = developer && !s.paused;
            UiKit.Place((RectTransform)_doneButton.transform, new Vector2(.5f, developer ? 1 : .5f), new Vector2(.5f, developer ? 1 : .5f),
                new Vector2(0, developer ? -48 : -12), new Vector2(140, 44));
            if (s == null)
            {
                _timerText.text = "접속 대기";
                UiKit.SetButton(_doneButton, "상태 갱신 중", false, Palette.Disabled, Palette.DisabledText);
                return;
            }

            if (!PhaseClock.HasDeadline(s) && !s.developerMode)
            {
                _timerText.text = ""; // '시간 제한 없음' 문구는 표시하지 않음
                _timerText.color = Palette.Text;
                UiKit.SetButton(_doneButton, "완료", false, Palette.Disabled, Palette.DisabledText);
                return;
            }

            if (s.paused)
            {
                _timerText.text = "<size=11>일시정지</size>\n<size=28><b>" + DisplayFormat.Clock(_clock.RemainingSeconds(s, Time.realtimeSinceStartup)) + "</b></size>";
                _timerText.color = Palette.Text;
                UiKit.SetButton(_doneButton, "일시정지 중", false, Palette.Disabled, Palette.DisabledText);
                return;
            }

            int remain = _clock.RemainingSeconds(s, Time.realtimeSinceStartup);
            string what = s.publicSelectionActive ? "공용 공개 " + (s.publicSelectionIndex + 1) + "/4 · " + s.publicSelectionPlayerId
                : s.phase == Protocol.Selection
                ? (string.IsNullOrEmpty(s.currentPlayerId) ? "선택" : s.currentPlayerId + " 차례")
                : PhaseNames.Korean(s.phase);
            _timerText.text = "<size=11>" + (remain == 0 ? "서버 진행 대기" : what) + "</size>\n<size=28><b>" + DisplayFormat.Clock(remain) + "</b></size>";
            if (s.developerMode && !PhaseClock.HasDeadline(s)) _timerText.text = "<size=11>개발자 모드 · " + what + "</size>\n<size=22><b>수동 진행</b></size>";
            _timerText.color = remain <= 10 && remain > 0 ? Palette.Bad : Palette.Text; // 마감 10초 전부터 빨간 글자

            bool can = _c.CanCompleteStep();
            bool done = _c.IsDoneForCurrentPhase;
            string label = done ? "완료함 · 대기 중"
                : s.phase == Protocol.Investment ? (Store.OrderLocked ? "다른 참가자 대기" : s.stockSystem ? "실시간 매매 중" : "주문 확정 시 진행")
                : s.phase == Protocol.Selection ? (s.stockSystem ? "매수 완료 시 진행" : "선택 시 자동 진행")
                : "완료";
            if (s.developerMode && can) label = s.stockSystem && s.phase == Protocol.Investment ? "확인 · 다음 일차" : "확인 · 다음 단계";
            _doneButton.name = developer ? "DeveloperNext" : s.stockSystem && s.phase == Protocol.Investment ? "LiveTradingStatus" : "Button";
            UiKit.SetButton(_doneButton, label, can, can ? Palette.Accent : Palette.Disabled, can ? Color.white : Palette.DisabledText);
        }

        private void RefreshAugmentList(string[] augments)
        {
            string key = string.Join(",", augments);
            if (key == _augmentKey) return;
            _augmentKey = key; UiKit.Clear(_augmentList);
            const float rowHeight = 20f;
            for (int i = 0; i < augments.Length; i++)
            {
                float y = (augments.Length - 1 - i) * rowHeight; // 마지막(새) 증강이 맨 아래
                var row = UiKit.Rect(_augmentList, "Augment_" + augments[i]);
                row.anchorMin = new Vector2(0, 0); row.anchorMax = new Vector2(1, 0); row.pivot = new Vector2(0, 0);
                row.offsetMin = new Vector2(0, y); row.offsetMax = new Vector2(0, y + rowHeight);
                var label = UiKit.Label(row, "- " + StockRules.Name(augments[i]), 13, Palette.Text, TextAnchor.MiddleLeft);
                label.horizontalOverflow = HorizontalWrapMode.Overflow;
            }
        }

        private void UpdateBanner()
        {
            // 공용 정보 차례 안내가 같은 자리에 떠 있으면 알림은 잠시 기다린다(바탕이 없어 글자가 겹치지 않게)
            if (_turnNotice != null && _turnNotice.gameObject.activeSelf) { _bannerGroup.alpha = 0f; return; }
            if (_bannerLeft <= 0f && _bannerQueue.Count > 0)
            {
                _bannerText.text = _bannerQueue.Dequeue();
                FitUnderline(_bannerLine, _bannerText);
                _bannerLeft = _bannerQueue.Count > 2 ? 1.5f : BannerSeconds; // 밀려 있으면 빨리 넘김
            }
            if (_bannerLeft > 0f)
            {
                _bannerLeft -= Time.unscaledDeltaTime;
                _bannerGroup.alpha = Mathf.Clamp01(_bannerLeft / 0.4f);
            }
            else
            {
                _bannerGroup.alpha = 0f;
            }
        }

        // ------------------------------------------------------------------
        // 동작
        // ------------------------------------------------------------------

        /// <summary>교환 턴 동안은 위쪽 안내 띠가 가려지지 않게 일반 알림을 띄우지 않는다(오류 알림은 띄움).</summary>
        private bool SwapQuiet => Store.Public?.swapTurn == true && (Store.Public.phase == Protocol.Info || Store.Public.phase == Protocol.Investment || Store.Public.phase == Protocol.Reveal);
        private static float SwapCeremonySeconds(Scoreboard board) => 1.2f + board.rows.Count(r => r != null && !string.IsNullOrEmpty(r.swapAccountId)) * 1.3f + 4.5f;

        public void ShowBanner(string message) { if (!SwapQuiet) ShowBannerAlways(message); }
        public void ShowBannerAlways(string message)
        {
            if (string.IsNullOrEmpty(message)) return;
            _bannerQueue.Enqueue(message);
        }

        private void ClearBanners()
        {
            _bannerQueue.Clear(); _bannerLeft = 0f;
            _bannerText.text = ""; _bannerGroup.alpha = 0f;
        }

        /// <summary>0 = 매수 창, 1 = 정보 구매 창.</summary>
        private bool _zoomFromTrade;
        public void ShowPage(int index)
        {
            _pageTarget = Mathf.Clamp01(index);
            if (index == 0) _zoomFromTrade = false;
            if (_pageTarget < .5f) _infoPage?.Collapse();
        }

        private void OnDoneClicked()
        {
            var status = _c.CompleteStep();
            if (status == CommandStatus.Sent && Store.Public?.developerMode != true)
                ShowBanner(Store.Public?.phase == Protocol.Investment ? Palette.GoodText("주문을 확정했습니다.") : Palette.GoodText("완료했습니다.") + " 다른 참가자를 기다립니다.");
        }

        // ------------------------------------------------------------------
        // 팝업(마우스 올림)
        // ------------------------------------------------------------------

        internal void ShowPlayerPopup(RankRow row, RectTransform slot)
        {
            if (row == null) return;
            bool stock = Store.Public?.stockSystem == true;
            var popup = OpenPopup(stock ? new Vector2(460, 540) : new Vector2(320, 230));
            PositionNextTo(popup, slot, rightSide: true);

            var r = row.Score;
            if (stock)
            {
                ShowStockPlayerPopup(popup, row, r);
                return;
            }
            string title = "<b>" + row.PlayerId + (row.PlayerId == _c.LocalPlayerId ? " (나)" : "") + "</b>";
            string body = r == null
                ? "\n<color=#8C8C8C>아직 정산 결과가 없습니다.</color>"
                : "\n이번 턴 수익률  <b><color=#" + ColorUtility.ToHtmlStringRGB(Palette.ForBps(r.turnReturnBps)) + ">" + DisplayFormat.Percent(r.turnReturnBps) + "</color></b>"
                  + "\n누적 손익  <b>" + DisplayFormat.SignedMoney(r.cumulativeProfit) + "</b>   ·   누적 수익률  <b>" + DisplayFormat.Percent(r.cumulativeReturnBps) + "</b>"
                  + "\n총자산  <b>" + DisplayFormat.Money(r.totalAssets) + "</b>";
            if (stock)
            {
                var augments = Store.Public.players?.FirstOrDefault(p => p.playerId == row.PlayerId)?.augments ?? System.Array.Empty<string>();
                body += "\n\n<b>보유 증강 (" + augments.Length + "/2)</b>\n" + (augments.Length == 0 ? "선택한 증강이 없습니다." :
                    string.Join("\n\n", augments.Select(id => "<b>" + StockRules.Name(id) + "</b> · " + StockRules.Description(id))));
            }
            var text = UiKit.Label(popup, title + body, 14, Palette.Text, TextAnchor.UpperLeft);
            UiKit.Stretch(text.rectTransform, 14, 10, 14, stock ? 112 : (r?.returnHistory?.Length > 0 ? 112 : 10));

            if (stock)
            {
                var augmentIds = Store.Public.players?.FirstOrDefault(p => p.playerId == row.PlayerId)?.augments ?? System.Array.Empty<string>();
                float gap = 84f;
                float start = -(augmentIds.Length - 1) * gap * 0.5f;
                for (int i = 0; i < augmentIds.Length; i++)
                {
                    var artwork = AugmentArtwork.Create(popup, augmentIds[i], 72);
                    UiKit.Place(artwork, new Vector2(.5f, 0), new Vector2(.5f, 0), new Vector2(start + i * gap, 20), new Vector2(72, 72));
                }
            }

            if (r?.returnHistory != null && r.returnHistory.Length > 0)
                AddTrendChart(popup, new[] { r }, new Vector2(14, stock ? 108 : 12), 92);
        }

        private void ShowStockPlayerPopup(RectTransform popup, RankRow row, ScoreRow score)
        {
            string title = "<b>" + row.PlayerId + (row.PlayerId == _c.LocalPlayerId ? " (나)" : "") + "</b>";
            string summary = score == null
                ? "\n<color=#6B7280>아직 정산 결과가 없습니다.</color>"
                : "\n이번 턴 수익률 <b><color=#" + ColorUtility.ToHtmlStringRGB(Palette.ForBps(score.turnReturnBps)) + ">" + DisplayFormat.Percent(score.turnReturnBps) + "</color></b>"
                  + "\n누적 손익 <b>" + DisplayFormat.SignedMoney(score.cumulativeProfit) + "</b> · 누적 수익률 <b>" + DisplayFormat.Percent(score.cumulativeReturnBps) + "</b>"
                  + "\n총자산 <b>" + DisplayFormat.Money(score.totalAssets) + "</b>";
            var summaryText = UiKit.Label(popup, title + summary, 14, Palette.Text, TextAnchor.UpperLeft);
            // 총자산 아래에 바로 증강 영역이 이어지도록 요약 영역의 하단 여백을 줄입니다.
            UiKit.Stretch(summaryText.rectTransform, 14, 10, 14, 130);

            var augmentIds = Store.Public.players?.FirstOrDefault(p => p.playerId == row.PlayerId)?.augments ?? System.Array.Empty<string>();
            var heading = UiKit.Label(popup, "<b>보유 증강 (" + augmentIds.Length + "/2)</b>", 14, Palette.Text, TextAnchor.UpperLeft);
            UiKit.Place(heading.rectTransform, new Vector2(0, 1), new Vector2(0, 1), new Vector2(14, -134), new Vector2(300, 24));

            for (int i = 0; i < augmentIds.Length; i++)
            {
                string id = augmentIds[i];
                var item = UiKit.Panel(popup, "PlayerAugment_" + id, Palette.Card, rounded: true);
                var itemTransform = item.rectTransform;
                itemTransform.anchorMin = new Vector2(0, 1);
                itemTransform.anchorMax = new Vector2(1, 1);
                itemTransform.pivot = new Vector2(.5f, 1);
                float top = 162 + i * 112;
                itemTransform.offsetMin = new Vector2(14, -top - 96);
                itemTransform.offsetMax = new Vector2(-14, -top);

                var artwork = AugmentArtwork.Create(item.transform, id, 82);
                UiKit.Place(artwork, new Vector2(0, .5f), new Vector2(0, .5f), new Vector2(8, 0), new Vector2(82, 82));
                var detail = UiKit.Label(item.transform,
                    "<b>" + StockRules.Name(id) + "</b>\n" + StockRules.Description(id),
                    13, Palette.Text, TextAnchor.MiddleLeft);
                UiKit.Stretch(detail.rectTransform, 104, 8, 8, 8);
            }

            if (augmentIds.Length == 0)
            {
                var none = UiKit.Label(popup, "선택한 증강이 없습니다.", 13, Palette.SubText, TextAnchor.UpperLeft);
                UiKit.Place(none.rectTransform, new Vector2(0, 1), new Vector2(0, 1), new Vector2(14, -162), new Vector2(300, 24));
            }

            if (score?.returnHistory != null && score.returnHistory.Length > 0)
                AddTrendChart(popup, new[] { score }, new Vector2(14, 14), 92);
        }

        private void ShowAllPlayersPopup(RectTransform anchor)
        {
            var popup = OpenPopup(new Vector2(380, 250));
            PositionNextTo(popup, anchor, rightSide: false);
            var board = Store.Scoreboard;

            var title = UiKit.Label(popup, "<b>플레이어별 수익률 추이</b>   <size=12>클릭: 내 고유능력 상태 보기</size>", 14, Palette.Text, TextAnchor.UpperLeft);
            UiKit.Stretch(title.rectTransform, 14, 10, 14, 10);

            if (board?.rows == null || board.rows.Length == 0)
            {
                var none = UiKit.Label(popup, "아직 정산 결과가 없습니다.", 14, Palette.SubText);
                UiKit.Stretch(none.rectTransform, 14, 40, 14, 14);
                return;
            }

            var legend = string.Join("    ", board.rows.OrderBy(r => r.playerId).Select(r =>
                "<color=#" + ColorUtility.ToHtmlStringRGB(Palette.Player(r.playerId)) + ">■</color> " + r.playerId + " " + DisplayFormat.Percent(r.cumulativeReturnBps)));
            var legendText = UiKit.Label(popup, legend, 12, Palette.Text, TextAnchor.UpperLeft);
            UiKit.Stretch(legendText.rectTransform, 14, 34, 14, 10);
            AddTrendChart(popup, board.rows, new Vector2(14, 14), 160);
        }

        private void AddTrendChart(RectTransform popup, IEnumerable<ScoreRow> rows, Vector2 margin, float height)
        {
            var list = rows.ToList();
            int maxAbs = 1000;
            int maxTurn = 10;
            foreach (var r in list)
                foreach (var p in r.returnHistory ?? new ReturnPoint[0])
                {
                    maxAbs = Mathf.Max(maxAbs, Mathf.Abs(p.returnBps));
                    maxTurn = Mathf.Max(maxTurn, p.turn);
                }
            float range = maxAbs * 1.2f;

            var rt = UiKit.Rect(popup, "Trend");
            rt.anchorMin = new Vector2(0, 0);
            rt.anchorMax = new Vector2(1, 0);
            rt.pivot = new Vector2(0.5f, 0);
            rt.offsetMin = new Vector2(margin.x + 30, margin.y + 12);
            rt.offsetMax = new Vector2(-margin.x, margin.y + height);
            var chart = rt.gameObject.AddComponent<LineChartGraphic>();
            chart.SetRange(0, maxTurn, -range, range);
            chart.AddHLine(0, Palette.Grid);
            foreach (var r in list)
            {
                var pts = (r.returnHistory ?? new ReturnPoint[0]).Where(p => p != null && p.turn > 0).OrderBy(p => p.turn).Select(p => new Vector2(p.turn, p.returnBps)).ToList();
                pts.Insert(0, Vector2.zero); // 시작 지점: 0턴 0%
                chart.AddSeries(pts, Palette.Player(r.playerId), 2.5f, 5f);
            }

            var top = UiKit.Label(rt, DisplayFormat.Percent((int)range), 9, Palette.SubText, TextAnchor.UpperRight);
            UiKit.Place(top.rectTransform, new Vector2(0, 1), new Vector2(1, 1), new Vector2(-2, 0), new Vector2(40, 12));
            var bottom = UiKit.Label(rt, DisplayFormat.Percent(-(int)range), 9, Palette.SubText, TextAnchor.LowerRight);
            UiKit.Place(bottom.rectTransform, new Vector2(0, 0), new Vector2(1, 0), new Vector2(-2, 0), new Vector2(40, 12));
            var xLabel = UiKit.Label(rt, "0턴" + new string(' ', 40) + maxTurn + "턴", 9, Palette.SubText, TextAnchor.UpperCenter);
            UiKit.Place(xLabel.rectTransform, new Vector2(0.5f, 0), new Vector2(0.5f, 1), new Vector2(0, -1), new Vector2(300, 12));
        }

        private RectTransform OpenPopup(Vector2 size)
        {
            UiKit.Clear(_popup);
            _dim.gameObject.SetActive(true);
            _dim.transform.SetAsLastSibling();
            _popup.SetAsLastSibling();
            _popup.gameObject.SetActive(true);

            var shadow = UiKit.Panel(_popup, "Shadow", new Color(0, 0, 0, 0.25f), rounded: true);
            UiKit.Stretch(shadow.rectTransform, -3, -1, -3, -5);
            var panel = UiKit.Panel(_popup, "Panel", Palette.Card, rounded: true);
            UiKit.Stretch(panel.rectTransform);
            _popup.sizeDelta = size;
            return panel.rectTransform;
        }

        private void PositionNextTo(RectTransform popup, RectTransform target, bool rightSide)
        {
            // 대상의 화면 위치를 Screen 좌표계로 옮겨 옆에 붙인다.
            var corners = new Vector3[4];
            target.GetWorldCorners(corners);
            var local = ScreenRect.InverseTransformPoint(rightSide ? corners[2] : corners[1]); // 오른쪽 위 / 왼쪽 위
            _popup.anchorMin = _popup.anchorMax = new Vector2(0.5f, 0.5f);
            _popup.pivot = rightSide ? new Vector2(0, 1) : new Vector2(1, 0);
            var pos = new Vector2(local.x + (rightSide ? 10 : -10), local.y);
            if (!rightSide) pos.y = ScreenRect.InverseTransformPoint(corners[0]).y; // 왼쪽 아래 기준으로 위로 펼침
            // 화면 밖으로 나가지 않게 보정
            var half = ScreenRect.rect.size * 0.5f;
            if (rightSide) pos.y = Mathf.Clamp(pos.y, -half.y + _popup.sizeDelta.y + 8, half.y - 8);
            _popup.anchoredPosition = pos;
        }

        internal void HidePopup()
        {
            _dim.gameObject.SetActive(false);
            _popup.gameObject.SetActive(false);
        }
    }

    internal sealed class RankRow
    {
        public string PlayerId;
        public int Rank;
        public bool Connected = true;
        public ScoreRow Score;
    }

    internal sealed class RankSlot
    {
        private readonly int _index;
        private readonly Image _border;
        private readonly Text _text, _assets;
        private readonly RectTransform _rt;
        private RankRow _row;
        private string _delta = "";

        public static Vector2 PositionOf(int index) => new Vector2(0, -64 - index * 96);

        public RankSlot(RectTransform column, int index, TabletView owner)
        {
            _index = index;
            // 네모 칸 대신 흰 밑줄만(방송 화면 스타일). 투명 바탕은 클릭 판정용.
            var bg = UiKit.Panel(column, "RankSlot" + (index + 1), Color.clear, raycast: true);
            _rt = bg.rectTransform;
            UiKit.Place(_rt, new Vector2(0.5f, 1), new Vector2(0.5f, 1), PositionOf(index), new Vector2(84, 86));
            _border = UiKit.Panel(_rt, "Underline", Color.white);
            _border.rectTransform.anchorMin = Vector2.zero; _border.rectTransform.anchorMax = new Vector2(1, 0);
            _border.rectTransform.offsetMin = new Vector2(4, 0); _border.rectTransform.offsetMax = new Vector2(-4, 2);
            var inner = UiKit.Panel(_rt, "Inner", Color.clear);
            UiKit.Stretch(inner.rectTransform, 3, 3, 3, 5);
            _text = UiKit.Label(inner.transform, "", 13, Palette.Text, TextAnchor.UpperCenter);
            UiKit.Stretch(_text.rectTransform, 2, 5, 2, 36);
            // 보유 금액(총자산): 크게 표시. '직업 미정' 줄은 없앴다.
            _assets = UiKit.Label(inner.transform, "", 17, Palette.Text, TextAnchor.MiddleCenter, FontStyle.Bold);
            _assets.name = "RankAssets";
            UiKit.Stretch(_assets.rectTransform, 2, 0, 2, 0);
            _assets.rectTransform.anchorMax = new Vector2(1, 0);
            _assets.rectTransform.offsetMin = new Vector2(1, 6);
            _assets.rectTransform.offsetMax = new Vector2(-1, 34);
            _assets.resizeTextForBestFit = true; // 금액이 길어지면 칸 안에 맞게 자동으로 줄임
            _assets.resizeTextMinSize = 11;
            _assets.resizeTextMaxSize = 17;

            // 플레이어 칸을 클릭해서 팝업을 고정하지 않고, 마우스가 칸 위에 있을 때만 표시합니다.
            // 칸에서 마우스가 벗어나면 즉시 숨겨서 이전처럼 팝업이 화면에 남지 않게 합니다.
            UiKit.Hover(bg.gameObject, () => owner.ShowPlayerPopup(_row, _rt), owner.HidePopup);
        }

        /// <param name="fromIndex">이번에 순서가 바뀌었으면 이 플레이어가 있던 칸 번호, 아니면 -1</param>
        /// <param name="prevAssets">직전에 보여 준 총자산(없으면 null → 세는 효과 없음)</param>
        public void Set(RankRow row, string localPlayerId, int fromIndex = -1, long? prevAssets = null, float delay = 0f)
        {
            bool samePlayer = _row != null && row != null && _row.PlayerId == row.PlayerId;
            _row = row;
            _rt.gameObject.SetActive(row != null);
            if (row == null) return;

            if (fromIndex >= 0 && fromIndex != _index)
            {
                bool up = fromIndex > _index;
                _delta = up ? " <color=#FFFFFF>▲" + (fromIndex - _index) + "</color>" : " <color=#9A9A9A>▼" + (_index - fromIndex) + "</color>";
                if (up) _rt.SetAsLastSibling(); // 올라오는 칸이 위에 보이게
                UiAnim.Slide(_rt, PositionOf(fromIndex), PositionOf(_index), UiAnim.SlideSeconds, delay);
                if (up) UiAnim.Pulse(_rt, delay + UiAnim.SlideSeconds * 0.8f, 1.1f);
            }
            else if (fromIndex >= 0 || !samePlayer) _delta = "";

            // 밑줄은 흰색 고정. 누구인지는 이름 앞 플레이어 색 점으로 구분.
            string rank = row.Rank > 0 ? row.Rank + "등" : "-";
            string me = row.PlayerId == localPlayerId ? " (나)" : "";
            string dot = "<color=#" + ColorUtility.ToHtmlStringRGB(Palette.Player(row.PlayerId)) + ">●</color> ";
            string conn = row.Connected ? "" : "\n" + Palette.BadText("연결 끊김");
            _text.text = "<b>" + rank + "</b>" + _delta + "\n" + dot + row.PlayerId + me + conn;

            if (row.Score == null) { _assets.text = ""; return; }
            long now = row.Score.totalAssets;
            UiAnim.Count(_assets, prevAssets ?? now, now, v => DisplayFormat.Money(v), UiAnim.CountSeconds, delay);
        }
    }
}
