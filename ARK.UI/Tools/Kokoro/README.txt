ARK — Kokoro-82M TTS: Размещение файлов модели
================================================

Требуемая структура папок (относительно ARK.UI.exe):

  Models\TTS\Kokoro\
  ├── kokoro.onnx          — основная модель Kokoro-82M (ONNX-формат)
  ├── vocab.json           — словарь IPA-токенов ({"символ": id, ...})
  └── voices\
      ├── af_sky.bin       — голосовой стиль (256 × float32 LE = 1024 байт)
      ├── af_sarah.bin
      └── ...              — любые дополнительные .bin голоса

------------------------------------------------------------------
Как получить файлы:

1. ONNX-модель и vocab.json:
   Скачайте из репозитория kokoro-onnx:
     https://github.com/thewh1teagle/kokoro-onnx/releases
   Файлы: kokoro-v0_19.onnx → переименуйте в kokoro.onnx
          vocab.json         → оставьте без изменений

2. Голосовые стили (.bin):
   Стили хранятся в оригинальном репозитории Kokoro как .pt (PyTorch).
   Для конвертации в .bin используйте скрипт:

     import torch, struct, pathlib
     src = pathlib.Path("voices")          # папка с .pt файлами
     dst = pathlib.Path("output_voices")   # куда сохранить .bin
     dst.mkdir(exist_ok=True)
     for pt in src.glob("*.pt"):
         t = torch.load(pt, map_location="cpu")
         # t.shape == (1, 256) float32
         vec = t.squeeze().float().numpy()
         (dst / (pt.stem + ".bin")).write_bytes(
             struct.pack(f"<{len(vec)}f", *vec))

   Готовые .pt голоса: https://huggingface.co/hexgrad/Kokoro-82M
   (папка voices/ в репозитории)

------------------------------------------------------------------
Выбор режима в ARK:
  Настройки → ИИ и Сеть → Синтез речи → Режим TTS: Kokoro
  После смены режима список голосов обновляется автоматически.

Примечание:
  ARK использует CPU для ONNX inference. Для GPU ускорения замените
  пакет Microsoft.ML.OnnxRuntime на Microsoft.ML.OnnxRuntime.Gpu
  и раскомментируйте AppendExecutionProvider_CUDA() в KokoroSynthesizer.cs.
