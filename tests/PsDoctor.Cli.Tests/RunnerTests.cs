using System.Text.Json;
using PsDoctor.Cli;
using PsDoctor.Core;
using PsDoctor.Infrastructure;
using Xunit;

namespace PsDoctor.Cli.Tests;

/// <summary>
/// Прогон проверяется вызовом, а не запуском процесса: <see cref="Runner.Run"/> принимает
/// оба потока и момент времени, поэтому коды возврата и вывод видны напрямую.
/// </summary>
public sealed class RunnerTests : IDisposable
{
    private static readonly DateTimeOffset Момент = new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);

    private readonly string _двор = Directory.CreateTempSubdirectory("psdoctor-тест-").FullName;

    public void Dispose() => Directory.Delete(_двор, recursive: true);

    /// <summary>Кладёт файл шоу в CP1251 — той же дверью, которой доктор его читает.</summary>
    private string ФайлШоу(string имя, params string[] строки)
    {
        var путь = Path.Combine(_двор, имя);
        File.WriteAllText(
            путь,
            ShowFile.Magic + "\r\n" + string.Join("\r\n", строки) + "\r\n",
            ShowFileEncoding.Cp1251);

        return путь;
    }

    private static (int Code, string Out, string Err) Run(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = Runner.Run(args, stdout, stderr, Момент);

        return (code, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public void Разобранный_проект_без_находок_даёт_ноль()
    {
        var (code, stdout, stderr) = Run(ФайлШоу("шоу.psh", "cells=1", "cell[0].time=5000"));

        Assert.Equal(ExitCodes.Clean, code);
        Assert.Empty(stderr);

        var json = JsonDocument.Parse(stdout).RootElement;
        Assert.Equal("psdoctor.report", json.GetProperty("schema").GetString());
        Assert.Equal(5000, json.GetProperty("inventory").GetProperty("slides").GetProperty("totalTimeMs").GetInt32());
    }

    [Fact]
    public void Не_файл_шоу_даёт_два()
    {
        var путь = Path.Combine(_двор, "чужое.psh");
        File.WriteAllText(путь, "title=ProShow Slideshow\r\n", ShowFileEncoding.Cp1251);

        var (code, stdout, stderr) = Run(путь);

        Assert.Equal(ExitCodes.NotAShowFile, code);
        Assert.NotEmpty(stderr);

        // Отчёт всё равно выдаётся: прогон пачки должен получить строку на каждый вход.
        Assert.False(JsonDocument.Parse(stdout).RootElement.GetProperty("source").GetProperty("magicMatched").GetBoolean());
    }

    [Fact]
    public void Отсутствующий_файл_даёт_два_а_не_единицу()
    {
        // Единица означает «разобран, есть находки», и путать её с «файла нет» нельзя.
        var (code, stdout, stderr) = Run(Path.Combine(_двор, "нет-такого.psh"));

        Assert.Equal(ExitCodes.NotAShowFile, code);
        Assert.Empty(stdout);
        Assert.NotEmpty(stderr);
    }

    [Fact]
    public void Отсутствие_аргументов_даёт_три()
    {
        var (code, stdout, stderr) = Run();

        Assert.Equal(ExitCodes.Environment, code);
        Assert.Empty(stdout);
        Assert.NotEmpty(stderr);
    }

    [Fact]
    public void Неизвестный_ключ_даёт_три()
    {
        var (code, _, stderr) = Run("--чего-то-такого");

        Assert.Equal(ExitCodes.Environment, code);
        Assert.NotEmpty(stderr);
    }

    [Fact]
    public void Отчёт_идёт_в_stdout_а_диагностика_в_stderr()
    {
        // Одна посторонняя строка в stdout ломает машинного потребителя.
        var (_, stdout, _) = Run(ФайлШоу("шоу.psh", "cells=0"), "--pretty");

        Assert.StartsWith("{", stdout.TrimStart());
        JsonDocument.Parse(stdout);
    }

    [Fact]
    public void Пачка_даёт_строку_на_файл_и_максимум_по_кодам()
    {
        var годный = ФайлШоу("первый.psh", "cells=1", "cell[0].time=1000");
        var негодный = Path.Combine(_двор, "второй.psh");
        File.WriteAllText(негодный, "не шоу\r\n", ShowFileEncoding.Cp1251);
        var третий = ФайлШоу("третий.psh", "cells=1", "cell[0].time=2000");

        var (code, stdout, _) = Run(годный, негодный, третий);

        Assert.Equal(ExitCodes.NotAShowFile, code);

        var строки = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, строки.Length);

        // Порядок — как в аргументах: сравнивать прогоны иначе невозможно.
        Assert.Equal(1000, Время(строки[0]));
        Assert.Null(Время(строки[1]));
        Assert.Equal(2000, Время(строки[2]));
    }

    [Fact]
    public void Ошибка_на_одном_файле_не_останавливает_остальные()
    {
        var (_, stdout, _) = Run(Path.Combine(_двор, "нет.psh"), ФайлШоу("есть.psh", "cells=1", "cell[0].time=7000"));

        Assert.Equal(7000, Время(stdout.Trim()));
    }

    [Fact]
    public void Обезличивание_с_одной_солью_повторяется()
    {
        // Имя заведомо неповторимое: обычное слово нашлось бы в собственном словаре единиц отчёта.
        const string имяФайла = "звенидетствозолотое";
        var путь = ФайлШоу("шоу.psh", "cells=1", "cell[0].nrOfImages=1", "cell[0].images[0].image=image/" + имяФайла + ".png");

        var (_, первый, _) = Run(путь, "--anonymize-salt", "проба");
        var (_, второй, _) = Run(путь, "--anonymize-salt", "проба");

        Assert.Equal(первый, второй);
        Assert.DoesNotContain(имяФайла, первый, StringComparison.Ordinal);
    }

    [Fact]
    public void Соль_без_значения_даёт_три()
    {
        var (code, _, stderr) = Run(ФайлШоу("шоу.psh", "cells=0"), "--anonymize-salt");

        Assert.Equal(ExitCodes.Environment, code);
        Assert.NotEmpty(stderr);
    }

    [Fact]
    public void Справка_и_версия_идут_в_stdout_и_дают_ноль()
    {
        var (codeHelp, помощь, _) = Run("--help");
        var (codeVersion, версия, _) = Run("--version");

        Assert.Equal(ExitCodes.Clean, codeHelp);
        Assert.Contains("psdoctor", помощь, StringComparison.Ordinal);
        Assert.Equal(ExitCodes.Clean, codeVersion);
        Assert.NotEmpty(версия.Trim());
    }

    [Fact]
    public void Кириллица_в_путях_читается_правильной_кодировкой()
    {
        // Ровно тот сбой, ради которого продукт затевался: под чужой кодировкой
        // живой файл объявляется потерянным.
        var (_, stdout, _) = Run(ФайлШоу(
            "шоу.psh",
            "cells=1",
            "cell[0].nrOfImages=1",
            "cell[0].images[0].image=image/ёлка.png"));

        var медиа = JsonDocument.Parse(stdout).RootElement
            .GetProperty("inventory").GetProperty("media").GetProperty("items")[0];

        Assert.Equal("image/ёлка.png", медиа.GetProperty("reference").GetString());
    }

    private static int? Время(string строка)
    {
        var slides = JsonDocument.Parse(строка).RootElement.GetProperty("inventory");

        return slides.ValueKind == JsonValueKind.Null
            ? null
            : slides.GetProperty("slides").GetProperty("totalTimeMs").GetInt32();
    }
}
