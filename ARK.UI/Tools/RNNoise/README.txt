RNNoise — нативный нейросетевой шумодав голосового тракта ARK
================================================================

СТАТУС: rnnoise.dll ОТСУТСТВУЕТ. Аудио-тракт работает в режиме Bypass
(сырой поток без шумоподавления), о чём ARK пишет в лог при старте:
  "[RNNoise] КРИТИЧЕСКАЯ ОШИБКА: ... Источник ошибки: rnnoise.dll не найдена..."

КАК АКТИВИРОВАТЬ:
1. Возьмите x64-сборку rnnoise.dll (Windows, 64-bit), например:
   - собрать из исходников https://github.com/xiph/rnnoise (cmake, Release x64)
   - или взять готовую x64 DLL из доверенного релиза
2. Положите файл сюда: ARK.UI\Tools\RNNoise\rnnoise.dll
3. Пересоберите проект — csproj автоматически скопирует DLL в корень output
   (условие Exists('Tools\RNNoise\rnnoise.dll') уже настроено).
4. После запуска в логе появится:
   "[RNNoise] Шумоподавление успешно активировано в аудио-тракте (16kHz Mono)."

ТРЕБОВАНИЯ К DLL:
- разрядность строго x64 (иначе BadImageFormatException);
- экспорты C API: rnnoise_create, rnnoise_destroy, rnnoise_process_frame.
