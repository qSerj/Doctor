using System.Net;
using PsDoctor.Core.Observation;
using Xunit;

namespace PsDoctor.Core.Tests;

public sealed class RemoteAccessTests
{
    private static readonly IPNetwork[] Сеть = [IPNetwork.Parse("192.168.0.0/24")];

    [Theory]
    [InlineData("192.168.0.1")]
    [InlineData("192.168.0.254")]
    [InlineData("::ffff:192.168.0.17")]
    public void Адрес_из_разрешённой_сети_пускается(string адрес) =>
        Assert.True(RemoteAccess.IsAllowed(IPAddress.Parse(адрес), Сеть));

    [Theory]
    [InlineData("192.168.1.10")]
    [InlineData("192.168.56.1")]
    [InlineData("10.0.0.5")]
    [InlineData("fe80::1")]
    public void Адрес_вне_сети_не_пускается(string адрес) =>
        Assert.False(RemoteAccess.IsAllowed(IPAddress.Parse(адрес), Сеть));

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("::ffff:127.0.0.1")]
    public void Петля_пускается_всегда(string адрес) =>
        Assert.True(RemoteAccess.IsAllowed(IPAddress.Parse(адрес), []));

    [Fact]
    public void Без_адреса_не_пускается() =>
        Assert.False(RemoteAccess.IsAllowed(null, Сеть));
}
