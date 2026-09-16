using PsDoctor.Core.Format;
using PsDoctor.Core.Media;
using PsDoctor.Core.Model;
using PsDoctor.Core.Rules;
using PsDoctor.Infrastructure;
using Xunit;
using Учёт = PsDoctor.Core.Inventory.Inventory;

namespace PsDoctor.Core.Tests;

/// <summary>
/// Критерий Э2: на боевых проектах правила называют находки с идентификаторами, адресами и
/// числами, а не общими словами. Сами числа лежат рядом с проектами.
/// </summary>
public sealed class BattleRulesTests
{
    [Fact]
    public void Числа_срабатываний_на_боевых_проектах_сходятся()
    {
        var checkedAny = false;

        foreach (var (path, label, expected) in Projects())
        {
            if (!expected.FindingRules.Any())
            {
                continue;
            }

            checkedAny = true;
            var findings = Run(path);

            foreach (var rule in expected.FindingRules)
            {
                var actual = findings.Count(f => f.RuleId == rule);

                Assert.True(
                    actual == expected.ExpectedFindings(rule),
                    $"{label}: правило «{rule}» сработало {actual} раз, ожидалось {expected.ExpectedFindings(rule)}");
            }
        }

        Assert.True(checkedAny || !BattleProject.IsCalibrated(), "рядом с боевыми проектами нет ни одного ожидаемого числа срабатываний");
    }

    [Fact]
    public void Каждая_находка_несёт_адрес_и_хоть_одно_число()
    {
        foreach (var (path, label, _) in Projects())
        {
            foreach (var finding in Run(path))
            {
                Assert.False(
                    string.IsNullOrEmpty(finding.Address.StableKey),
                    label + ": находка «" + finding.RuleId + "» без адреса");

                // Находка без чисел ничем не отличается от общих слов, а общих слов доктор не говорит.
                Assert.True(
                    finding.Numbers.Count > 0,
                    label + ": находка «" + finding.RuleId + "» без единого числа");
            }
        }
    }

    [Fact]
    public void Ни_одна_находка_на_боевом_материале_порога_не_проходит()
    {
        // Порогов нет ни у одного правила, поэтому код возврата остаётся нулевым.
        // Это решённое поведение: правила считаются и пишутся, а числа для порогов набираются работой.
        foreach (var (path, label, _) in Projects())
        {
            var passed = Run(path).Count(f => f.PassedThreshold);

            Assert.True(passed == 0, label + ": порог прошли " + passed);
        }
    }

    [Fact]
    public void Чужой_корень_находится_в_обоих_боевых_проектах()
    {
        // Оба проекта пришли с чужих машин, и это видно по шапке независимо от разговора.
        foreach (var (path, label, _) in Projects())
        {
            Assert.True(
                Run(path).Any(f => f.RuleId == "foreign-root"),
                label + ": чужой корень не найден, хотя проект чужого происхождения");
        }
    }

    [Fact]
    public void Уменьшение_картинок_снимает_заметную_долю_распакованных_пикселей()
    {
        // Главный рычаг продукта. Проверяется не порогом, а тем, что выигрыш вообще есть
        // и что он не превышает самой суммы — иначе где-то ошибка знака или двойной счёт.
        foreach (var (path, label, _) in Projects())
        {
            if (BattleProject.FindMediaRoot(path) is null)
            {
                continue;
            }

            var inventory = Build(path);
            var saving = Run(path)
                .Where(f => f.RuleId == "oversized-stills")
                .Sum(f => f.Numbers["savingBytes"]);

            Assert.True(saving > 0, label + ": уменьшать нечего");
            Assert.True(saving < inventory.Media.UnpackedBytes, label + ": выигрыш больше самой суммы");
        }
    }

    private static IEnumerable<(string Path, string Label, ExpectedNumbers Expected)> Projects()
    {
        var paths = BattleProject.FindShowFiles();

        for (var i = 0; i < paths.Count; i++)
        {
            if (ExpectedNumbers.Beside(paths[i]) is { } expected)
            {
                yield return (paths[i], BattleProject.Label(i), expected);
            }
        }
    }

    private static IReadOnlyList<Finding> Run(string showFilePath) =>
        AcceptanceRules.Run(
            Build(showFilePath),
            new RuleContext(RuleSettings.Default, Path.GetDirectoryName(Path.GetFullPath(showFilePath))));

    private static Учёт Build(string showFilePath)
    {
        using var reader = ShowFileEncoding.OpenRead(showFilePath);
        var show = Show.From(ShowFileParser.Parse(reader).Document!);

        var mediaRoot = BattleProject.FindMediaRoot(showFilePath);
        if (mediaRoot is null)
        {
            return Учёт.Build(show, MediaCatalog.Empty);
        }

        var probe = new FileMediaProbe(mediaRoot);
        return Учёт.Build(show, probe.ProbeAll(show.AllLayers.Select(l => l.Image).OfType<MediaReference>()));
    }
}
