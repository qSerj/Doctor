using System.Globalization;
using PsDoctor.Core.Observation;

namespace PsDoctor.Infrastructure.Observation;

public enum VideoImportFixOutcome
{
    /// <summary>Галка отмечена, копия прежнего файла лежит в папке Doctor.</summary>
    Fixed,

    /// <summary>Галка уже отмечена: менять нечего.</summary>
    AlreadySet,

    /// <summary>ProShow запущен: он переписывает <c>proshow.cfg</c> сам, правка пропала бы. Ничего не тронуто.</summary>
    ProgramRunning,

    /// <summary>Действующий файл неизвестен или настройки в нём нет. Ничего не тронуто: галку отмечают руками.</summary>
    ConfigUnknown,

    /// <summary>Файл не прочёлся или не записался — например, лежит рядом с программой без прав. Ничего не тронуто.</summary>
    NotWritable,
}

/// <param name="Config">Действующий файл; <c>null</c> — неизвестен.</param>
/// <param name="Backup">Копия файла до правки; только у <see cref="VideoImportFixOutcome.Fixed"/>.</param>
public sealed record VideoImportFixResult(VideoImportFixOutcome Outcome, string? Config, string? Backup = null, string? Error = null);

/// <summary>
/// Рецепт эпизода <c>qtime-loop</c> (Э4.4): отметить в ProShow галку «Avoid using DirectShow when possible» —
/// <c>prefDShowUseFFMPEG</c> с <c>0</c> на <c>1</c> в том <c>proshow.cfg</c>, который программа читает. Видео пойдёт через
/// встроенный FFmpeg, как у программы по умолчанию. Правится один байт на месте; перед правкой — копия файла.
/// </summary>
public sealed class ProShowVideoImportFix
{
    /// <summary>Значение отмеченной галки.</summary>
    public const string Checked = "1";

    private readonly Func<string?> effectiveConfig;
    private readonly string backupDirectory;
    private readonly Func<bool> isProgramRunning;
    private readonly Func<DateTime> now;

    /// <param name="effectiveConfig">Действующий <c>proshow.cfg</c> закрытой программы — спрашивается при каждом действии.</param>
    /// <param name="backupDirectory">Куда кладётся копия файла до правки.</param>
    public ProShowVideoImportFix(Func<string?> effectiveConfig, string backupDirectory, Func<bool> isProgramRunning, Func<DateTime>? now = null)
    {
        ArgumentNullException.ThrowIfNull(effectiveConfig);
        ArgumentException.ThrowIfNullOrEmpty(backupDirectory);
        ArgumentNullException.ThrowIfNull(isProgramRunning);
        this.effectiveConfig = effectiveConfig;
        this.backupDirectory = backupDirectory;
        this.isProgramRunning = isProgramRunning;
        this.now = now ?? (() => DateTime.Now);
    }

    /// <summary>Рецепт для ProShow этой машины: файл — как в слепке окружения, копия — в <c>%LOCALAPPDATA%\PsDoctor\backup</c>.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static ProShowVideoImportFix ForProgram(string programPath, Func<bool> isProgramRunning) =>
        new(() => EnvironmentSnapshotReader.LocateConfig(programPath).Effective,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PsDoctor", "backup"),
            isProgramRunning);

    /// <summary>Значение галки в действующем файле; файла нет, он не прочёлся или настройки нет — <c>null</c>.</summary>
    public string? CurrentValue()
    {
        try
        {
            return effectiveConfig() is { } config ? ProShowConfig.Value(File.ReadAllBytes(config), ProShowConfig.DShowUseFfmpeg) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public VideoImportFixResult Apply()
    {
        if (isProgramRunning())
        {
            return new(VideoImportFixOutcome.ProgramRunning, null);
        }
        if (effectiveConfig() is not { } config)
        {
            return new(VideoImportFixOutcome.ConfigUnknown, null);
        }
        byte[] content;
        try
        {
            content = File.ReadAllBytes(config);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new(VideoImportFixOutcome.NotWritable, config, Error: e.Message);
        }
        if (ProShowConfig.Value(content, ProShowConfig.DShowUseFfmpeg) == Checked)
        {
            return new(VideoImportFixOutcome.AlreadySet, config);
        }
        if (ProShowConfig.WithValue(content, ProShowConfig.DShowUseFfmpeg, Checked) is not { } changed)
        {
            return new(VideoImportFixOutcome.ConfigUnknown, config);
        }

        // Копия — до правки и в своей папке: рядом с программой у монтажёра может не быть прав на запись.
        var backup = Path.Combine(backupDirectory,
            $"{ProShowConfig.FileName}.{now().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}");
        // Временный файл рядом с настоящим: подмена в пределах одной папки не оставит полузаписанного файла.
        var temporary = config + ".psdoctor.tmp";
        try
        {
            Directory.CreateDirectory(backupDirectory);
            File.Copy(config, backup, overwrite: true);
            File.WriteAllBytes(temporary, changed);
            File.Move(temporary, config, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            TryDelete(temporary);
            return new(VideoImportFixOutcome.NotWritable, config, Error: e.Message);
        }
        return new(VideoImportFixOutcome.Fixed, config, backup);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Не удалился — не мешает: программа читает только proshow.cfg.
        }
    }
}
