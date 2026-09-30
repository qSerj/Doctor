using PsDoctor.Infrastructure.Observation;
using Xunit;

namespace PsDoctor.Infrastructure.Tests;

/// <summary>Описание файла — одно на факт <c>environment</c>, слепок и загрузку модуля (Э6.3); работает и не на Windows.</summary>
public sealed class FileDescriptionTests : IDisposable
{
    private readonly string _каталог = Directory.CreateTempSubdirectory("psdoctor-файл-").FullName;

    public void Dispose() => Directory.Delete(_каталог, recursive: true);

    [Fact]
    public void Нет_файла_записан_как_отсутствующий()
    {
        var путь = Path.Combine(_каталог, "нет.dll");

        var файл = FileDescription.Describe(путь);

        Assert.Equal(путь, файл.Path);
        Assert.False(файл.Exists);
        Assert.Null(файл.Version);
        Assert.Null(файл.Size);
        Assert.Null(FileDescription.Version(путь));
    }

    [Fact]
    public void Файл_без_ресурса_версии_даёт_размер_и_время()
    {
        var путь = Path.Combine(_каталог, "модуль.dll");
        File.WriteAllBytes(путь, new byte[12]);

        var файл = FileDescription.Describe(путь);

        Assert.True(файл.Exists);
        Assert.True(string.IsNullOrEmpty(файл.Version));
        Assert.Equal(12, файл.Size);
        Assert.Equal(File.GetLastWriteTimeUtc(путь), файл.Written!.Value.UtcDateTime);
    }
}
