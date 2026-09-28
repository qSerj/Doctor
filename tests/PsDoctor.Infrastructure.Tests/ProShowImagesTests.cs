using PsDoctor.Core.Observation;
using PsDoctor.Infrastructure.Observation;
using Xunit;

namespace PsDoctor.Infrastructure.Tests;

public sealed class ProShowImagesTests
{
    [Fact]
    public void Роль_процесса_по_имени_образа()
    {
        Assert.Equal(ProShowRole.Main, ProShowImages.RoleOf("ProShow"));
        Assert.Equal(ProShowRole.Decoder, ProShowImages.RoleOf("fvideo"));
        Assert.Equal(ProShowRole.Encoder, ProShowImages.RoleOf("device-enc"));
        Assert.Equal(ProShowRole.Encoder, ProShowImages.RoleOf("device-encp"));
        Assert.Null(ProShowImages.RoleOf("explorer"));
    }
}
