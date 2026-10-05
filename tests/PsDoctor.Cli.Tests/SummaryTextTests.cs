using PsDoctor.Cli;
using PsDoctor.Core.Observation;
using Xunit;

namespace PsDoctor.Cli.Tests;

/// <summary>Сводка дня словами (Э6.6): главное первой строкой, время — местное время машины.</summary>
public sealed class SummaryTextTests
{
    private static readonly TimeSpan Москва = TimeSpan.FromHours(3);

    private static string[] Текст(DailySummary сводка)
    {
        var writer = new StringWriter();
        SummaryText.Write(сводка, writer);
        return writer.ToString().Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);
    }

    [Fact]
    public void Главное_первым_время_местное_подписи_с_числом()
    {
        // 07:00 UTC — 10:00 по Москве.
        var падение = new WindowsEvent(new DateTimeOffset(2026, 10, 4, 7, 0, 0, TimeSpan.Zero), "Application", "Application Error", 1000, 1,
            ["proshow.exe", "9.0", "0", "all.dnt", "1.0", "0", "0xc0000005", "0x0051b3b5"]);
        var выключение = new WindowsEvent(new DateTimeOffset(2026, 10, 4, 9, 0, 0, Москва), "System", "Microsoft-Windows-Kernel-Power", 41, 2,
            ["239", "0", "0", "0", "0", "0", "0"]);
        var сводка = DailySummaries.Build(new DailySummaryInput(new DateOnly(2026, 10, 4), Москва, true, [], [падение, падение, выключение],
            [], [], null, null, null, null, []));

        var строки = Текст(сводка);

        Assert.Equal("2026-10-04 — день закончен", строки[0]);
        Assert.Equal("Главное: упал ProShow ×2; аварийное выключение ×1; синий экран ×1.", строки[1]);
        Assert.Contains("  proshow: падений 2, зависаний 0 — 10:00, 10:00", строки);
        Assert.Contains("    all.dnt+0x0051b3b5 ×2", строки);
        Assert.Contains(строки, s => s.StartsWith("Машина: аварийные выключения: 09:00 синий экран 239;", StringComparison.Ordinal));
        Assert.Contains(строки, s => s.StartsWith("Нет в данных: слепок окружения; замер места", StringComparison.Ordinal));
    }

    [Fact]
    public void Спокойный_день()
    {
        var сводка = DailySummaries.Build(new DailySummaryInput(new DateOnly(2026, 10, 4), Москва, false, [], [], [], [], null, null,
            new DiskReading(new DateTimeOffset(2026, 10, 4, 12, 0, 0, Москва), new DiskSpace(@"C:\", 200L << 30, 500L << 30), null), 0, []));

        var строки = Текст(сводка);

        Assert.Equal("2026-10-04 — день идёт, сводка неполная", строки[0]);
        Assert.Equal("Главное: день спокойный.", строки[1]);
        Assert.Contains("Место (12:00): системный C:\\ свободно 200 из 500 ГБ; %TEMP% не замерен.", строки);
    }
}
