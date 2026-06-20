using System.Runtime.InteropServices;

namespace ARK.UI.Core.Audio;

/// <summary>
/// Нейросетевой шумодав RNNoise поверх нативной rnnoise.dll (x64).
/// Тракт ARK (16 кГц / 16-бит / моно, чанки ~33 мс) адаптируется к нативному формату RNNoise
/// (48 кГц, кадры 480 сэмплов = 10 мс): линейный апсемплинг ×3 → rnnoise_process_frame →
/// децимация ×3 с усреднением. Нарезка на кадры идёт через внутренние FIFO без аллокаций
/// на чанк (Zero-Allocation), алгоритмическая задержка — единичные миллисекунды.
/// Недоступность DLL — graceful degradation: IsAvailable=false, ProcessInPlace становится no-op.
/// </summary>
public sealed partial class RnNoiseDenoiser : IDisposable
{
    private const int FrameSize16k = 160;   // 10 мс при 16 кГц
    private const int FrameSize48k = 480;   // нативный кадр RNNoise (10 мс при 48 кГц)
    private const int FifoCapacity = 8192;  // с запасом: чанк WaveInEvent 33 мс ≈ 528 сэмплов

    // Ищется рядом с exe: rnnoise.dll (x64). Кладётся в Tools\RNNoise\ — csproj копирует в output.
    [LibraryImport("rnnoise", EntryPoint = "rnnoise_create")]
    private static partial nint RnnoiseCreate(nint model);

    [LibraryImport("rnnoise", EntryPoint = "rnnoise_destroy")]
    private static partial void RnnoiseDestroy(nint state);

    [LibraryImport("rnnoise", EntryPoint = "rnnoise_process_frame")]
    private static unsafe partial float RnnoiseProcessFrame(nint state, float* output, float* input);

    private nint _state;
    private bool _disposed;

    // FIFO-кадрирование: вход копится до полных кадров 160, выход отдаётся ровно по размеру чанка
    private readonly short[] _inFifo  = new short[FifoCapacity];
    private readonly short[] _outFifo = new short[FifoCapacity];
    private int _inCount;
    private int _outCount;

    private readonly float[] _frame48In  = new float[FrameSize48k];
    private readonly float[] _frame48Out = new float[FrameSize48k];
    private float _lastSample;   // непрерывность линейной интерполяции между кадрами

    public bool IsAvailable { get; }

    /// <summary>Причина сбоя инициализации (null при успехе) — для детальной диагностики в логах.</summary>
    public Exception? InitializationError { get; }

    public RnNoiseDenoiser()
    {
        try
        {
            _state      = RnnoiseCreate(nint.Zero);
            IsAvailable = _state != nint.Zero;

            // DLL загрузилась, но нативное состояние не выделено (сбой malloc внутри rnnoise)
            if (!IsAvailable)
                InitializationError = new InvalidOperationException(
                    "rnnoise_create вернул NULL — нативное состояние DenoiseState не выделено.");
        }
        catch (Exception ex) when (ex is DllNotFoundException
                                      or EntryPointNotFoundException
                                      or BadImageFormatException
                                      or MarshalDirectiveException)
        {
            // DLL отсутствует / не x64 / несовместимый экспорт — шумодав выключен,
            // тракт работает на сыром потоке (Bypass)
            IsAvailable         = false;
            InitializationError = ex;
        }
    }

    /// <summary>
    /// Очищает PCM16 LE чанк от шума in-place (длина не меняется).
    /// Вызывается из аудиопотока NAudio до расчёта RMS и записи в буфер.
    /// </summary>
    public void ProcessInPlace(byte[] buffer, int count)
    {
        if (!IsAvailable || _disposed || count < 2) return;

        int samples = count / 2;

        // Нештатно крупный чанк — сбрасываем FIFO и пропускаем сырой поток (no-op)
        if (_inCount + samples > _inFifo.Length || _outCount + samples > _outFifo.Length)
        {
            _inCount  = 0;
            _outCount = 0;
            return;
        }

        // PCM16 LE → входной FIFO
        for (int i = 0, b = 0; i < samples; i++, b += 2)
            _inFifo[_inCount + i] = (short)(buffer[b] | (buffer[b + 1] << 8));
        _inCount += samples;

        // Обрабатываем все накопленные полные кадры по 160 сэмплов
        int processed = 0;
        while (_inCount - processed >= FrameSize16k)
        {
            DenoiseFrame(_inFifo.AsSpan(processed, FrameSize16k),
                         _outFifo.AsSpan(_outCount, FrameSize16k));
            processed += FrameSize16k;
            _outCount += FrameSize16k;
        }

        if (processed > 0)
        {
            Array.Copy(_inFifo, processed, _inFifo, 0, _inCount - processed);
            _inCount -= processed;
        }

        // Компенсация кадровой задержки: добиваем фронт тишиной (суммарно ≤10 мс за сессию)
        if (_outCount < samples)
        {
            int deficit = samples - _outCount;
            Array.Copy(_outFifo, 0, _outFifo, deficit, _outCount);
            Array.Clear(_outFifo, 0, deficit);
            _outCount += deficit;
        }

        // Выходной FIFO → PCM16 LE обратно в буфер вызывающего
        for (int i = 0, b = 0; i < samples; i++, b += 2)
        {
            short s       = _outFifo[i];
            buffer[b]     = (byte)s;
            buffer[b + 1] = (byte)(s >> 8);
        }
        Array.Copy(_outFifo, samples, _outFifo, 0, _outCount - samples);
        _outCount -= samples;
    }

    private unsafe void DenoiseFrame(ReadOnlySpan<short> in16k, Span<short> out16k)
    {
        // Апсемплинг ×3 линейной интерполяцией: 160 @ 16 кГц → 480 @ 48 кГц.
        // RNNoise ожидает float в диапазоне 16-битного PCM (без нормализации в [-1..1]).
        var up   = _frame48In.AsSpan();
        float prev = _lastSample;
        for (int i = 0; i < FrameSize16k; i++)
        {
            float cur     = in16k[i];
            float delta   = cur - prev;
            up[i * 3]     = prev + delta * (1f / 3f);
            up[i * 3 + 1] = prev + delta * (2f / 3f);
            up[i * 3 + 2] = cur;
            prev = cur;
        }
        _lastSample = prev;

        fixed (float* pIn  = _frame48In)
        fixed (float* pOut = _frame48Out)
        {
            RnnoiseProcessFrame(_state, pOut, pIn);
        }

        // Децимация ×3 с усреднением тройки — дешёвый anti-aliasing
        var dn = _frame48Out.AsSpan();
        for (int i = 0; i < FrameSize16k; i++)
        {
            float avg = (dn[i * 3] + dn[i * 3 + 1] + dn[i * 3 + 2]) * (1f / 3f);
            out16k[i] = (short)Math.Clamp(avg, short.MinValue, short.MaxValue);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_state != nint.Zero)
        {
            RnnoiseDestroy(_state);
            _state = nint.Zero;
        }
    }
}
