namespace ARK.VoskHost;

/// <summary>
/// Детектор шума второго уровня в VoskHost.
/// Если уровень входящего аудио (RMS) превышает 70% от максимума
/// суммарно на протяжении 3-х секунд подряд — переходит в режим Pause.
/// Когда уровень снижается — возвращается в Resume.
/// </summary>
internal sealed class VadMonitor
{
    // 70% от диапазона PCM16 (32767): ~22 937
    private const float  NoiseLevelThreshold = 0.70f;
    private const double NoiseDurationSec    = 3.0;

    private double _cumulativeNoiseSec;
    private bool   _isPaused;

    internal bool IsPaused => _isPaused;

    /// <summary>
    /// Обрабатывает один чанк PCM и возвращает изменение состояния.
    /// </summary>
    /// <param name="pcm">Сэмплы PCM16 (short[])</param>
    /// <param name="chunkDurationSec">Длительность чанка в секундах</param>
    /// <returns>(изменилось ли состояние, новое состояние: "Pause" | "Resume" | "")</returns>
    internal (bool Changed, string NewState) Process(short[] pcm, double chunkDurationSec)
    {
        float rms   = CalculateRms(pcm);
        bool  loud  = rms > NoiseLevelThreshold;

        if (loud)
        {
            _cumulativeNoiseSec += chunkDurationSec;
            if (!_isPaused && _cumulativeNoiseSec >= NoiseDurationSec)
            {
                _isPaused = true;
                return (true, "Pause");
            }
        }
        else
        {
            _cumulativeNoiseSec = 0.0;
            if (_isPaused)
            {
                _isPaused = false;
                return (true, "Resume");
            }
        }
        return (false, string.Empty);
    }

    // RMS нормализован в диапазон [0, 1] относительно максимальной амплитуды PCM16
    private static float CalculateRms(short[] pcm)
    {
        if (pcm.Length == 0) return 0f;
        double sumSq = 0.0;
        foreach (var s in pcm)
            sumSq += (double)s * s;
        return (float)(Math.Sqrt(sumSq / pcm.Length) / 32_767.0);
    }
}
