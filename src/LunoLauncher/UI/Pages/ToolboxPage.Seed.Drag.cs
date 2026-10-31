using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace LunoLauncher.UI.Pages
{
    /// <summary>
    /// R5：自绘小窗（结构查找面板、结构详情小框）都能按住拖动。
    ///
    /// 关键点：
    ///  - 拖动把手只包含标题/坐标那一块，按钮不在把手里面，所以"点按钮"不会被拖动吃掉；
    ///  - 按下时把事件标记为 Handled 并 CaptureMouse，画布的拖动逻辑就收不到这次事件了；
    ///  - 坐标按父容器分别处理：在 Grid 里改 Margin，在 Canvas 里改 Canvas.Left/Top。
    /// </summary>
    public partial class ToolboxPage
    {
        bool _panelDragging;
        FrameworkElement _panelTarget;
        bool _panelInCanvas;
        Point _panelStart;
        Point _panelOrigin;

        /// <summary>把 handle 变成 target 的拖动把手</summary>
        void MakeDraggable(FrameworkElement handle, FrameworkElement target, bool inCanvas)
        {
            if (handle == null || target == null) return;

            handle.MouseLeftButtonDown += (s, e) =>
            {
                if (e.ChangedButton != MouseButton.Left) return;
                e.Handled = true;
                _panelDragging = true;
                _panelTarget = target;
                _panelInCanvas = inCanvas;
                _panelStart = e.GetPosition(MapHost);
                _panelOrigin = inCanvas
                    ? new Point(Canvas.GetLeft(target), Canvas.GetTop(target))
                    : new Point(target.Margin.Left, target.Margin.Top);
                if (double.IsNaN(_panelOrigin.X)) _panelOrigin.X = 0;
                if (double.IsNaN(_panelOrigin.Y)) _panelOrigin.Y = 0;
                handle.CaptureMouse();
            };

            handle.MouseMove += (s, e) =>
            {
                if (!_panelDragging || !ReferenceEquals(_panelTarget, target)) return;
                var p = e.GetPosition(MapHost);
                double nx = _panelOrigin.X + (p.X - _panelStart.X);
                double ny = _panelOrigin.Y + (p.Y - _panelStart.Y);
                double w = target.ActualWidth > 0 ? target.ActualWidth : 200;
                double h = target.ActualHeight > 0 ? target.ActualHeight : 80;
                if (inCanvas)
                {
                    Canvas.SetLeft(target, Math.Max(0, Math.Min(Math.Max(0, AreaW - w), nx)));
                    Canvas.SetTop(target, Math.Max(0, Math.Min(Math.Max(0, AreaH - h), ny)));
                }
                else
                {
                    target.Margin = new Thickness(
                        Math.Max(0, Math.Min(Math.Max(0, AreaW - w), nx)),
                        Math.Max(0, Math.Min(Math.Max(0, AreaH - h), ny)), 0, 0);
                }
            };

            handle.MouseLeftButtonUp += (s, e) =>
            {
                if (!_panelDragging) return;
                _panelDragging = false;
                _panelTarget = null;
                if (handle.IsMouseCaptured) handle.ReleaseMouseCapture();
                e.Handled = true;
            };

            handle.LostMouseCapture += (s, e) =>
            {
                if (ReferenceEquals(_panelTarget, target)) { _panelDragging = false; _panelTarget = null; }
            };
        }

        void WirePanelDragging()
        {
            if (_panelDragWired) return;
            _panelDragWired = true;
            MakeDraggable(FinderTitle, FinderPanel, false);
            MakeDraggable(StructInfoDrag, StructInfo, true);
        }

        bool _panelDragWired;

        void StructInfo_Enter(object sender, MouseEventArgs e) => CancelHideStructInfo();

        void StructInfo_Leave(object sender, MouseEventArgs e)
        {
            if (_panelDragging) return;      // 正在拖就别收
            HideStructInfoSoon();
        }
    }
}
