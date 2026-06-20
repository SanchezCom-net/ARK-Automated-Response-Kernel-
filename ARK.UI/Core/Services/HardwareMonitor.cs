using System.IO;
using System.Runtime.InteropServices;

namespace ARK.UI.Core.Services;

public static class HardwareMonitor
{
    private static volatile bool _cudaAvailable;
    private static volatile bool _cudaCached;

    public static bool IsCudaAvailable()
    {
        if (_cudaCached) return _cudaAvailable;
        _cudaAvailable = DetectCuda();
        _cudaCached    = true;
        return _cudaAvailable;
    }

    // Сбрасывает кэш и перепроверяет наличие CUDA (вызывается GPU Watchdog).
    public static bool RefreshCuda()
    {
        _cudaCached    = false;
        _cudaAvailable = DetectCuda();
        _cudaCached    = true;
        return _cudaAvailable;
    }

    private static bool DetectCuda()
    {
        var cudaDll = Path.Combine(AppContext.BaseDirectory, "ggml-cuda.dll");
        if (!File.Exists(cudaDll)) return false;

        try
        {
            if (!NativeLibrary.TryLoad(cudaDll, out var handle)) return false;
            NativeLibrary.Free(handle);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
