using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using Spectre.Console;
using M3Undle.Core.IO;
using M3Undle.Core.M3u;
using M3Undle.Core.Net;
using M3Undle.Core;

namespace M3Undle.Cli.Commands;

public sealed class RunCommand
{
    private const string UngroupedLabel = "(no group)";

    private readonly TextWriter _stdout;
    private readonly TextWriter _stderr;
    private readonly TextWriter _diagnostics;
    private readonly HttpClient _httpClient;
    private readonly PlaylistParser _parser;
    private readonly IAnsiConsole _console;

    public RunCommand(
        TextWriter stdout,
        TextWriter stderr,
        TextWriter diagnostics,
        HttpClient httpClient,
        PlaylistParser parser,
        IAnsiConsole? console = null)
    {
        _stdout = stdout;
        _stderr = stderr;
        _diagnostics = diagnostics;
        _httpClient = httpClient;
        _parser = parser;
        _console = console ?? Spectre.Console.AnsiConsole.Console;
    }

    [RequiresUnreferencedCode("Command execution may use configuration loading with YAML which requires reflection.")]
    [RequiresDynamicCode("Command execution may use configuration loading with YAML which may require dynamic code generation.")]
    public async Task<int> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(context.PlaylistSource))
        {
            throw new CliException("Missing required: --playlist-url or --config with playlist", ExitCodes.ConfigError);
        }

        var epgRequested = !string.IsNullOrEmpty(context.EpgSource);
        var playlistOut = context.PlaylistOutputPath;
        var epgOut = context.EpgOutputPath;

        if (epgRequested && string.IsNullOrEmpty(playlistOut) && string.IsNullOrEmpty(epgOut))
        {
            throw new CliException("When an EPG is requested you must provide --out-playlist, --out-epg, or use '-' for stdout.", ExitCodes.ConfigError);
        }

        var interactive = ShouldUseInteractiveConsole(playlistOut, epgOut, epgRequested);
        if (!interactive && _diagnostics != TextWriter.Null)
        {
            await _diagnostics.WriteLineAsync("[VERBOSE] Suppressing interactive console output because results are streamed to stdout.");
        }

        var fetcher = new SourceFetcher(_httpClient, _diagnostics);
        var playlistContent = interactive
            ? await fetcher.GetStringWithProgressAsync(context.PlaylistSource!, _console, cancellationToken)
            : await fetcher.GetStringAsync(context.PlaylistSource!, cancellationToken);

        var document = await ParsePlaylistAsync(playlistContent, cancellationToken, interactive);
        var allEntries = document.Entries
            .Where(static e => !string.IsNullOrWhiteSpace(e.Url))
            .ToList();

        if (_diagnostics != TextWriter.Null)
        {
            await _diagnostics.WriteLineAsync($"Playlist entries after removing blanks: {allEntries.Count}.");
        }

        if (context.LiveOnly)
        {
            allEntries = allEntries
                .Where(e => LiveClassifier.IsLive(e.Url))
                .ToList();

            if (_diagnostics != TextWriter.Null)
            {
                await _diagnostics.WriteLineAsync($"Live-only filter active. Remaining live entries: {allEntries.Count}.");
            }
        }

        var playlistGroups = allEntries
            .Where(static e => !string.IsNullOrWhiteSpace(e.Group))
            .Select(static e => e.Group!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        GroupSelectionFile.GroupSelection? groupSelection = null;
        if (!string.IsNullOrEmpty(context.GroupsFile))
        {
            groupSelection = await LoadGroupsSelectionAsync(context.GroupsFile!, cancellationToken, interactive);

            if (_diagnostics != TextWriter.Null)
            {
                await _diagnostics.WriteLineAsync($"Loaded {groupSelection.Keep.Count} keep group(s) from {context.GroupsFile}.");
                if (groupSelection.PendingReview.Count > 0)
                {
                    await _diagnostics.WriteLineAsync($"Pending review groups (##): {string.Join(", ", groupSelection.PendingReview.OrderBy(g => g, StringComparer.OrdinalIgnoreCase))}");
                }
            }

            ReportNewAndPendingGroups(context.GroupsFile!, playlistGroups, groupSelection, interactive);
        }

        var filterResult = ApplyGroupFilters(allEntries, groupSelection);

        if (_diagnostics != TextWriter.Null)
        {
            await _diagnostics.WriteLineAsync($"Filtering summary: kept={filterResult.Selected.Count}, dropped-no-group={filterResult.DroppedWithoutGroup}, dropped-by-groups-file={filterResult.DroppedExcluded}.");
        }

        if (filterResult.Selected.Count == 0)
        {
            await _stderr.WriteLineAsync("Warning: no channels matched the provided filters.");
        }

        if (interactive && filterResult.KeptGroups.Count > 0)
        {
            RenderSummaryTable(filterResult.KeptGroups);
        }

        await WritePlaylistWithOptionalStatusAsync(document, filterResult.Selected, playlistOut, cancellationToken, interactive);

        if (epgRequested)
        {
            var epgContent = interactive
                ? await fetcher.GetStringWithProgressAsync(context.EpgSource!, _console, cancellationToken)
                : await fetcher.GetStringAsync(context.EpgSource!, cancellationToken);

            await WriteEpgWithOptionalStatusAsync(epgContent, epgOut, cancellationToken, interactive);
        }

        await _stdout.WriteLineAsync($"Kept {filterResult.Selected.Count} channel(s).");
        return ExitCodes.Success;
    }

    private async Task<PlaylistDocument> ParsePlaylistAsync(string content, CancellationToken cancellationToken, bool interactive)
    {
        if (!interactive)
        {
            return _parser.Parse(content, cancellationToken);
        }

        PlaylistDocument document = null!;
        await _console.Status()
            .StartAsync("Parsing playlist...", async _ =>
            {
                document = await Task.Run(() => _parser.Parse(content, cancellationToken), cancellationToken);
            });
        return document;
    }

    private async Task<GroupSelectionFile.GroupSelection> LoadGroupsSelectionAsync(string path, CancellationToken cancellationToken, bool interactive)
    {
        if (!interactive)
        {
            return await Task.Run(() => GroupSelectionFile.LoadSelection(path), cancellationToken);
        }

        GroupSelectionFile.GroupSelection selection = null!;
        await _console.Status()
            .StartAsync("Loading groups file...", async _ =>
            {
                selection = await Task.Run(() => GroupSelectionFile.LoadSelection(path), cancellationToken);
            });
        return selection;
    }

    private async Task WritePlaylistWithOptionalStatusAsync(
        PlaylistDocument document,
        IReadOnlyList<M3uEntry> entries,
        string? outputPath,
        CancellationToken cancellationToken,
        bool interactive)
    {
        if (!interactive)
        {
            await WritePlaylistAsync(document, entries, outputPath, cancellationToken);
            return;
        }

        await _console.Status()
            .StartAsync("Writing playlist...", _ => WritePlaylistAsync(document, entries, outputPath, cancellationToken));
    }

    private async Task WriteEpgWithOptionalStatusAsync(
        string content,
        string? outputPath,
        CancellationToken cancellationToken,
        bool interactive)
    {
        if (!interactive)
        {
            await WriteEpgAsync(content, outputPath, cancellationToken);
            return;
        }

        await _console.Status()
            .StartAsync("Writing EPG...", _ => WriteEpgAsync(content, outputPath, cancellationToken));
    }

    private void RenderSummaryTable(IReadOnlyDictionary<string, int> keptGroups)
    {
        var table = new Table().Title("[green]Kept groups[/]").AddColumn("Group").AddColumn("Channels");
        foreach (var kvp in keptGroups
                     .OrderByDescending(k => k.Value)
                     .ThenBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
        {
            table.AddRow(Markup.Escape(kvp.Key), kvp.Value.ToString(CultureInfo.InvariantCulture));
        }

        table.Border(TableBorder.Rounded);
        _console.Write(table);
    }

    private static FilterResult ApplyGroupFilters(
        IReadOnlyList<M3uEntry> entries,
        GroupSelectionFile.GroupSelection? groupSelection)
    {
        if (groupSelection is null)
        {
            var selected = new List<M3uEntry>(entries);
            var counts = BuildGroupCounts(selected);
            return new FilterResult(selected, counts, 0, 0);
        }

        var selectedEntries = new List<M3uEntry>();
        var keepSet = groupSelection.Keep;
        var allSet = groupSelection.All;
        var droppedMissingGroup = 0;
        var droppedExcluded = 0;

        foreach (var entry in entries)
        {
            var group = entry.Group;

            if (string.IsNullOrWhiteSpace(group))
            {
                droppedMissingGroup++;
                continue;
            }

            if (allSet.Contains(group) && !keepSet.Contains(group))
            {
                droppedExcluded++;
                continue;
            }

            selectedEntries.Add(entry);
        }

        var groupCounts = BuildGroupCounts(selectedEntries);
        return new FilterResult(selectedEntries, groupCounts, droppedMissingGroup, droppedExcluded);
    }

    private static Dictionary<string, int> BuildGroupCounts(IEnumerable<M3uEntry> entries)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            var key = string.IsNullOrWhiteSpace(entry.Group) ? UngroupedLabel : entry.Group!;
            counts.TryGetValue(key, out var current);
            counts[key] = current + 1;
        }

        return counts;
    }

    private void ReportNewAndPendingGroups(
        string groupsFilePath,
        IReadOnlySet<string> playlistGroups,
        GroupSelectionFile.GroupSelection selection,
        bool interactive)
    {
        var newGroups = playlistGroups
            .Where(group => !selection.All.Contains(group))
            .OrderBy(g => g, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (newGroups.Count > 0)
        {
            if (interactive)
            {
                _console.MarkupLine($"[yellow]Discovered {newGroups.Count} new group(s) not present in {groupsFilePath}:[/]");
                foreach (var group in newGroups)
                {
                    _console.MarkupLine($"  [cyan]{Markup.Escape(group)}[/]");
                }
            }
            else
            {
                _stderr.WriteLine($"Discovered {newGroups.Count} new group(s) not present in {groupsFilePath}:");
                foreach (var group in newGroups)
                {
                    _stderr.WriteLine($"  {group}");
                }
            }
        }

        if (selection.PendingReview.Count == 0)
        {
            return;
        }

        var pendingInPlaylist = selection.PendingReview
            .Where(group => playlistGroups.Contains(group))
            .OrderBy(g => g, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (pendingInPlaylist.Count == 0)
        {
            return;
        }

        if (interactive)
        {
            _console.MarkupLine("[yellow]The following new group(s) are marked with '##' and were included. Edit your groups file to drop them:[/]");
            foreach (var group in pendingInPlaylist)
            {
                _console.MarkupLine($"  [cyan]{Markup.Escape(group)}[/]");
            }
        }
        else
        {
            _stderr.WriteLine("The following new group(s) are marked with '##' and were included:");
            foreach (var group in pendingInPlaylist)
            {
                _stderr.WriteLine($"  {group}");
            }
        }
    }

    private static bool ShouldUseInteractiveConsole(string? playlistOut, string? epgOut, bool epgRequested)
    {
        var playlistToStdout = string.IsNullOrEmpty(playlistOut) || playlistOut == "-";
        var epgToStdout = epgRequested && (string.IsNullOrEmpty(epgOut) || epgOut == "-");
        var outputRedirected = Console.IsOutputRedirected || Console.IsErrorRedirected;
        
        // Use interactive mode only if:
        // 1. Output is not going to stdout (we're writing to files)
        // 2. AND stdout/stderr are NOT redirected (we're in a real terminal)
        return !(playlistToStdout || epgToStdout) && !outputRedirected;
    }

    private async Task WritePlaylistAsync(
        PlaylistDocument document,
        IReadOnlyList<M3uEntry> entries,
        string? outputPath,
        CancellationToken cancellationToken)
    {
        var lines = document.EnumerateLines(entries).ToList();
        if (string.IsNullOrEmpty(outputPath) || outputPath == "-")
        {
            foreach (var line in lines)
            {
                await _stdout.WriteLineAsync(line);
            }
        }
        else
        {
            var fullPath = Path.GetFullPath(outputPath);
            if (_diagnostics != TextWriter.Null)
            {
                await _diagnostics.WriteLineAsync($"[VERBOSE] Writing playlist to: {fullPath}");
            }
            
            await TextFileWriter.WriteAtomicAsync(outputPath, lines, cancellationToken);
            await _stdout.WriteLineAsync($"Playlist written to {fullPath}");
        }
    }

    private async Task WriteEpgAsync(string content, string? outputPath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(outputPath) || outputPath == "-")
        {
            await _stdout.WriteAsync(content);
            if (!content.EndsWith('\n'))
            {
                await _stdout.WriteLineAsync();
            }
        }
        else
        {
            var fullPath = Path.GetFullPath(outputPath);
            if (_diagnostics != TextWriter.Null)
            {
                await _diagnostics.WriteLineAsync($"[VERBOSE] Writing EPG to: {fullPath}");
            }
            
            await TextFileWriter.WriteAtomicTextAsync(outputPath, content, cancellationToken);
            await _stdout.WriteLineAsync($"EPG written to {fullPath}");
        }
    }

    private sealed record FilterResult(
        List<M3uEntry> Selected,
        Dictionary<string, int> KeptGroups,
        int DroppedWithoutGroup,
        int DroppedExcluded);
}

