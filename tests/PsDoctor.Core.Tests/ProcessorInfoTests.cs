using PsDoctor.Core.Observation;
using Xunit;

namespace PsDoctor.Core.Tests;

/// <summary>Ревизия микрокода из значения реестра «Update Revision» (Э6.5).</summary>
public sealed class ProcessorInfoTests
{
    [Fact]
    public void У_Intel_ревизия_в_старших_четырёх_байтах()
    {
        Assert.Equal("0x12F", ProcessorInfo.Revision([0, 0, 0, 0, 0x2F, 0x01, 0, 0]));
    }

    [Fact]
    public void Старшие_байты_пусты_ревизия_в_младших()
    {
        Assert.Equal("0x12B", ProcessorInfo.Revision([0x2B, 0x01, 0, 0, 0, 0, 0, 0]));
        Assert.Equal("0x12B", ProcessorInfo.Revision([0x2B, 0x01, 0, 0]));
    }

    [Fact]
    public void Нет_значения_или_ноль_ревизии_нет()
    {
        Assert.Null(ProcessorInfo.Revision(null));
        Assert.Null(ProcessorInfo.Revision([]));
        Assert.Null(ProcessorInfo.Revision([1, 2]));
        Assert.Null(ProcessorInfo.Revision([0, 0, 0, 0, 0, 0, 0, 0]));
    }
}
