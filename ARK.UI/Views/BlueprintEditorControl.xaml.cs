using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ARK.UI.Core.Models;
using ARK.UI.Core.Nodes;
using ARK.UI.ViewModels;
using UserControl      = System.Windows.Controls.UserControl;
using MouseEventArgs   = System.Windows.Input.MouseEventArgs;
using KeyEventArgs     = System.Windows.Input.KeyEventArgs;
using DragEventArgs    = System.Windows.DragEventArgs;
using DragDropEffects  = System.Windows.DragDropEffects;
using WpfDataObject    = System.Windows.DataObject;
using WpfPoint         = System.Windows.Point;
using WpfColor         = System.Windows.Media.Color;
using RoutedEventArgs  = System.Windows.RoutedEventArgs;

namespace ARK.UI.Views;

public partial class BlueprintEditorControl : UserControl
{
    // ── Восстановление/сохранение ширины боковых панелей ─────────────────

    private void OnControlLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is not BlueprintEditorViewModel vm) return;
        ToolboxBorder.Width = vm.ToolboxWidth;
        PropsBorder.Width   = vm.PropertiesWidth;
    }

    internal void SavePanelWidths(AppConfig config)
    {
        if (ToolboxBorder.Width > 0)
            config.ToolboxWidth    = ToolboxBorder.Width;
        if (PropsBorder.Width > 0)
            config.PropertiesWidth = PropsBorder.Width;
    }

    // ── Toolbox Drag-and-Drop ──────────────────────────────────────────
    private const string DragFormat = "ARK_NodeTemplate_Type";
    private WpfPoint?              _toolboxDragStart;
    private NodeTemplateViewModel? _toolboxDragTemplate;

    // ── Real-Time нод-перетаскивание ───────────────────────────────────
    // Нода перемещается физически: X/Y обновляются на каждый MouseMove.
    // VisualConnectionLine подписан на PropertyChanged X/Y и пересчитывает BezierPath → связи
    // "тянутся" за нодой без дополнительной логики.
    private bool        _isDraggingNode;
    private WpfPoint    _dragStartMouse;
    private double      _dragStartX;
    private double      _dragStartY;
    private VisualNode? _draggedNode;

    // ── Магнитная стыковка QueueBlock ─────────────────────────────────
    private const double QueueCardHeight = 60.0;
    private const double QueueSnapGap    =  2.0;   // минимальный зазор между стакнутыми блоками

    // ── Протягивание связи ─────────────────────────────────────────────
    private bool             _isDrawingConnection;
    private VisualNode?      _connectionSource;
    private bool             _connectionIsError;
    private bool             _connectionIsData;
    private bool             _connectionFromInput;   // true = drag стартовал с "In" порта
    private WpfPoint         _connectionOrigin;
    private FrameworkElement? _snapTarget;

    public BlueprintEditorControl()
    {
        InitializeComponent();

        EditorCanvas.PreviewMouseLeftButtonDown += Canvas_PreviewMouseLeftButtonDown;
        EditorCanvas.MouseMove                  += Canvas_MouseMove;
        EditorCanvas.MouseLeftButtonUp          += Canvas_MouseLeftButtonUp;
        EditorCanvas.LostMouseCapture           += Canvas_LostMouseCapture;

        EditorCanvas.DragOver += Canvas_DragOver;
        EditorCanvas.Drop     += Canvas_Drop;

        ToolboxList.PreviewMouseLeftButtonDown += Toolbox_PreviewMouseLeftButtonDown;
        ToolboxList.PreviewMouseMove           += Toolbox_PreviewMouseMove;
        ToolboxList.MouseLeftButtonUp          += Toolbox_MouseLeftButtonUp;

        this.PreviewKeyDown += BlueprintEditorControl_PreviewKeyDown;
        this.Unloaded += (_, _) => (DataContext as BlueprintEditorViewModel)?.ResetAllNodeStates();
    }

    // ══ Toolbox: источник Drag-and-Drop ═══════════════════════════════

    private void Toolbox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var template = (e.OriginalSource as FrameworkElement)?.DataContext as NodeTemplateViewModel;
        if (template is null) return;
        _toolboxDragStart    = e.GetPosition(ToolboxList);
        _toolboxDragTemplate = template;
    }

    private void Toolbox_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _toolboxDragStart    = null;
        _toolboxDragTemplate = null;
    }

    private void Toolbox_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_toolboxDragStart is null || _toolboxDragTemplate is null) return;
        if (e.LeftButton != MouseButtonState.Pressed) { _toolboxDragStart = null; return; }

        var delta = e.GetPosition(ToolboxList) - _toolboxDragStart.Value;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance
         && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        var template = _toolboxDragTemplate;
        _toolboxDragStart    = null;
        _toolboxDragTemplate = null;

        var data = new WpfDataObject(DragFormat, template.NodeType);
        DragDrop.DoDragDrop(e.OriginalSource as DependencyObject ?? ToolboxList, data, DragDropEffects.Copy);
    }

    // ══ Canvas: Drop-цель ══════════════════════════════════════════════

    private static void Canvas_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DragFormat) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Canvas_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DragFormat) is not Type nodeType) return;
        // Принимаем только реальные потомки BaseNode — отклоняем VisualNode и прочие случайные типы.
        if (!nodeType.IsSubclassOf(typeof(BaseNode))) return;
        var pos = e.GetPosition(EditorCanvas);
        (DataContext as BlueprintEditorViewModel)
            ?.DropNodeCommand.Execute(new NodeDropPayload(nodeType, pos.X, pos.Y));
        e.Handled = true;
    }

    // ══ Canvas: нажатие кнопки мыши ═══════════════════════════════════

    private void Canvas_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var origin = e.OriginalSource as DependencyObject;
        var pos    = e.GetPosition(EditorCanvas);

        // Клик по выходному порту → начинаем протягивание связи.
        if (origin is FrameworkElement { Tag: string tag } && tag is "Success" or "Error" or "Data")
        {
            var node = FindNodeFromSource(origin);
            if (node is null) return;
            BeginConnectionDraw(node, tag == "Error", pos, tag == "Data");
            e.Handled = true;
            return;
        }

        // Клик по входному порту → обратное перетаскивание для разрыва связи (Toggle).
        if (origin is FrameworkElement { Tag: "In" })
        {
            var node = FindNodeFromSource(origin);
            if (node is null) return;
            BeginConnectionDrawFromInput(node, pos);
            e.Handled = true;
            return;
        }

        // Клик по элементу связи (тело кривой) — не снимаем выделение.
        if (IsConnectionElement(origin)) return;

        // Клик по телу карточки → выделяем ноду и начинаем перетаскивание.
        var dragNode = FindNodeFromSource(origin);
        var vm       = DataContext as BlueprintEditorViewModel;
        if (dragNode is null)
        {
            if (vm is not null) vm.SelectedNode = null;
            return;
        }

        // Очищаем зависшее состояние Toolbox-drag: если пользователь кликнул в Toolbox
        // и отпустил кнопку вне его границ, Toolbox_MouseLeftButtonUp не срабатывает —
        // старые _toolboxDragStart/_toolboxDragTemplate остаются ненулевыми.
        // При возврате мыши в Toolbox с зажатой кнопкой это могло бы запустить DoDragDrop.
        _toolboxDragStart    = null;
        _toolboxDragTemplate = null;

        vm!.SelectedNode = dragNode;
        BeginDirectDrag(dragNode, pos);
        e.Handled = true;
    }

    // ══ Захват горячей клавиши ═════════════════════════════════════════

    private void OnHotKeyBoxPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        if (fe.DataContext is not HotkeyTriggerNode node) return;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        e.Handled = true;

        if (key == Key.Escape)
        {
            node.HotKey          = Key.None;
            node.HotKeyModifiers = ModifierKeys.None;
            return;
        }

        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
                or Key.LeftAlt  or Key.RightAlt  or Key.LWin      or Key.RWin)
            return;

        node.HotKey          = key;
        node.HotKeyModifiers = Keyboard.Modifiers;
    }

    // ══ Захват целевой клавиши для SendInputNode ══════════════════════

    private void OnTargetKeyBoxPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var node = (DataContext as BlueprintEditorViewModel)?.SelectedNode?.LogicalNode as SendInputNode;
        if (node is null) return;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        e.Handled = true;

        if (key == Key.Escape)
        {
            node.TargetKey       = Key.None;
            node.TargetModifiers = ModifierKeys.None;
            return;
        }

        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
                or Key.LeftAlt  or Key.RightAlt  or Key.LWin      or Key.RWin)
            return;

        node.TargetKey       = key;
        node.TargetModifiers = Keyboard.Modifiers;
    }

    // ══ Esc: отмена активной операции ══════════════════════════════════

    private void BlueprintEditorControl_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;

        if (_isDrawingConnection)
        {
            FinishConnectionDraw();
            e.Handled = true;
        }
        else if (_isDraggingNode && _draggedNode is not null)
        {
            // Отмена: возвращаем ноду на исходную позицию без записи.
            _draggedNode.X = _dragStartX;
            _draggedNode.Y = _dragStartY;
            EndDirectDrag(saveAfter: false);
            EditorCanvas.ReleaseMouseCapture();
            e.Handled = true;
        }
    }

    // ══ Canvas: движение мыши ══════════════════════════════════════════

    private void Canvas_MouseMove(object sender, MouseEventArgs e)
    {
        var pos = e.GetPosition(EditorCanvas);

        if (_isDrawingConnection)
        {
            // Snap: при прямом drag — ищем "In" порт; при обратном — "Success"/"Error"/"Data".
            _snapTarget = FindSnapTarget(pos);
            var endPos  = pos;
            if (_snapTarget is not null)
            {
                var snapNode = FindNodeFromSource(_snapTarget);
                if (snapNode is not null)
                {
                    if (_connectionFromInput)
                    {
                        var tag       = _snapTarget.Tag?.ToString() ?? "Success";
                        var (tx, ty)  = tag == "Data"  ? snapNode.DataPortCenter    :
                                        tag == "Error" ? snapNode.ErrorPortCenter   :
                                                         snapNode.SuccessPortCenter;
                        endPos = new WpfPoint(tx, ty);
                    }
                    else
                    {
                        var (tx, ty) = snapNode.InPortCenter;
                        endPos = new WpfPoint(tx, ty);
                    }
                }
            }
            UpdatePreviewPath(_connectionOrigin, endPos);
            return;
        }

        if (!_isDraggingNode || _draggedNode is null || !EditorCanvas.IsMouseCaptured) return;

        var deltaX = pos.X - _dragStartMouse.X;
        var deltaY = pos.Y - _dragStartMouse.Y;

        // Canvas 3000×2000; 2800/1800 — правый/нижний предел с запасом под размер карточки (~180×55).
        _draggedNode.X = Math.Clamp(_dragStartX + deltaX, 0, 2800);
        _draggedNode.Y = Math.Clamp(_dragStartY + deltaY, 0, 1800);

        if (_draggedNode.LogicalNode is Logic_QueueBlockNode)
            TrySnapQueueBlock();
    }

    // ══ Canvas: отпускание кнопки ══════════════════════════════════════

    private void Canvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        // Кнопка отпущена над холстом — любое зависшее состояние Toolbox-drag сбрасываем.
        _toolboxDragStart    = null;
        _toolboxDragTemplate = null;

        if (_isDrawingConnection)
        {
            // Финальный HitTest для гарантии snap (на случай быстрого клика без MouseMove).
            var snap = FindSnapTarget(e.GetPosition(EditorCanvas)) ?? _snapTarget;
            if (snap is not null && _connectionSource is not null)
            {
                var vm = DataContext as BlueprintEditorViewModel;
                if (_connectionFromInput)
                {
                    // Обратное направление: стартовали с "In" → финишируем на выходном порту
                    var outputNode = FindNodeFromSource(snap);
                    if (outputNode is not null && CanConnect(outputNode, _connectionSource))
                    {
                        var tag = snap.Tag?.ToString() ?? "Success";
                        vm?.ConnectNodes(outputNode.NodeId, _connectionSource.NodeId,
                                         tag == "Error", tag == "Data");
                    }
                }
                else
                {
                    // Прямое направление: стартовали с выходного порта → финишируем на "In"
                    var targetNode = FindNodeFromSource(snap);
                    if (targetNode is not null && CanConnect(_connectionSource, targetNode))
                        vm?.ConnectNodes(_connectionSource.NodeId, targetNode.NodeId,
                                         _connectionIsError, _connectionIsData);
                }
            }
            FinishConnectionDraw();
            return;
        }

        if (!_isDraggingNode) return;

        EndDirectDrag(saveAfter: true);
        EditorCanvas.ReleaseMouseCapture();
    }

    // ══ Потеря захвата мыши (Alt+Tab и т.п.) ══════════════════════════

    private void Canvas_LostMouseCapture(object sender, MouseEventArgs e)
    {
        // Потеря захвата (Alt+Tab, системный диалог) — гарантированно чистим Toolbox-состояние.
        _toolboxDragStart    = null;
        _toolboxDragTemplate = null;

        if (_isDraggingNode)
        {
            EndDirectDrag(saveAfter: false);
            return;
        }

        if (_isDrawingConnection)
        {
            _isDrawingConnection   = false;
            _connectionSource      = null;
            _connectionFromInput   = false;
            PreviewPath.Visibility = Visibility.Collapsed;
            PreviewPath.Data       = null;
        }
    }

    // ══ Direct Real-Time Drag ══════════════════════════════════════════

    private void BeginDirectDrag(VisualNode node, WpfPoint canvasPos)
    {
        _isDraggingNode = true;
        _draggedNode    = node;
        _dragStartMouse = canvasPos;
        _dragStartX     = node.X;
        _dragStartY     = node.Y;
        EditorCanvas.CaptureMouse();
    }

    private void EndDirectDrag(bool saveAfter)
    {
        _isDraggingNode = false;
        _draggedNode    = null;

        if (saveAfter && DataContext is BlueprintEditorViewModel vm)
            vm.TriggerDebouncedSave();
    }

    // Ищет ближайший блок очереди снизу, примагничивает _draggedNode к нему
    // и автоматически создаёт связь Success→In (если ещё не существует).
    // Удаление провода — только вручную стандартным Toggle-перетаскиванием.
    private void TrySnapQueueBlock()
    {
        if (_draggedNode is null) return;
        if (DataContext is not BlueprintEditorViewModel vm) return;

        const double snapThresholdX = 20.0;
        const double snapThresholdY = 25.0;

        foreach (var other in vm.VisualNodes)
        {
            if (other.NodeId == _draggedNode.NodeId) continue;
            if (other.LogicalNode is not Logic_QueueBlockNode) continue;

            double xDiff = Math.Abs(_draggedNode.X - other.X);
            double yGap  = _draggedNode.Y - (other.Y + QueueCardHeight + QueueSnapGap);

            if (xDiff < snapThresholdX && Math.Abs(yGap) < snapThresholdY)
            {
                // Позиционирование: ровно под верхним блоком с зазором 2 px
                _draggedNode.X = other.X;
                _draggedNode.Y = other.Y + QueueCardHeight + QueueSnapGap;

                // Авто-связь Success→In — только если её ещё нет.
                // ConnectNodes имеет Toggle-поведение (удаляет при повторном вызове),
                // поэтому проверяем наличие связи ДО вызова, чтобы не триггерить разрыв
                // при каждом MouseMove в зоне прилипания.
                bool alreadyLinked = vm.ConnectionLines.Any(l =>
                    l.Source.NodeId == other.NodeId        &&
                    l.Target.NodeId == _draggedNode.NodeId &&
                    !l.IsErrorRoute && !l.IsDataRoute);

                if (!alreadyLinked)
                    vm.ConnectNodes(other.NodeId, _draggedNode.NodeId, isErrorRoute: false);

                return;
            }
        }
    }

    // ══ Рисование связи ════════════════════════════════════════════════

    private void BeginConnectionDrawFromInput(VisualNode targetNode, WpfPoint pos)
    {
        _isDrawingConnection = true;
        _connectionSource    = targetNode;
        _connectionFromInput = true;
        _connectionIsError   = false;
        _connectionIsData    = false;

        var (ox, oy)      = targetNode.InPortCenter;
        _connectionOrigin = new WpfPoint(ox, oy);

        PreviewPath.Stroke     = new SolidColorBrush(WpfColor.FromArgb(0xAA, 0xC0, 0xC0, 0xC0));
        PreviewPath.Visibility = Visibility.Visible;

        UpdatePreviewPath(_connectionOrigin, pos);
        EditorCanvas.CaptureMouse();
    }

    private void BeginConnectionDraw(VisualNode source, bool isError, WpfPoint pos, bool isData = false)
    {
        _isDrawingConnection = true;
        _connectionSource    = source;
        _connectionIsError   = isError;
        _connectionIsData    = isData;
        _connectionFromInput = false;   // явный сброс: предыдущий обратный drag не должен влиять

        var (ox, oy) = isData  ? source.DataPortCenter    :
                       isError ? source.ErrorPortCenter   :
                                 source.SuccessPortCenter;
        _connectionOrigin = new WpfPoint(ox, oy);

        PreviewPath.Stroke     = isData  ? new SolidColorBrush(WpfColor.FromArgb(0xAA, 0xD3, 0xD3, 0xD3)) :
                                 isError ? new SolidColorBrush(WpfColor.FromArgb(0xAA, 0xD3, 0x2F, 0x2F)) :
                                           new SolidColorBrush(WpfColor.FromArgb(0xAA, 0xD4, 0xAF, 0x37));
        PreviewPath.Visibility = Visibility.Visible;

        UpdatePreviewPath(_connectionOrigin, pos);
        EditorCanvas.CaptureMouse();
    }

    private void UpdatePreviewPath(WpfPoint start, WpfPoint end)
    {
        var dx      = Math.Max(Math.Abs(end.X - start.X) * 0.5, 60.0);
        var cp1     = new WpfPoint(start.X + dx, start.Y);
        var cp2     = new WpfPoint(end.X   - dx, end.Y);
        var segment = new BezierSegment(cp1, cp2, end, isStroked: true);
        var figure  = new PathFigure { StartPoint = start, IsClosed = false };
        figure.Segments.Add(segment);
        PreviewPath.Data = new PathGeometry(new[] { figure });
    }

    private void FinishConnectionDraw()
    {
        _snapTarget            = null;
        _isDrawingConnection   = false;
        _connectionSource      = null;
        _connectionIsData      = false;
        _connectionFromInput   = false;
        PreviewPath.Visibility = Visibility.Collapsed;
        PreviewPath.Data       = null;
        EditorCanvas.ReleaseMouseCapture();
    }

    // ══ Вспомогательные методы ══════════════════════════════════════════

    // Ищет порт-цель под курсором: при прямом drag — "In"; при обратном — "Success"/"Error"/"Data".
    private FrameworkElement? FindSnapTarget(WpfPoint canvasPos)
    {
        FrameworkElement? found = null;
        VisualTreeHelper.HitTest(
            EditorCanvas,
            null,
            r =>
            {
                if (r.VisualHit is FrameworkElement fe)
                {
                    var tag     = fe.Tag?.ToString();
                    bool isMatch = _connectionFromInput
                        ? tag is "Success" or "Error" or "Data"
                        : tag == "In";
                    if (isMatch) { found = fe; return HitTestResultBehavior.Stop; }
                }
                return HitTestResultBehavior.Continue;
            },
            new PointHitTestParameters(canvasPos));
        return found;
    }

    private static VisualNode? FindNodeFromSource(DependencyObject? source)
    {
        var current = source;
        while (current is not null)
        {
            if (current is FrameworkElement { DataContext: VisualNode vn }) return vn;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private static bool IsConnectionElement(DependencyObject? source)
    {
        var current = source;
        while (current is not null)
        {
            if (current is FrameworkElement { DataContext: VisualConnectionLine }) return true;
            current = VisualTreeHelper.GetParent(current);
        }
        return false;
    }

    // Глобальная валидация соединения портов:
    // • выход → вход (соблюдается HitTest-логикой, здесь — страховка)
    // • запрещено: один и тот же узел
    // • запрещено: входящее соединение к TriggerRootNode (нет входного порта)
    private static bool CanConnect(VisualNode source, VisualNode target)
    {
        if (source.NodeId == target.NodeId) return false;
        if (target.LogicalNode is TriggerRootNode) return false;
        return true;
    }
}
