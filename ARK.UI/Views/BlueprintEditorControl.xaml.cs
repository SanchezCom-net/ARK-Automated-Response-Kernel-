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
    private bool             _connectionIsCustomData;
    private bool             _connectionIsBlackBox;
    private bool             _connectionFromInput;   // true = drag стартовал с "In"/"InData" порта
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
        if (origin is FrameworkElement { Tag: string tag }
            && tag is "Success" or "Error" or "Data" or "CustomData" or "BlackBox")
        {
            var node = FindNodeFromSource(origin);
            if (node is null) return;
            BeginConnectionDraw(node, tag, pos);
            e.Handled = true;
            return;
        }

        // Клик по входному порту → обратное перетаскивание (Toggle).
        if (origin is FrameworkElement { Tag: "In" or "InData" } inFe)
        {
            var node = FindNodeFromSource(origin);
            if (node is null) return;
            bool isDataInput = inFe.Tag?.ToString() == "InData";
            BeginConnectionDrawFromInput(node, pos, isDataInput);
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
            // Snap: поиск только СОВМЕСТИМЫХ портов по типу (Signal vs Data).
            _snapTarget = FindSnapTarget(pos);
            var endPos  = pos;
            if (_snapTarget is not null)
            {
                var snapNode = FindNodeFromSource(_snapTarget);
                if (snapNode is not null)
                {
                    if (_connectionFromInput)
                    {
                        // Обратный drag: из Input → ищем Output
                        var tag = _snapTarget.Tag?.ToString() ?? "Success";
                        var (tx, ty) = tag switch
                        {
                            "Error"      => snapNode.ErrorPortCenter,
                            "Data"       => snapNode.DataPortCenter,
                            "CustomData" => snapNode.CustomDataPortCenter,
                            "BlackBox"   => snapNode.BlackBoxPortCenter,
                            _            => snapNode.SuccessPortCenter
                        };
                        endPos = new WpfPoint(tx, ty);
                    }
                    else
                    {
                        // Прямой drag: из Output → ищем Input
                        var (tx, ty) = _connectionIsData
                            ? snapNode.DataInPortCenter
                            : snapNode.TriggerInPortCenter;
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
                    // Обратное: стартовали с "In"/"InData" → финишируем на выходном порту
                    var outputNode = FindNodeFromSource(snap);
                    if (outputNode is not null && CanConnect(outputNode, _connectionSource))
                    {
                        var tag       = snap.Tag?.ToString() ?? "Success";
                        bool isErr    = tag == "Error";
                        bool isData   = tag is "Data" or "CustomData" or "BlackBox";
                        bool isCustom = tag == "CustomData";
                        bool isBlack  = tag == "BlackBox";
                        vm?.ConnectNodes(outputNode.NodeId, _connectionSource.NodeId,
                                         isErr, isData, isCustom, isBlack);
                    }
                }
                else
                {
                    // Прямое: стартовали с выходного порта → финишируем на "In"/"InData"
                    var targetNode = FindNodeFromSource(snap);
                    if (targetNode is not null && CanConnect(_connectionSource, targetNode))
                        vm?.ConnectNodes(_connectionSource.NodeId, targetNode.NodeId,
                                         _connectionIsError, _connectionIsData,
                                         _connectionIsCustomData, _connectionIsBlackBox);
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

    private void BeginConnectionDrawFromInput(VisualNode targetNode, WpfPoint pos, bool isData = false)
    {
        _isDrawingConnection  = true;
        _connectionSource     = targetNode;
        _connectionFromInput  = true;
        _connectionIsError    = false;
        _connectionIsData     = isData;
        _connectionIsCustomData = false;
        _connectionIsBlackBox   = false;

        var (ox, oy) = isData ? targetNode.DataInPortCenter : targetNode.TriggerInPortCenter;
        _connectionOrigin = new WpfPoint(ox, oy);

        PreviewPath.Stroke = isData
            ? new SolidColorBrush(WpfColor.FromArgb(0xAA, 0xD3, 0xD3, 0xD3))
            : new SolidColorBrush(WpfColor.FromArgb(0xAA, 0xD4, 0xAF, 0x37));
        PreviewPath.Visibility = Visibility.Visible;

        UpdatePreviewPath(_connectionOrigin, pos);
        EditorCanvas.CaptureMouse();
    }

    private void BeginConnectionDraw(VisualNode source, string sourceTag, WpfPoint pos)
    {
        _isDrawingConnection  = true;
        _connectionSource     = source;
        _connectionFromInput  = false;
        _connectionIsError    = sourceTag == "Error";
        _connectionIsData     = sourceTag is "Data" or "CustomData" or "BlackBox";
        _connectionIsCustomData = sourceTag == "CustomData";
        _connectionIsBlackBox   = sourceTag == "BlackBox";

        var (ox, oy) = sourceTag switch
        {
            "Error"      => source.ErrorPortCenter,
            "Data"       => source.DataPortCenter,
            "CustomData" => source.CustomDataPortCenter,
            "BlackBox"   => source.BlackBoxPortCenter,
            _            => source.SuccessPortCenter
        };
        _connectionOrigin = new WpfPoint(ox, oy);

        PreviewPath.Stroke = sourceTag switch
        {
            "Data" or "CustomData" => new SolidColorBrush(WpfColor.FromArgb(0xAA, 0xD3, 0xD3, 0xD3)),
            "BlackBox"             => new SolidColorBrush(WpfColor.FromArgb(0xAA, 0x5B, 0x9B, 0xD5)),
            "Error"                => new SolidColorBrush(WpfColor.FromArgb(0xAA, 0xD3, 0x2F, 0x2F)),
            _                     => new SolidColorBrush(WpfColor.FromArgb(0xAA, 0xD4, 0xAF, 0x37))
        };
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
        _snapTarget             = null;
        _isDrawingConnection    = false;
        _connectionSource       = null;
        _connectionIsError      = false;
        _connectionIsData       = false;
        _connectionIsCustomData = false;
        _connectionIsBlackBox   = false;
        _connectionFromInput    = false;
        PreviewPath.Visibility  = Visibility.Collapsed;
        PreviewPath.Data        = null;
        EditorCanvas.ReleaseMouseCapture();
    }

    // ══ Вспомогательные методы ══════════════════════════════════════════

    // Ищет СОВМЕСТИМЫЙ порт под курсором (магнитное притяжение V3):
    // Signal-провод → только Signal-порты; Data-провод → только Data-порты.
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
                    var tag = fe.Tag?.ToString();
                    bool isMatch = _connectionFromInput
                        // обратный drag: из Data In → только Data Out; из Trigger In → Success/Error
                        ? (_connectionIsData
                            ? tag is "Data" or "CustomData" or "BlackBox"
                            : tag is "Success" or "Error")
                        // прямой drag: из Data Out → только In Data; из Signal Out → только Trigger In
                        : (_connectionIsData
                            ? tag == "InData"
                            : tag == "In");
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

    // Глобальная валидация соединения портов (страховка поверх HitTest-фильтрации):
    // • запрещено: один и тот же узел
    // • запрещено: входящее к TriggerRootNode (нет входного порта)
    // • запрещено: MacroPolicyNode как источник или цель (пассивная нода, нет портов)
    private static bool CanConnect(VisualNode source, VisualNode target)
    {
        if (source.NodeId == target.NodeId) return false;
        if (target.LogicalNode is TriggerRootNode) return false;
        if (source.LogicalNode is MacroPolicyNode) return false;
        if (target.LogicalNode is MacroPolicyNode) return false;
        return true;
    }
}
