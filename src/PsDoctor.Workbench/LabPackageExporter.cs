using System.Security.Cryptography;
using System.Text.Json;
using PsDoctor.Core.Observation;
using PsDoctor.Observer.Client;

namespace PsDoctor.Workbench;

/// <summary>Собирает закрытый сеанс в переносимый пакет лабораторной проверки.</summary>
public sealed class LabPackageExporter
{
    public async Task<string> ExportAsync(
        ObserverClient client,
        string session,
        string scenario,
        string showPath,
        string exchangeDirectory,
        SessionArtifact? artifact,
        bool includeRaw,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrEmpty(session);
        ArgumentException.ThrowIfNullOrEmpty(exchangeDirectory);

        var checks = Path.Combine(exchangeDirectory, "checks");
        Directory.CreateDirectory(checks);
        var id = "workbench-" + session;
        var final = Path.Combine(checks, id);
        if (Directory.Exists(final))
            throw new IOException($"пакет уже существует: {final}");

        var building = Path.Combine(checks, ".building-" + id);
        if (Directory.Exists(building))
            throw new IOException($"незавершённый пакет уже существует: {building}");
        Directory.CreateDirectory(Path.Combine(building, "results"));

        try
        {
            var metadata = new PackageMetadata(id, session, showPath, scenario, DateTimeOffset.UtcNow,
                artifact is null ? "missing" : "selected", includeRaw);
            await File.WriteAllTextAsync(Path.Combine(building, "request.md"),
                $"# Лабораторный рендер\n\nСеанс: `{session}`\n\nПроект: `{showPath}`\n\n```text\n{scenario}\n```\n",
                cancellationToken).ConfigureAwait(false);
            await WriteJsonAsync(Path.Combine(building, "run.json"), metadata, cancellationToken).ConfigureAwait(false);

            var factsPath = Path.Combine(building, "results", "facts.jsonl");
            await using (var facts = new FileStream(factsPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await client.DownloadFactsAsync(session, facts, cancellationToken).ConfigureAwait(false);
            }

            if (artifact is not null)
            {
                var resultPath = Path.Combine(building, "results", SafeFileName(artifact.Name));
                await using var output = new FileStream(resultPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                await client.DownloadArtifactAsync(session, artifact.Id, output, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                await VerifyAsync(resultPath, artifact, cancellationToken).ConfigureAwait(false);
            }

            var rawState = "not-requested";
            if (includeRaw)
            {
                var rawPath = Path.Combine(building, "results", "etw.jsonl");
                try
                {
                    await using var raw = new FileStream(rawPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                        64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    await client.DownloadRawAsync(session, raw, cancellationToken).ConfigureAwait(false);
                    rawState = "saved";
                }
                catch (ObserverException e) when (e.Error?.Error == ObserverErrors.NoRaw)
                {
                    File.Delete(rawPath);
                    rawState = "unavailable";
                }
            }

            metadata = metadata with { Raw = rawState };
            await WriteJsonAsync(Path.Combine(building, "run.json"), metadata, cancellationToken).ConfigureAwait(false);
            var manifest = await ManifestAsync(building, metadata, cancellationToken).ConfigureAwait(false);
            await WriteJsonAsync(Path.Combine(building, "manifest.json"), manifest, cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(building, "exit.txt"), "0\n", cancellationToken).ConfigureAwait(false);
            Directory.Move(building, final);
            return final;
        }
        catch
        {
            try { await File.WriteAllTextAsync(Path.Combine(building, "FAILED.json"), "{\"status\":\"failed\"}\n"); }
            catch (IOException) { }
            throw;
        }
    }

    private static async Task VerifyAsync(string path, SessionArtifact expected, CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (info.Length != expected.Bytes)
            throw new InvalidDataException("размер скачанного MP4 не совпал с данными наблюдателя");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var digest = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
        if (!digest.Equals(expected.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("контрольная сумма скачанного MP4 не совпала");
    }

    private static async Task<Manifest> ManifestAsync(string root, PackageMetadata metadata, CancellationToken cancellationToken)
    {
        var files = new List<ManifestFile>();
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                     .Where(path => !Path.GetFileName(path).Equals("manifest.json", StringComparison.OrdinalIgnoreCase)))
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var digest = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
            files.Add(new ManifestFile(Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/'),
                new FileInfo(path).Length, digest));
        }
        return new Manifest(metadata, files);
    }

    private static Task WriteJsonAsync<T>(string path, T value, CancellationToken cancellationToken) =>
        File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }), cancellationToken);

    private static string SafeFileName(string name)
    {
        var file = Path.GetFileName(name);
        return string.IsNullOrWhiteSpace(file) ? "render.mp4" : file;
    }

    private sealed record PackageMetadata(string Id, string Session, string ShowPath, string Scenario,
        DateTimeOffset ExportedAtUtc, string Artifact, bool RawRequested, string Raw = "not-requested");

    private sealed record Manifest(PackageMetadata Package, IReadOnlyList<ManifestFile> Files);

    private sealed record ManifestFile(string Path, long Bytes, string Sha256);
}
