using ARK.UI.Core.Models;

namespace ARK.UI.Core.Interfaces;

public interface ISpeechTriggerService
{
    // bool = activationNameDetected: гейткипер подтвердил имя активации и отсёк его
    event Func<string, bool, Task>? SpeechRecognized;
    // Вызывается из потока NAudio каждые ~33 мс, передаёт нормализованный RMS [0..1]
    event Action<double>? LevelUpdated;
    bool IsRunning    { get; }
    bool IsMonitoring { get; }
    // Предзагружает модель Whisper в память (VRAM/RAM) без запуска захвата аудио.
    // Вызывается StartupManager при старте — делает активацию голоса мгновенной.
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync();
    Task SwitchModelAsync(string modelPath, CancellationToken cancellationToken = default);
    Task SwitchEngineAsync(SpeechEngineMode engine, string language, CancellationToken cancellationToken = default);
    // Лёгкий режим: WaveIn без Whisper — только для VU-Meter
    Task StartMonitoringAsync(CancellationToken cancellationToken = default);
    Task StopMonitoringAsync();

    // Задача завершается после первого вызова StartAsync или StartMonitoringAsync
    // (вне зависимости от результата). Используется для защиты от Race Condition:
    // UI-действия вызывают await WhenReadyAsync() перед обращением к состоянию сервиса.
    Task WhenReadyAsync();
}
