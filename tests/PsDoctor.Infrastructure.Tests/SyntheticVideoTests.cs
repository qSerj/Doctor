using System.Diagnostics;
using PsDoctor.Core;
using PsDoctor.Core.Format;
using PsDoctor.Core.Media;
using PsDoctor.Core.Model;
using PsDoctor.Core.Rules;
using PsDoctor.Infrastructure;
using Xunit;
using Учёт = PsDoctor.Core.Inventory.Inventory;

namespace PsDoctor.Infrastructure.Tests;

/// <summary>
/// Измерение видео на роликах, которые <c>ffmpeg</c> делает здесь же с заданными кодеком,
/// частотой и кадром. Ролики по секунде-две и крошечные; каталог удаляется после теста.
/// </summary>
/// <remarks>
/// Без <c>ffmpeg</c> эти тесты **падают**, а не пропускаются: они и есть критерий Э1.1,
/// и зелёный прогон без них соврал бы. Инструмент ищется в <c>PSDOCTOR_FFMPEG_DIR</c>, затем в <c>PATH</c>;
/// <c>ffprobe</c> берётся из того же каталога. Кодеки — встроенные в любую сборку ffmpeg.
/// </remarks>
public sealed class SyntheticVideoTests : IDisposable
{
    private readonly string _каталог = Directory.CreateTempSubdirectory("psdoctor-видео-").FullName;

    public void Dispose() => Directory.Delete(_каталог, recursive: true);

    [Fact]
    public void Ролик_mp4_даёт_ровно_заданные_параметры()
    {
        Сделать("a.mp4", "-f", "lavfi", "-i", "testsrc=size=320x240:rate=25:duration=2", "-c:v", "mpeg4");

        var video = Измерить("a.mp4");

        Assert.Equal("mpeg4", video.Codec);
        Assert.Equal(320, video.WidthPx);
        Assert.Equal(240, video.HeightPx);
        Assert.Equal(25_000, video.FrameRateMilliFps);
        Assert.False(video.VariableFrameRate);
        Assert.InRange(video.DurationMs!.Value, 1_900, 2_100);
        Assert.Contains("mp4", video.Container!, StringComparison.Ordinal);
    }

    [Fact]
    public void Ролик_avi_с_частотой_NTSC_даёт_29970()
    {
        Сделать("b.avi", "-f", "lavfi", "-i", "testsrc=size=640x360:rate=30000/1001:duration=2", "-c:v", "mjpeg");

        var video = Измерить("b.avi");

        Assert.Equal("mjpeg", video.Codec);
        Assert.Equal(640, video.WidthPx);
        Assert.Equal(360, video.HeightPx);
        Assert.Equal(29_970, video.FrameRateMilliFps);
        Assert.Equal("avi", video.Container);
    }

    [Fact]
    public void Съёмка_камеры_mts_без_своей_читалки_измеряется_опросчиком()
    {
        // Сигнатуру транспортного потока читалки заголовков не знают; берёт его опросчик по расширению.
        Сделать("c.mts", "-f", "lavfi", "-i", "testsrc=size=720x576:rate=25:duration=1", "-c:v", "mpeg2video", "-f", "mpegts");

        var probe = Опросить("c.mts");

        Assert.Equal(MediaProbeStatus.Ok, probe.Status);
        Assert.Null(probe.FormatId);
        Assert.Equal("mpeg2video", probe.Video!.Codec);
        Assert.Equal(720, probe.Video.WidthPx);
        Assert.Equal(25_000, probe.Video.FrameRateMilliFps);
    }

    [Fact]
    public void Ролик_с_выброшенными_кадрами_называется_переменным()
    {
        // Из каждых десяти кадров остаются пять: заявлено 30, в среднем выходит около 15.
        Сделать(
            "d.mp4",
            "-f", "lavfi", "-i", "testsrc=size=320x240:rate=30:duration=3",
            "-vf", @"select=lt(mod(n\,10)\,5)",
            "-fps_mode", "vfr",
            "-c:v", "mpeg4");

        var video = Измерить("d.mp4");

        Assert.True(video.VariableFrameRate);
        Assert.True(video.AverageFrameRateMilliFps < video.FrameRateMilliFps);
    }

    [Fact]
    public void Размер_кадра_видео_не_попадает_в_размер_картинки()
    {
        // Иначе правила о негабаритных картинках и сумма пикселей начали бы считать видео.
        Сделать("a.mp4", "-f", "lavfi", "-i", "testsrc=size=3840x2160:rate=25:duration=1", "-c:v", "mpeg4");

        var probe = Опросить("a.mp4");

        Assert.Equal(MediaProbeStatus.Ok, probe.Status);
        Assert.Null(probe.Size);
        Assert.Equal(3840, probe.Video!.WidthPx);
    }

    [Fact]
    public void Битый_ролик_остаётся_неопрошенным_с_причиной()
    {
        // Сигнатура mp4 есть, содержимого нет: читалка заголовка его опознаёт, опросчик — нет.
        File.WriteAllBytes(Path.Combine(_каталог, "e.mp4"), [0, 0, 0, 24, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'i', (byte)'s', (byte)'o', (byte)'m', 0, 0, 0, 0]);

        var probe = Опросить("e.mp4");

        Assert.Equal(MediaProbeStatus.NotProbed, probe.Status);
        Assert.Equal("mp4", probe.FormatId);
        Assert.Equal(NotProbedReasons.FfprobeFailed, probe.NotProbedReason);
    }

    [Fact]
    public void Без_опросчика_видео_остаётся_неопрошенным_и_причина_названа()
    {
        // Этому тесту ffmpeg не нужен: опросчика нет по построению.
        File.WriteAllBytes(Path.Combine(_каталог, "e.mp4"), [0, 0, 0, 24, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'i', (byte)'s', (byte)'o', (byte)'m', 0, 0, 0, 0]);

        var probe = new FileMediaProbe(_каталог).Probe(new MediaReference("e.mp4"));

        Assert.Equal(MediaProbeStatus.NotProbed, probe.Status);
        Assert.Equal(NotProbedReasons.NoFfprobe, probe.NotProbedReason);
        Assert.Null(probe.Video);
    }

    [Fact]
    public void Ролики_разной_частоты_дают_находку_о_разнобое()
    {
        Сделать("a.mp4", "-f", "lavfi", "-i", "testsrc=size=320x240:rate=25:duration=1", "-c:v", "mpeg4");
        Сделать("b.avi", "-f", "lavfi", "-i", "testsrc=size=320x240:rate=30000/1001:duration=1", "-c:v", "mjpeg");

        var находка = Assert.Single(Правило("a.mp4", "b.avi"));

        Assert.Equal(2, находка.Numbers["distinctFrameRateCount"]);
        Assert.Equal(2, находка.Numbers["measuredVideoCount"]);
        Assert.Equal(25_000, находка.Numbers["minFrameRateMilliFps"]);
        Assert.Equal(29_970, находка.Numbers["maxFrameRateMilliFps"]);
        Assert.Equal(AddressKind.Show, находка.Address.Kind);
    }

    [Fact]
    public void Ролики_одной_частоты_находки_не_дают()
    {
        Сделать("a.mp4", "-f", "lavfi", "-i", "testsrc=size=320x240:rate=25:duration=1", "-c:v", "mpeg4");
        Сделать("c.mts", "-f", "lavfi", "-i", "testsrc=size=720x576:rate=25:duration=1", "-c:v", "mpeg2video", "-f", "mpegts");

        Assert.Empty(Правило("a.mp4", "c.mts"));
    }

    private IEnumerable<Finding> Правило(params string[] ролики)
    {
        string[] строки =
        [
            ShowFile.Magic,
            "cells=1",
            $"cell[0].nrOfImages={ролики.Length}",
            .. ролики.SelectMany((ролик, i) => new[]
            {
                $"cell[0].images[{i}].objectId={i + 1}",
                $"cell[0].images[{i}].image={ролик}",
            }),
        ];

        using var reader = new StringReader(string.Join("\r\n", строки) + "\r\n");
        var show = Show.From(ShowFileParser.Parse(reader).Document!);
        var catalog = new FileMediaProbe(_каталог, Ffmpeg.Probe()).ProbeAll(show.AllLayers.Select(l => l.Image).OfType<MediaReference>());

        return AcceptanceRules.Run(Учёт.Build(show, catalog), RuleContext.Bare).Where(f => f.RuleId == "mixed-framerate");
    }

    private MediaProbe Опросить(string имя) =>
        new FileMediaProbe(_каталог, Ffmpeg.Probe()).Probe(new MediaReference(имя));

    private VideoParameters Измерить(string имя)
    {
        var probe = Опросить(имя);

        Assert.Equal(MediaProbeStatus.Ok, probe.Status);
        return Assert.IsType<VideoParameters>(probe.Video);
    }

    private void Сделать(string имя, params string[] аргументы)
    {
        var start = new ProcessStartInfo(Ffmpeg.Executable("ffmpeg"))
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        string[] все = ["-v", "error", "-y", .. аргументы, Path.Combine(_каталог, имя)];
        foreach (var аргумент in все)
        {
            start.ArgumentList.Add(аргумент);
        }

        using var process = Process.Start(start)!;
        var ошибки = process.StandardError.ReadToEndAsync();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            Assert.Fail($"ffmpeg не сделал {имя}: {ошибки.Result}");
        }
    }

    /// <summary>Где взять ffmpeg и ffprobe. Не нашёлся — тест падает и говорит, что делать.</summary>
    private static class Ffmpeg
    {
        private static readonly Lazy<string?> Located = new(Find);

        public static string Executable(string name) =>
            Path.Combine(
                Located.Value ?? throw new InvalidOperationException(
                    "ffmpeg не найден: поставьте его в PATH или задайте каталог с ffmpeg и ffprobe в PSDOCTOR_FFMPEG_DIR. "
                    + "Без него критерий Э1.1 не проверяется."),
                OperatingSystem.IsWindows() ? name + ".exe" : name);

        public static FfprobeVideoReader Probe() => new(Executable("ffprobe"));

        private static string? Find()
        {
            var assigned = Environment.GetEnvironmentVariable("PSDOCTOR_FFMPEG_DIR");
            var candidates = string.IsNullOrWhiteSpace(assigned)
                ? (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                : new[] { assigned };

            var suffix = OperatingSystem.IsWindows() ? ".exe" : string.Empty;

            return candidates
                .Select(c => c.Trim('"'))
                .FirstOrDefault(c => File.Exists(Path.Combine(c, "ffmpeg" + suffix)) && File.Exists(Path.Combine(c, "ffprobe" + suffix)));
        }
    }
}
