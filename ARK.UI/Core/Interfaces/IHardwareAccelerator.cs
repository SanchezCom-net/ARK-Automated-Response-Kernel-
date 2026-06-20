namespace ARK.UI.Core.Interfaces;

public interface IHardwareAccelerator
{
    bool    IsGpuAccelerationAvailable { get; }
    bool    IsCudaAvailable            { get; }
    bool    IsDirectMlAvailable        { get; }
    bool    IsRocmAvailable            { get; }
    string? PrimaryGpuName             { get; }

    Task RefreshAsync(CancellationToken ct = default);

    /// <summary>
    /// Адаптивное ожидание готовности CUDA: повторяет RefreshAsync до maxAttempts раз
    /// с паузой delayMilliseconds между попытками.
    /// Fast path: возвращает true немедленно при первом успешном обнаружении CUDA.
    /// </summary>
    Task<bool> WaitForCudaAsync(int maxAttempts, int delayMilliseconds, CancellationToken ct = default);
}
