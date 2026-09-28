using PsDoctor.Core.Observation;
using Xunit;

namespace PsDoctor.Core.Tests;

/// <summary>Когда мастер просит ждать, а когда предлагает завершить ProShow (Э6.2, часть Б). Снимки подложены, Win32 нет.</summary>
public sealed class ProShowHangWatchTests
{
    private static readonly DateTime Создан = new(2026, 9, 28, 10, 0, 0, DateTimeKind.Utc);

    private static TimeSpan С(double секунд) => TimeSpan.FromSeconds(секунд);

    private static ProShowSnapshot Снимок(ProShowState состояние, double cpu = 0, long io = 0, bool рендер = false,
        params ProShowProcess[] ещё) =>
        new(состояние, рендер, [new ProShowProcess(ProShowRole.Main, 100, Создан, С(cpu), io), .. ещё]);

    /// <summary>Снимки раз в секунду с <paramref name="от"/> по <paramref name="до"/>; процессор и ввод-вывод растут на заданное в секунду.</summary>
    private static void Кормить(ProShowHangWatch наблюдение, ProShowState состояние, int от, int до, double cpuВСекунду = 0, long ioВСекунду = 0)
    {
        for (var t = от; t <= до; t++)
        {
            наблюдение.Observe(С(t), Снимок(состояние, cpuВСекунду * t, ioВСекунду * t));
        }
    }

    [Fact]
    public void Статус_не_отвечает_только_после_30_секунд_подряд()
    {
        var наблюдение = new ProShowHangWatch();
        Кормить(наблюдение, ProShowState.Responding, 0, 10);
        Кормить(наблюдение, ProShowState.Hung, 11, 40);

        Assert.False(наблюдение.NotResponding(С(40)));
        Assert.True(наблюдение.NotResponding(С(41)));
    }

    [Fact]
    public void Без_окна_статус_не_меняется()
    {
        var наблюдение = new ProShowHangWatch();
        Кормить(наблюдение, ProShowState.NoWindow, 0, 120);

        Assert.False(наблюдение.NotResponding(С(120)));
    }

    [Fact]
    public void Первые_60_секунд_окна_только_ждать_даже_при_долгом_зависании()
    {
        var наблюдение = new ProShowHangWatch();
        Кормить(наблюдение, ProShowState.Hung, 0, 400);

        Assert.Equal(HangAdvice.Wait, наблюдение.Advice(С(400), opened: С(350)));
        Assert.Equal(HangAdvice.OfferTerminate, наблюдение.Advice(С(400), opened: С(340)));
    }

    [Fact]
    public void Тихое_зависание_завершать_не_раньше_трёх_минут()
    {
        var наблюдение = new ProShowHangWatch();
        Кормить(наблюдение, ProShowState.Hung, 0, 179);

        Assert.Equal(HangAdvice.Wait, наблюдение.Advice(С(179), opened: С(0)));
        Кормить(наблюдение, ProShowState.Hung, 180, 180);
        Assert.Equal(HangAdvice.OfferTerminate, наблюдение.Advice(С(180), opened: С(0)));
    }

    [Fact]
    public void Занятый_но_не_отвечающий_ProShow_завершить_не_предлагается()
    {
        var процессор = new ProShowHangWatch();
        Кормить(процессор, ProShowState.Hung, 0, 1000, cpuВСекунду: 0.5);
        var диск = new ProShowHangWatch();
        Кормить(диск, ProShowState.Hung, 0, 1000, ioВСекунду: 1024 * 1024);

        Assert.Equal(HangAdvice.Busy, процессор.Advice(С(1000), opened: С(0)));
        Assert.Equal(HangAdvice.Busy, диск.Advice(С(1000), opened: С(0)));
    }

    [Fact]
    public void Почти_ноль_работы_считается_тишиной()
    {
        var наблюдение = new ProShowHangWatch();
        Кормить(наблюдение, ProShowState.Hung, 0, 300, cpuВСекунду: 0.01, ioВСекунду: 1000);

        Assert.False(наблюдение.Active());
        Assert.Equal(HangAdvice.OfferTerminate, наблюдение.Advice(С(300), opened: С(0)));
    }

    [Fact]
    public void Новый_процесс_куста_это_работа()
    {
        var наблюдение = new ProShowHangWatch();
        Кормить(наблюдение, ProShowState.Hung, 0, 299);
        наблюдение.Observe(С(300), Снимок(ProShowState.Hung, ещё: new ProShowProcess(ProShowRole.Decoder, 200, Создан.AddMinutes(5), TimeSpan.Zero, 0)));

        Assert.True(наблюдение.Active());
        Assert.Equal(HangAdvice.Busy, наблюдение.Advice(С(300), opened: С(0)));
    }

    [Fact]
    public void Рендер_не_завершается_никогда()
    {
        var наблюдение = new ProShowHangWatch();
        for (var t = 0; t <= 3600; t++)
        {
            наблюдение.Observe(С(t), Снимок(ProShowState.Hung, рендер: true));
        }

        Assert.Equal(HangAdvice.Busy, наблюдение.Advice(С(3600), opened: С(0)));
    }

    [Fact]
    public void Без_окна_завершать_после_минуты()
    {
        var наблюдение = new ProShowHangWatch();
        Кормить(наблюдение, ProShowState.NoWindow, 0, 59, cpuВСекунду: 1);

        Assert.Equal(HangAdvice.Wait, наблюдение.Advice(С(59), opened: С(-100)));
        Кормить(наблюдение, ProShowState.NoWindow, 60, 60, cpuВСекунду: 1);
        Assert.Equal(HangAdvice.OfferTerminate, наблюдение.Advice(С(60), opened: С(-100)));
    }

    [Fact]
    public void Смена_состояния_начинает_отсчёт_заново()
    {
        var наблюдение = new ProShowHangWatch();
        Кормить(наблюдение, ProShowState.Hung, 0, 170);
        Кормить(наблюдение, ProShowState.Responding, 171, 171);
        Кормить(наблюдение, ProShowState.Hung, 172, 300);

        Assert.Equal(HangAdvice.Wait, наблюдение.Advice(С(300), opened: С(0)));
        Assert.Equal(HangAdvice.None, new ProShowHangWatch().Advice(С(300), opened: С(0)));
    }

    [Fact]
    public void Завершается_ProShow_а_без_него_декодер_но_не_кодировщик()
    {
        var декодер = new ProShowProcess(ProShowRole.Decoder, 200, Создан, TimeSpan.Zero, 0);
        var кодировщик = new ProShowProcess(ProShowRole.Encoder, 300, Создан, TimeSpan.Zero, 0);
        var неизвестный = new ProShowProcess(ProShowRole.Main, 400, null, TimeSpan.Zero, 0);

        Assert.Equal([100], Снимок(ProShowState.Hung, ещё: декодер).Targets.Select(p => p.ProcessId));
        Assert.Equal([200], new ProShowSnapshot(ProShowState.NoWindow, false, [декодер, кодировщик]).Targets.Select(p => p.ProcessId));
        Assert.Empty(new ProShowSnapshot(ProShowState.NoWindow, false, [неизвестный]).Targets);
    }
}
