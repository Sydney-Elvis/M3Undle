using System.Diagnostics.CodeAnalysis;
using M3Undle.Cli.Commands;
using M3Undle.Core;
using M3Undle.Core.Configuration;
using M3Undle.Core.Env;
using M3Undle.Core.IO;
using M3Undle.Core.M3u;
using M3Undle.Core.Net;
using System.Runtime.CompilerServices;
using Spectre.Console;

namespace M3Undle.Cli;

public sealed class CliApp : IDisposable
{
    private readonly TextWriter _stdout;
    private readonly TextWriter _stderr;
    private readonly HttpClient _httpClient;
    private bool _disposed;

    public CliApp(TextWriter stdout, TextWriter stderr)
    {
        _stdout = stdout;
        _stderr = stderr;
        _httpClient = HttpClientFactory.CreateDefault();
    }

    public void Dispose()
    {
        if(!_disposed)
        {
            _httpClient.Dispose();
            _disposed = true;
        }
    }

    [RequiresUnreferencedCode("Application may use YAML configuration which requires reflection and is not trim-compatible. Use JSON configuration for trim-compatible operation.")]
    [RequiresDynamicCode("Application may use YAML configuration which may require dynamic code generation. Use JSON configuration for AOT-compatible operation.")]
    public async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Length == 0)
        {
            UsagePrinter.PrintUsage(_stdout);
            return ExitCodes.ConfigError;
        }

        var commandName = args[0].ToLowerInvariant();
        var optionArgs = args[1..]; // span-based slice starting at index 1 to end of array

        CommandOptionSet options;
        try
        {
            options = CommandOptionParser.Parse(optionArgs);
        }
        catch (CommandOptionException ex)
        {
            await _stderr.WriteLineAsync($"Error: {ex.Message}");
            UsagePrinter.PrintUsage(_stdout);
            return ExitCodes.ConfigError;
        }

        var diagnostics = options.IsFlagSet("verbose") ? _stdout : TextWriter.Null;
        
        // Create appropriate console - plain console if output is redirected (non-TTY)
        // This prevents ANSI escape codes from polluting log files
        var console = CreateConsole();

        try
        {
            switch (commandName)
            {
                case "groups":
                {
                    var context = await CommandContextBuilder.CreateAsync(options, CommandKind.Groups, diagnostics, cancellationToken);
                    var command = new GroupsCommand(_stdout, _stderr, diagnostics, _httpClient, new PlaylistParser(), console);
                    return await command.ExecuteAsync(context, cancellationToken);
                }
                case "run":
                {
                    var context = await CommandContextBuilder.CreateAsync(options, CommandKind.Run, diagnostics, cancellationToken);
                    var command = new RunCommand(_stdout, _stderr, diagnostics, _httpClient, new PlaylistParser(), console);
                    return await command.ExecuteAsync(context, cancellationToken);
                }
                default:
                    UsagePrinter.PrintUsage(_stdout);
                    return ExitCodes.ConfigError;
            }
        }
        catch (CommandOptionException ex)
        {
            await _stderr.WriteLineAsync($"Error: {ex.Message}");
            UsagePrinter.PrintUsage(_stdout);
            return ExitCodes.ConfigError;
        }
        catch (CliException ex)
        {
            await _stderr.WriteLineAsync(ex.Message);
            return ex.ExitCode;
        }
        catch (OperationCanceledException)
        {
            await _stderr.WriteLineAsync("Operation canceled.");
            return ExitCodes.OtherError;
        }
        catch (Exception ex)
        {
            await _stderr.WriteLineAsync($"Unexpected error: {ex}");
            if (diagnostics != TextWriter.Null)
            {
                await diagnostics.WriteLineAsync($"Stack Trace: {ex}");
            }
            return ExitCodes.OtherError;
        }
    }

    private static IAnsiConsole CreateConsole()
    {
        // Detect if stdout/stderr is redirected to a file (non-interactive/non-TTY)
        // When redirected, we want plain text output without ANSI codes
        if (Console.IsOutputRedirected || Console.IsErrorRedirected)
        {
            // Create a plain console with no ANSI support for clean log files
            return AnsiConsole.Create(new AnsiConsoleSettings
            {
                Ansi = AnsiSupport.No,
                ColorSystem = ColorSystemSupport.NoColors,
                Out = new AnsiConsoleOutput(Console.Out),
            });
        }

        // Use default interactive console with full ANSI support
        return AnsiConsole.Console;
    }
}

