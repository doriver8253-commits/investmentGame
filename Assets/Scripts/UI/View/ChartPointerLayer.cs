// NEXT-D01/D02 그래프 위 포인터 입력(마우스 위치 툴팁·날짜 클릭).
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using BlackWhale.Shared;
using BlackWhale.UI.Presentation;

namespace BlackWhale.UI.View
{
    /// <summary>그래프 위 포인터 → 날짜. Canvas 배율·카메라를 고려해 RectTransform 로컬 좌표로 바꾼 뒤 날짜를 계산한다.
    /// 봉이 아니라 날짜 칸 전체가 클릭을 받는다(값이 없는 날짜도 선택 가능).</summary>
    [RequireComponent(typeof(BarChartGraphic))]
    public sealed class ChartPointerLayer : MonoBehaviour, IPointerMoveHandler, IPointerEnterHandler, IPointerExitHandler, IPointerClickHandler, IPointerDownHandler, IDragHandler, IPointerUpHandler
    {
        public System.Action<int?> Hovered;
        /// <summary>day, 오른쪽 버튼 여부</summary>
        public System.Action<int, bool> Clicked;
        public ChartRangeSelection RangeSelection;
        public System.Func<bool> CanEditRange;
        public System.Func<Interval[]> Intervals;
        public System.Func<int, int, int, string> EditInterval;
        public System.Action<int> DeleteInterval;
        public System.Action RangeChanged;
        /// <summary>드래그(또는 클릭)로 구간 고르기를 마친 순간. 투자 결정 단계에서는 이때 바로 진입·청산을 정한다.</summary>
        public System.Action RangeFinished;
        public string LastEditError { get; private set; } = "";
        private bool _rangeDown;
        private InputAction _deleteAction;

        private BarChartGraphic _chart;

        private void Awake()
        {
            _chart = GetComponent<BarChartGraphic>();
            _chart.SetInteractive(true);
        }

        // 그래프 Awake 순서와 상관없이 항상 클릭을 받도록(미열람 칸 포함 날짜 칸 전체가 입력 영역).
        private void OnEnable()
        {
            if (_chart == null) _chart = GetComponent<BarChartGraphic>();
            if (_chart != null) _chart.SetInteractive(true);
            if (_deleteAction == null)
            {
                _deleteAction = new InputAction("DeleteSelectedChartRange", InputActionType.Button, "<Keyboard>/delete");
                _deleteAction.AddBinding("<Keyboard>/backspace");
                _deleteAction.performed += context => {
                    if (EventSystem.current?.currentSelectedGameObject == gameObject) DeleteSelectedRange();
                };
            }
            _deleteAction.Enable();
        }
        private void OnDisable() { _rangeDown = false; _deleteAction?.Disable(); }
        private void OnDestroy() => _deleteAction?.Dispose();

        private int? DayOf(PointerEventData e, Camera cam)
        {
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(_chart.rectTransform, e.position, cam, out var local))
                return null;
            return _chart.DayAtLocalX(local.x);
        }

        public void OnPointerEnter(PointerEventData e) => Report(DayOf(e, e.enterEventCamera));
        public void OnPointerMove(PointerEventData e) => Report(DayOf(e, e.enterEventCamera));

        public void OnPointerExit(PointerEventData e)
        {
            _chart.SetHover(null);
            Hovered?.Invoke(null);
        }

        public void OnPointerClick(PointerEventData e)
        {
            if (RangeSelection != null) return;
            var day = DayOf(e, e.pressEventCamera);
            if (!day.HasValue) return;
            if (e.button == PointerEventData.InputButton.Left) Clicked?.Invoke(day.Value, false);
            else if (e.button == PointerEventData.InputButton.Right) Clicked?.Invoke(day.Value, true);
        }

        public void OnPointerDown(PointerEventData e)
        {
            if (RangeSelection == null || CanEditRange?.Invoke() != true) return;
            if (e.button == PointerEventData.InputButton.Right)
            {
                EventSystem.current?.SetSelectedGameObject(gameObject);
                var single = DayOf(e, e.pressEventCamera);
                if (single.HasValue && !RangeSelection.FixedPublicWindow) RangeSelection.SetRange(single.Value, single.Value);
                else RangeSelection.Clear();
                RangeChanged?.Invoke(); return;
            }
            if (e.button != PointerEventData.InputButton.Left) return;
            var day = DayOf(e, e.pressEventCamera); if (!day.HasValue) return;
            EventSystem.current?.SetSelectedGameObject(gameObject);
            RangeSelection.Begin(day.Value, Intervals?.Invoke() ?? new Interval[0]); _rangeDown = true;
            LastEditError = ""; RangeChanged?.Invoke();
        }
        public void OnDrag(PointerEventData e)
        {
            if (!_rangeDown || CanEditRange?.Invoke() != true) return;
            RangeSelection.Drag(DragDay(e)); RangeChanged?.Invoke();
        }
        private int DragDay(PointerEventData e)
        {
            var day = DayOf(e, e.pressEventCamera);
            if (day.HasValue) return day.Value;
            RectTransformUtility.ScreenPointToLocalPointInRectangle(_chart.rectTransform, e.position, e.pressEventCamera, out var local);
            return local.x < _chart.rectTransform.rect.xMin ? 0 : 30;
        }
        public void OnPointerUp(PointerEventData e)
        {
            if (!_rangeDown) return; _rangeDown = false;
            if (CanEditRange?.Invoke() != true) return;
            RangeSelection.Finish(DragDay(e));
            if (RangeSelection.SelectedIndex >= 0 && RangeSelection.ChangedByDrag && EditInterval != null)
            {
                LastEditError = EditInterval(RangeSelection.SelectedIndex, RangeSelection.Start, RangeSelection.End);
                if (!string.IsNullOrEmpty(LastEditError)) RangeSelection.Restore(Intervals?.Invoke() ?? new Interval[0]);
            }
            RangeChanged?.Invoke();
            RangeFinished?.Invoke();
        }
        public void DeleteSelectedRange()
        {
            if (RangeSelection == null || CanEditRange?.Invoke() != true) return;
            if (RangeSelection.SelectedIndex >= 0) DeleteInterval?.Invoke(RangeSelection.SelectedIndex);
            RangeSelection.Clear(); RangeChanged?.Invoke();
        }

        private void Report(int? day)
        {
            _chart.SetHover(day);
            Hovered?.Invoke(day);
        }
    }
}
