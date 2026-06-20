namespace ARK.UI.Core.Nodes;

public enum WinProcessAction { Kill, CheckRunning, Restart }

public enum WinPowerAction { Sleep, Shutdown, Restart, Lock, MonitorOff }

public enum SmartWaitType { UntilWindowActive, UntilPixelColor }

public enum WinWindowAction
{
    // Одиночное окно
    Minimize, Maximize, Restore, Close, Focus, MoveAndResize, CheckActive,
    // Все окна / системная сетка
    MinimizeAll, RestoreAll, TileVertical, TileHorizontal, Cascade
}
