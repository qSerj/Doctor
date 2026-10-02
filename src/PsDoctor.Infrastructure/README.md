# PsDoctor.Infrastructure

Всё, что ядру запрещено: файлы, кодировки, заголовки медиа, процессы, задания Windows, окна, журнал событий. Windows нужен почти везде.

## Граница

Здесь живёт работа с системой, но не решения о ней: числа снимаются, а считает по ним [ядро](../PsDoctor.Core/README.md). Классы с вызовами Win32 помечаются `[SupportedOSPlatform("windows")]`, а сами вызовы прячутся за `OperatingSystem.IsWindows()` — иначе `CA1416` при `TreatWarningsAsErrors` ломает сборку на Linux.

**Кодировка — единственная дверь.** `ShowFileEncoding` регистрирует провайдер кодовых страниц один раз; открывать файл шоу мимо него нельзя, иначе кириллические пути читаются как мусор и живые файлы объявляются потерянными.

## С чего читать

| Хочу понять | Начинать с |
| --- | --- |
| откуда берутся разрешения картинок | `MediaHeaderReader.cs` — свои читалки PNG, JPEG, PSD |
| как измеряется видео | `FfprobeVideoReader.cs` — внешний `ffprobe`, разбор его ответа отдельно от запуска; вызывает его `FileMediaProbe.cs` |
| где установленный Doctor держит настройки, ключ и журналы | `Installation/InstalledLayout.cs` |
| как запускается или находится чужая программа | `Observation/ProShowLauncher.cs`, затем `Observation/ProgramRun.cs` или `Observation/AttachedRun.cs` |
| где на машине ProShow — один порядок у сторожа и App | `Observation/ProShowLocator.cs` |
| как видно куст процессов | `Observation/Win32Job.cs` |
| как читаются окна и диалоги | `Observation/WindowWatcher.cs`, нажатие — `Observation/UiAutomation.cs` |
| что складывается в один сеанс | `Observation/ProgramRun.cs`, для пассивного сеанса `Observation/AttachedRun.cs` |
| что за машина: факт `environment` и слепок окружения | `Observation/MachineEnvironment.cs`, `Observation/EnvironmentSnapshotReader.cs`; файл и его версия — `Observation/FileDescription.cs` |
| рецепт эпизода `qtime-loop`: галка в `proshow.cfg` | `Observation/ProShowVideoImportFix.cs`; какой файл действует — `EnvironmentSnapshotReader.LocateConfig`, тот же выбор, что в слепке |

## Чего здесь нет

Вердиктов. Гигиена сеанса даёт разницу служебных файлов, а не «программа намусорила»; журнал Windows даёт события, а не «упала».

Полного декодирования медиа — читаются только заголовки. Своего разбора видеоконтейнеров тоже нет: видео измеряет `ffprobe`, а без него видео остаётся «не опрашивали» с причиной, а не «неизвестно».

## Тесты

`tests/PsDoctor.Infrastructure.Tests`. Тесты измерения видео делают ролики `ffmpeg` и без него **падают**, а не пропускаются: `ffmpeg` и `ffprobe` ищутся в `PSDOCTOR_FFMPEG_DIR`, затем в `PATH`. Тесты с Win32 на не-Windows молча не выполняются, а на Linux зелёный прогон о них **не говорит ничего**: задание, процессы и окна проверяются на стенде, куда код доставляет `lab/observer.sh`.
