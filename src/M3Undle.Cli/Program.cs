using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;
using M3Undle.Cli;

// Check for --version or -v before running the app
if (args.Length == 1 && (args[0] == "--version" || args[0] == "-v"))
{
    var version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "unknown";
    Console.WriteLine($"bndl version {version}");
    return 0;
}

try
{
    using (var app = new CliApp(Console.Out, Console.Error))
        return await RunAppAsync(app, args);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Fatal error: {ex.Message}");
    return 1;
}

[UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode", 
    Justification = "This is the application entry point. YAML support is intentional and users are informed via attributes that JSON is the trim-compatible alternative.")]
[UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode",
    Justification = "This is the application entry point. YAML support is intentional and users are informed via attributes that JSON is the AOT-compatible alternative.")]
static Task<int> RunAppAsync(CliApp app, string[] args)
{
    return app.RunAsync(args);
}

