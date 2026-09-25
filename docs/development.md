# Сборка и проверки

Статус: **Согласовано**. Команды здесь выполняет владелец; агент называет их в задании проверки ([договор обмена](../lab/README.md)). Что на какой машине возможно — [окружения](environments.md).

## Команды

```bash
dotnet test PsDoctor.slnx                                              # все тесты
dotnet test PsDoctor.slnx --filter "FullyQualifiedName~Кодировка_1251" # один тест по имени
dotnet run --project src/PsDoctor.Cli -- "путь/к/проекту.psh"          # машинный отчёт в stdout
dotnet run --project src/PsDoctor.Cli -- "проект.psh" --pretty         # то же, с отступами
dotnet run --project src/PsDoctor.Cli -- "проект.psh" --anonymize      # обезличенный срез
dotnet run --project src/PsDoctor.App                                  # Avalonia, только Windows
dotnet run --project src/PsDoctor.Workbench                            # пульт наблюдателя, где угодно
lab/observer.sh                                                        # собрать наблюдатель, доставить на стенд, прогнать тесты
install/package.sh [каталог]                                           # пакет установки Doctor для машины монтажёра
dotnet run --project src/PsDoctor.Cli -- observe health                # версия и коммит наблюдателя на стенде
dotnet run --project src/PsDoctor.Cli -- observe run сценарий.txt --follow  # прогон сценария, факты в stdout
```

Коды возврата обеих ролей CLI и переменные `observe` — в [README Cli](../src/PsDoctor.Cli/README.md). Тестов пять проектов xUnit в `tests/`.

## Правила сборки

- `TreatWarningsAsErrors` включён на весь solution: любое предупреждение ломает сборку.
- Версии пакетов живут только в `Directory.Packages.props`; в `csproj` — `PackageReference` без `Version`.
- `global.json` закрепляет **линейку** SDK, а не точную версию: `10.0.100` с `rollForward: latestFeature` берёт старшую установленную 10.0.x. Точную версию не ставить — на машинах разработки SDK разные, и точное закрепление ломает сборку везде, кроме одной.
- Корень репозитория — текущий checkout; скрипты находят его через Git или от своего расположения, абсолютные пути не зашиваются.
