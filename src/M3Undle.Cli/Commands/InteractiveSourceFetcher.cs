using M3Undle.Core.Net;
using Spectre.Console;

namespace M3Undle.Cli.Commands;

internal sealed class InteractiveSourceFetcher
{
    private readonly HttpClient _httpClient;
    private readonly TextWriter _diagnostics;
    private readonly SourceFetcher _sourceFetcher;

    public InteractiveSourceFetcher(HttpClient httpClient, TextWriter diagnostics)
    {
        _httpClient = httpClient;
        _diagnostics = diagnostics;
        _sourceFetcher = new SourceFetcher(httpClient, diagnostics);
    }

    public async Task<string> GetStringWithProgressAsync(string source, IAnsiConsole console, CancellationToken cancellationToken)
    {
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            using var response = await HttpFetcher.SendAndValidateAsync(
                _httpClient, uri, _diagnostics, cancellationToken,
                HttpCompletionOption.ResponseHeadersRead);

            var total = response.Content.Headers.ContentLength ?? -1L;
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var ms = new MemoryStream();
            var buffer = new byte[8192];
            long read = 0;

            var result = string.Empty;
            await console.Progress()
                .AutoClear(true)
                .Columns(CreateColumns(total))
                .StartAsync(async ctx =>
                {
                    var task = ctx.AddTask("Downloading", maxValue: total > 0 ? total : double.MaxValue);
                    if (total <= 0)
                    {
                        task.IsIndeterminate = true;
                        task.Description = "Downloading (0 B)";
                    }

                    int bytesRead;
                    while ((bytesRead = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
                    {
                        ms.Write(buffer, 0, bytesRead);
                        read += bytesRead;

                        task.Increment(bytesRead);

                        if (total > 0)
                            task.Value = read;
                        else
                            task.Description = $"Downloading ({FormatBytes(read)})";
                    }

                    task.StopTask();
                    result = System.Text.Encoding.UTF8.GetString(ms.ToArray());
                });

            return result;
        }

        return await _sourceFetcher.GetStringAsync(source, cancellationToken);
    }

    private static ProgressColumn[] CreateColumns(long total)
    {
        if (total > 0)
        {
            return
            [
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new DownloadedColumn(),
                new TransferSpeedColumn()
            ];
        }

        return
        [
            new TaskDescriptionColumn(),
            new SpinnerColumn(),
            new TransferSpeedColumn()
        ];
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024)
            return $"{bytes} B";

        string[] units = ["KB", "MB", "GB", "TB"];
        double value = bytes;
        var unitIndex = 0;
        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return $"{value:0.##} {units[unitIndex]}";
    }
}
