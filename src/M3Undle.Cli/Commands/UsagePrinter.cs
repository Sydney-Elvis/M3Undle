namespace M3Undle.Cli.Commands;

public static class UsagePrinter
{
    public static void PrintUsage(TextWriter stdout)
    {
        stdout.WriteLine("Usage:");
        stdout.WriteLine("  bndl groups   [options]");
        stdout.WriteLine("  bndl run      [options]");
        stdout.WriteLine("  bndl --version | -v");
        stdout.WriteLine();
        stdout.WriteLine("Commands:");
        stdout.WriteLine("  groups    Create or refresh the group selection file.");
        stdout.WriteLine("  run       One-shot pipeline: fetch → filter → write.");
        stdout.WriteLine();
        stdout.WriteLine("Options (see docs/cli_spec.md for full details):");
        stdout.WriteLine("  --playlist-url <url>");
        stdout.WriteLine("  --epg-url <url>");
        stdout.WriteLine("  --config <path>");
        stdout.WriteLine("  --profile <name>");
        stdout.WriteLine("  --groups-file <path>");
        stdout.WriteLine("  --out-groups <path>");
        stdout.WriteLine("  --out-playlist <path>");
        stdout.WriteLine("  --out-epg <path>");
        stdout.WriteLine("  --live                Filter to exclude VOD content (movies/series); keeps live streams only");
        stdout.WriteLine("  --verbose");
        stdout.WriteLine("  --force");
        stdout.WriteLine("  --version, -v   Show version information and exit.");
    }
}

