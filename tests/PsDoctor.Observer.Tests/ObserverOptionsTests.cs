using System.Net;
using PsDoctor.Observer;
using Xunit;

namespace PsDoctor.Observer.Tests;

public sealed class ObserverOptionsTests : IDisposable
{
    private readonly string _ключ = Path.Combine(Directory.CreateTempSubdirectory("psdoctor-наблюдатель-").FullName, "observer.key");

    public ObserverOptionsTests() => File.WriteAllText(_ключ, "секрет\n");

    public void Dispose() => Directory.Delete(Path.GetDirectoryName(_ключ)!, recursive: true);

    [Fact]
    public void По_умолчанию_слушает_только_петлю()
    {
        var (options, _) = ObserverOptions.Parse(["--key-file", _ключ]);

        Assert.Equal(IPAddress.Loopback, options!.Address);
        Assert.Equal(ObserverOptions.DefaultPort, options.Port);
        Assert.Equal("секрет", options.Key);
    }

    [Fact]
    public void Сетевой_адрес_задаётся_явно()
    {
        var (options, _) = ObserverOptions.Parse(["--listen", "192.168.56.5:8100", "--allow-remote", "--key-file", _ключ]);

        Assert.Equal(IPAddress.Parse("192.168.56.5"), options!.Address);
        Assert.Equal(8100, options.Port);
    }

    [Fact]
    public void Не_петлевой_адрес_без_allow_remote_отвергается()
    {
        var (options, error) = ObserverOptions.Parse(["--listen", "0.0.0.0:8100", "--key-file", _ключ]);
        var (сКлючом, _) = ObserverOptions.Parse(["--listen", "0.0.0.0:8100", "--allow-remote", "--key-file", _ключ]);

        Assert.Null(options);
        Assert.Contains("--allow-remote", error!, StringComparison.Ordinal);
        Assert.Equal(IPAddress.Any, сКлючом!.Address);
    }

    [Fact]
    public void Петля_allow_remote_не_требует()
    {
        var (options, _) = ObserverOptions.Parse(["--listen", "127.0.0.1:8101", "--key-file", _ключ]);

        Assert.Equal(IPAddress.Loopback, options!.Address);
        Assert.Equal(8101, options.Port);
    }

    [Fact]
    public void Без_файла_ключа_не_запускается()
    {
        var (options, error) = ObserverOptions.Parse(["--listen", "192.168.56.5:8100", "--allow-remote"]);

        Assert.Null(options);
        Assert.NotNull(error);
    }

    [Fact]
    public void Пустой_ключ_не_принимается()
    {
        File.WriteAllText(_ключ, "  \n");

        var (options, _) = ObserverOptions.Parse(["--key-file", _ключ]);

        Assert.Null(options);
    }

    [Theory]
    [InlineData("8100")]
    [InlineData("localhost:8100")]
    [InlineData("192.168.56.5:порт")]
    public void Неразборчивый_адрес_отвергается(string адрес)
    {
        var (options, _) = ObserverOptions.Parse(["--listen", адрес, "--key-file", _ключ]);

        Assert.Null(options);
    }
}
