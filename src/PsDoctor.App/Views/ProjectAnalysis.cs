using PsDoctor.Core.Format;
using PsDoctor.Core.Media;
using PsDoctor.Core.Model;
using PsDoctor.Core.Rules;
using PsDoctor.Infrastructure;
using Учёт = PsDoctor.Core.Inventory.Inventory;

namespace PsDoctor.App.Views;

internal sealed record ProjectAnalysis(string? Resolution, IReadOnlyList<Finding> Findings)
{
    public static ProjectAnalysis Analyze(string path)
    {
        using var reader = ShowFileEncoding.OpenRead(path);
        var parse = ShowFileParser.Parse(reader);
        if (parse.Document is null || !parse.MagicMatched)
        {
            throw new InvalidDataException("Это не файл проекта ProShow.");
        }

        var show = Show.From(parse.Document);
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var probe = new FileMediaProbe(directory);
        var media = probe.ProbeAll(show.AllLayers.Select(layer => layer.Image).OfType<MediaReference>());
        var inventory = Учёт.Build(show, media);
        var findings = AcceptanceRules.Run(inventory, new RuleContext(RuleSettings.Default, directory));
        var size = show.Header.TargetSize;
        return new ProjectAnalysis(size is null ? null : $"{size.WidthPx} × {size.HeightPx}", findings);
    }
}
