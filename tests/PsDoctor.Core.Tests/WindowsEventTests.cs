using PsDoctor.Core.Observation;
using Xunit;

namespace PsDoctor.Core.Tests;

/// <summary>Отбор событий журнала Windows по именам образов куста ProShow (Э6.2, часть Г).</summary>
public sealed class WindowsEventTests
{
    private static readonly string[] Образы = ["proshow", "fvideo", "device-enc"];

    [Fact]
    public void Имя_образа_ищется_в_любом_параметре_без_учёта_регистра()
    {
        Assert.True(WindowsEvent.Mentions(["ProShow.exe", "9.0.3797.0", "c0000005"], Образы));
        Assert.True(WindowsEvent.Mentions(["APPCRASH", "Not available", "0", "fvideo.exe"], Образы));
        Assert.True(WindowsEvent.Mentions(["device-encp.dll", "N-87344-g5a3b602"], Образы));
        Assert.True(WindowsEvent.Mentions(["explorer.exe", @"C:\Program Files (x86)\Photodex\ProShow Producer\if.dnt"], Образы));
    }

    [Fact]
    public void Чужое_событие_не_о_программе()
    {
        Assert.False(WindowsEvent.Mentions(["notepad.exe", "10.0.19041.1", @"C:\Windows\System32\ntdll.dll"], Образы));
        Assert.False(WindowsEvent.Mentions([], Образы));
    }
}
