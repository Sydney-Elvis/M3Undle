using System.Diagnostics.CodeAnalysis;
using Spectre.Console;
using M3Undle.Core.Groups;
using M3Undle.Core.IO;
using M3Undle.Core.M3u;
using M3Undle.Core.Net;
using M3Undle.Core;

namespace M3Undle.Cli.Commands;

public sealed class GroupsCommand
{
    private readonly TextWriter _stdout;
    private readonly TextWriter _stderr;
    private readonly TextWriter _diagnostics;
    private readonly HttpClient _httpClient;
    private readonly PlaylistParser _parser;
    private readonly IAnsiConsole _console;

    public GroupsCommand(
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

        var fetcher = new SourceFetcher(_httpClient, _diagnostics);
        var interactiveFetcher = new InteractiveSourceFetcher(_httpClient, _diagnostics);
        
        // Detect if we should use interactive mode based on output redirection
        var isInteractive = !Console.IsOutputRedirected && !Console.IsErrorRedirected;
        
        var playlistContent = isInteractive
            ? await interactiveFetcher.GetStringWithProgressAsync(context.PlaylistSource, _console, cancellationToken)
            : await fetcher.GetStringAsync(context.PlaylistSource, cancellationToken);

        PlaylistDocument document = null!;
        if (isInteractive)
        {
            await _console.Status()
                .StartAsync("Parsing playlist...", async ctx =>
                {
                    document = await Task.Run(() => _parser.Parse(playlistContent, cancellationToken), cancellationToken);
                });
        }
        else
        {
            if (_diagnostics != TextWriter.Null)
            {
                await _diagnostics.WriteLineAsync("Parsing playlist...");
            }
            document = _parser.Parse(playlistContent, cancellationToken);
        }

        IEnumerable<M3uEntry> entries = document.Entries.Where(e => !string.IsNullOrEmpty(e.Url));
        if (context.LiveOnly)
        {
            entries = entries.Where(e => LiveClassifier.IsLive(e.Url));
        }

        SortedSet<string> groups = null!;
        if (isInteractive)
        {
            await _console.Status()
                .StartAsync("Extracting groups...", async ctx =>
                {
                    groups = await Task.Run(() => PlaylistGroupDiscovery.Discover(entries), cancellationToken);
                });
        }
        else
        {
            if (_diagnostics != TextWriter.Null)
            {
                await _diagnostics.WriteLineAsync("Extracting groups...");
            }
            groups = PlaylistGroupDiscovery.Discover(entries);
        }

        if (_diagnostics != TextWriter.Null)
        {
            await _diagnostics.WriteLineAsync($"Discovered {groups.Count} groups.");
        }

        var outPath = context.GroupsOutputPath;
        var force = context.Options.IsFlagSet("force");
        if (string.IsNullOrEmpty(outPath) || outPath == "-")
        {
            // Write to stdout
            var header = GroupsFileValidator.CreateHeader();
            var outputLines = header.Concat(groups).ToList();
            
            if (isInteractive)
            {
                _console.MarkupLine($"[blue]Displaying {groups.Count} discovered groups:[/]");
            }
            else
            {
                await _stdout.WriteLineAsync($"Displaying {groups.Count} discovered groups:");
            }
            
            foreach (var line in outputLines)
            {
                await _stdout.WriteLineAsync(line);
            }
        }
        else
        {
            // Check if file exists and merge with existing groups
            if (File.Exists(outPath))
            {
                string? fileVersion = null;
                
                // Validate the existing file (unless --force is used)
                if (!force)
                {
                    var validation = await GroupsFileValidator.ValidateFileAsync(outPath, cancellationToken);
                    fileVersion = validation.FileVersion;
                    
                    if (!validation.IsValid)
                    {
                        await _stderr.WriteLineAsync($"Warning: {validation.ErrorMessage}");
                        await _stderr.WriteLineAsync("The file will NOT be modified.");
                        await _stderr.WriteLineAsync("Use --force to override this check.");
                        
                        return ExitCodes.ConfigError;
                    }

                    if (validation.FileVersion != null && _diagnostics != TextWriter.Null)
                    {
                        await _diagnostics.WriteLineAsync($"Existing file version: {validation.FileVersion}");
                    }
                }
                else
                {
                    // Force is enabled, validate to show warning but proceed anyway
                    var validation = await GroupsFileValidator.ValidateFileAsync(outPath, cancellationToken);
                    fileVersion = validation.FileVersion;
                    
                    if (!validation.IsValid)
                    {
                        await _stderr.WriteLineAsync($"Warning: {validation.ErrorMessage}");
                        await _stderr.WriteLineAsync("Proceeding due to --force flag.");
                    }
                }

                var currentVersion = GroupsFileValidator.GetCurrentVersion();
                var result = await MergeWithExistingGroupsFileAsync(outPath, groups, currentVersion, cancellationToken);
                
                // Check if version needs updating or if new groups were added
                var versionChanged = fileVersion != null && fileVersion != currentVersion;
                var hasChanges = result.NewGroups.Count > 0 || versionChanged;
                
                if (hasChanges)
                {
                    // Only create backup if we're actually making changes
                    var backup = GroupsFileValidator.CreateBackupPath(outPath);
                    await GroupsFileValidator.CreateBackupAsync(outPath, cancellationToken);
                    
                    await TextFileWriter.WriteAtomicAsync(outPath, result.OutputLines, cancellationToken);
                    
                    if (isInteractive)
                    {
                        if (result.NewGroups.Count > 0)
                        {
                            _console.MarkupLine($"[green]Added {result.NewGroups.Count} new group(s) to {outPath}[/]");
                        }
                        if (versionChanged)
                        {
                            _console.MarkupLine($"[blue]Updated version from {fileVersion} to {currentVersion}[/]");
                        }
                        _console.MarkupLine($"[dim]Backup saved to: {backup}[/]");
                        
                        if (result.NewGroups.Count > 0)
                        {
                            _console.MarkupLine("[yellow]New groups found:[/]");
                            foreach (var newGroup in result.NewGroups)
                            {
                                _console.MarkupLine($"  [cyan]{newGroup}[/]");
                            }
                        }
                    }
                    else
                    {
                        if (result.NewGroups.Count > 0)
                        {
                            await _stdout.WriteLineAsync($"Added {result.NewGroups.Count} new group(s) to {outPath}");
                        }
                        if (versionChanged)
                        {
                            await _stdout.WriteLineAsync($"Updated version from {fileVersion} to {currentVersion}");
                        }
                        await _stdout.WriteLineAsync($"Backup saved to: {backup}");
                        
                        if (result.NewGroups.Count > 0)
                        {
                            await _stdout.WriteLineAsync("New groups found:");
                            foreach (var newGroup in result.NewGroups)
                            {
                                await _stdout.WriteLineAsync($"  {newGroup}");
                            }
                        }
                    }
                }
                else
                {
                    if (isInteractive)
                    {
                        _console.MarkupLine($"[green]No new groups found. File {outPath} unchanged.[/]");
                    }
                    else
                    {
                        await _stdout.WriteLineAsync($"No new groups found. File {outPath} unchanged.");
                    }
                }
            }
            else
            {
                // File doesn't exist, create new one
                var header = GroupsFileValidator.CreateHeader();
                var outputLines = header.Concat(groups).ToList();
                await TextFileWriter.WriteAtomicAsync(outPath, outputLines, cancellationToken);
                
                if (isInteractive)
                {
                    _console.MarkupLine($"[green]{groups.Count} groups written to {outPath}[/]");
                }
                else
                {
                    await _stdout.WriteLineAsync($"{groups.Count} groups written to {outPath}");
                }
            }
        }

        return ExitCodes.Success;
    }

    private static async Task<GroupsFileMergeResult> MergeWithExistingGroupsFileAsync(
        string filePath, 
        SortedSet<string> discoveredGroups, 
        string currentVersion,
        CancellationToken cancellationToken)
    {
        var existingLines = await File.ReadAllLinesAsync(filePath, cancellationToken);
        return GroupsFileMerge.Merge(existingLines, discoveredGroups, currentVersion);
    }
}
