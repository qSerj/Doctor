using PsDoctor.Core.Media;
using PsDoctor.Infrastructure;
using Xunit;

namespace PsDoctor.Infrastructure.Tests;

/// <summary>
/// Разбор ответа опросчика проверяется строкой: так он не зависит от того, есть ли ffprobe на машине
/// и какой он версии. Ответы собраны по образцу настоящих, лишние поля выброшены.
/// </summary>
public sealed class FfprobeOutputTests
{
    [Fact]
    public void Ровный_ролик_даёт_все_параметры()
    {
        var reading = FfprobeVideoReader.ParseOutput("""
            {
              "streams": [
                {
                  "codec_name": "h264", "profile": "High", "codec_type": "video",
                  "width": 1920, "height": 1080,
                  "r_frame_rate": "25/1", "avg_frame_rate": "25/1",
                  "duration": "12.480000", "bit_rate": "8000000",
                  "disposition": { "attached_pic": 0 }
                }
              ],
              "format": { "format_name": "mov,mp4,m4a,3gp,3g2,mj2", "duration": "12.500000", "bit_rate": "8100000" }
            }
            """);

        var video = Assert.IsType<VideoParameters>(reading.Video);
        Assert.Null(reading.NotProbedReason);
        Assert.Equal("h264", video.Codec);
        Assert.Equal("High", video.Profile);
        Assert.Equal("mov,mp4,m4a,3gp,3g2,mj2", video.Container);
        Assert.Equal(1920, video.WidthPx);
        Assert.Equal(1080, video.HeightPx);
        Assert.Equal(25_000, video.FrameRateMilliFps);
        Assert.Equal(25_000, video.AverageFrameRateMilliFps);
        Assert.False(video.VariableFrameRate);

        // Длительность и битрейт берутся у потока, а не у контейнера.
        Assert.Equal(12_480, video.DurationMs);
        Assert.Equal(8_000_000, video.BitRateBitsPerSecond);
    }

    [Fact]
    public void Частота_NTSC_считается_в_тысячных_кадра()
    {
        var reading = FfprobeVideoReader.ParseOutput("""
            { "streams": [ { "codec_type": "video", "r_frame_rate": "30000/1001", "avg_frame_rate": "30000/1001" } ] }
            """);

        Assert.Equal(29_970, reading.Video!.FrameRateMilliFps);
        Assert.False(reading.Video.VariableFrameRate);
    }

    [Fact]
    public void Средняя_частота_далеко_от_заявленной_значит_переменную()
    {
        var reading = FfprobeVideoReader.ParseOutput("""
            { "streams": [ { "codec_type": "video", "r_frame_rate": "30/1", "avg_frame_rate": "2689/100" } ] }
            """);

        Assert.Equal(30_000, reading.Video!.FrameRateMilliFps);
        Assert.Equal(26_890, reading.Video.AverageFrameRateMilliFps);
        Assert.True(reading.Video.VariableFrameRate);
    }

    [Fact]
    public void Расхождение_в_сотые_доли_переменной_частотой_не_считается()
    {
        // Ровный ролик, у которого длительность округлена: средняя чуть меньше заявленной.
        var reading = FfprobeVideoReader.ParseOutput("""
            { "streams": [ { "codec_type": "video", "r_frame_rate": "30/1", "avg_frame_rate": "29985/1000" } ] }
            """);

        Assert.False(reading.Video!.VariableFrameRate);
    }

    [Fact]
    public void Неизвестная_частота_не_выдумывается()
    {
        var reading = FfprobeVideoReader.ParseOutput("""
            { "streams": [ { "codec_type": "video", "codec_name": "mjpeg", "r_frame_rate": "0/0", "avg_frame_rate": "0/0" } ] }
            """);

        Assert.Equal("mjpeg", reading.Video!.Codec);
        Assert.Null(reading.Video.FrameRateMilliFps);
        Assert.Null(reading.Video.AverageFrameRateMilliFps);
        Assert.Null(reading.Video.VariableFrameRate);
    }

    [Fact]
    public void Шкала_времени_контейнера_частотой_не_считается()
    {
        // Так опросчик отвечает на wmv3 в asf: заявлено 1000 кадров/с, средней нет.
        var reading = FfprobeVideoReader.ParseOutput("""
            { "streams": [ { "codec_type": "video", "codec_name": "wmv3", "r_frame_rate": "1000/1", "avg_frame_rate": "0/0" } ] }
            """);

        Assert.Equal("wmv3", reading.Video!.Codec);
        Assert.Null(reading.Video.FrameRateMilliFps);
        Assert.Null(reading.Video.VariableFrameRate);
    }

    [Fact]
    public void Высокая_частота_при_известной_средней_остаётся_частотой()
    {
        var reading = FfprobeVideoReader.ParseOutput("""
            { "streams": [ { "codec_type": "video", "r_frame_rate": "1000/1", "avg_frame_rate": "1000/1" } ] }
            """);

        Assert.Equal(1_000_000, reading.Video!.FrameRateMilliFps);
    }

    [Fact]
    public void Без_длительности_у_потока_берётся_длительность_контейнера()
    {
        var reading = FfprobeVideoReader.ParseOutput("""
            {
              "streams": [ { "codec_type": "video", "duration": "N/A", "r_frame_rate": "25/1" } ],
              "format": { "format_name": "mpegts", "duration": "7.040000", "bit_rate": "15000000" }
            }
            """);

        Assert.Equal(7_040, reading.Video!.DurationMs);
        Assert.Equal(15_000_000, reading.Video.BitRateBitsPerSecond);
        Assert.Equal("mpegts", reading.Video.Container);
    }

    [Fact]
    public void Обложка_роликом_не_считается()
    {
        var reading = FfprobeVideoReader.ParseOutput("""
            {
              "streams": [ { "codec_type": "video", "codec_name": "png", "disposition": { "attached_pic": 1 } } ],
              "format": { "format_name": "mov,mp4,m4a,3gp,3g2,mj2" }
            }
            """);

        Assert.Null(reading.Video);
        Assert.Equal(NotProbedReasons.NoVideoStream, reading.NotProbedReason);
    }

    [Fact]
    public void Контейнер_без_видеопотока_называется_так()
    {
        var reading = FfprobeVideoReader.ParseOutput("""{ "streams": [], "format": { "format_name": "mov,mp4,m4a,3gp,3g2,mj2" } }""");

        Assert.Equal(NotProbedReasons.NoVideoStream, reading.NotProbedReason);
    }

    [Theory]
    [InlineData("")]
    [InlineData("не json")]
    [InlineData("[1, 2]")]
    public void Ответ_который_не_разобрать_это_сбой_опросчика(string output)
    {
        var reading = FfprobeVideoReader.ParseOutput(output);

        Assert.Null(reading.Video);
        Assert.Equal(NotProbedReasons.FfprobeFailed, reading.NotProbedReason);
    }
}
