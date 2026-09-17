using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace PsDoctor.Observer;

public static class ObserverHost
{
    /// <summary>Собирает хост с маршрутами. Запуск — у вызывающего: Program и тесты.</summary>
    public static WebApplication Build(ObserverOptions options)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(options.Address, options.Port));

        var app = builder.Build();
        var expected = Encoding.UTF8.GetBytes(options.Key);

        // Проверка ключа стоит перед всеми маршрутами: открытых маршрутов у наблюдателя нет.
        app.Use(async (context, next) =>
        {
            if (!HasKey(context.Request.Headers.Authorization.ToString(), expected))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.Headers.WWWAuthenticate = "Bearer";
                return;
            }
            await next(context);
        });

        var build = BuildInfo.Of(typeof(ObserverHost).Assembly);
        app.MapGet("/health", () => Results.Json(build));

        return app;
    }

    private static bool HasKey(string header, byte[] expected)
    {
        const string scheme = "Bearer ";
        if (!header.StartsWith(scheme, StringComparison.Ordinal))
            return false;
        var presented = Encoding.UTF8.GetBytes(header[scheme.Length..].Trim());
        return CryptographicOperations.FixedTimeEquals(presented, expected);
    }
}

/// <summary>
/// Версия и коммит сборки. Коммит берётся из суффикса InformationalVersion, который SDK
/// дописывает из SourceRevisionId; скрипт лаборатории передаёт его явно, с пометкой -dirty.
/// </summary>
public sealed record BuildInfo(string Version, string? Commit)
{
    public static BuildInfo Of(Assembly assembly)
    {
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
        var plus = informational.IndexOf('+');
        return plus < 0
            ? new BuildInfo(informational, null)
            : new BuildInfo(informational[..plus], informational[(plus + 1)..]);
    }
}
