namespace gridart;

/// <summary>
/// Translates the two positional arguments into configuration keys. Everything else is left to the
/// standard configuration pipeline, so any <see cref="MosaicOptions"/> property can be set with
/// <c>--Mosaic:Name=value</c> or the <c>Mosaic__Name</c> environment variable.
/// </summary>
public static class CommandLine
{
    public const string UsageText = """
        Usage: gridart <base-image> <tiles-folder> [options]

          <base-image>     Image the mosaic should resemble when zoomed out.
          <tiles-folder>   Folder of images used as mosaic tiles.

        Options:
          -o, --output <path>            Output file (default: <base-image>.mosaic.png)
          -n, --tiles-across <n>         Tiles along the long axis (default 64)
          -s, --tile-size <px>           Pixels per tile (default 48)
          -g, --signature-grid <n>       Match patches per axis, 1-16 (default 3)
          -c, --color-adjust <0-1>       Tint toward the base image (default 0.35)
          -r, --max-reuse <n>            Max placements per image, 0 = unlimited (default 0)
          -d, --repeat-distance <n>      Cells to keep between repeats (default 2)
              --recursive <bool>         Scan subfolders (default true)
          -f, --overwrite                Replace an existing output file
              --no-cache                 Skip the decoded-tile cache for this run
              --cache-dir <path>         Cache location (default: %LOCALAPPDATA%\GridArt\cache)
              --clear-cache              Delete all cache files before running
          -q, --quiet                    Suppress progress output
          -h, --help                     Show this help

        Decoded, cell-sized tiles are cached on disk and reused when a source file's size and
        timestamp are unchanged. The cache is keyed on the tiles folder and --tile-size only, so
        changing any other option still hits the cache.

        Every option is also settable as configuration, which is how appsettings.json and
        environment variables reach it:
          --Mosaic:TilesAcross=80        command line
          Mosaic__TilesAcross=80         environment variable
          { "Mosaic": { "TilesAcross": 80 } }   appsettings.json
        """;

    /// <summary>
    /// Short and long aliases for the <c>Mosaic:*</c> configuration keys. Aliases are the only
    /// hand-maintained part of the options surface — add an entry here when adding a property to
    /// <see cref="MosaicOptions"/>, or it will still work as <c>--Mosaic:Name=value</c> but without
    /// a short form.
    /// </summary>
    public static IReadOnlyDictionary<string, string> SwitchMappings { get; } = BuildSwitchMappings();

    /// <summary>
    /// Flags that take no value. The configuration binder needs <c>key=true</c>, so a bare
    /// <c>--overwrite</c> is expanded before parsing.
    /// </summary>
    private static readonly string[] BooleanFlags =
        ["-f", "--overwrite", "--no-cache", "--clear-cache", "-q", "--quiet"];

    private static Dictionary<string, string> BuildSwitchMappings()
    {
        // Case-insensitive to match how the configuration provider compares switches internally, so
        // FindUnknownSwitch can't reject something the provider would have accepted.
        var mappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        void Map(string key, string shortAlias, string longAlias)
        {
            var target = $"{MosaicOptions.SectionName}:{key}";
            if (shortAlias.Length > 0)
            {
                mappings[shortAlias] = target;
            }
            mappings[longAlias] = target;
        }

        Map(nameof(MosaicOptions.OutputPath), "-o", "--output");
        Map(nameof(MosaicOptions.TilesAcross), "-n", "--tiles-across");
        Map(nameof(MosaicOptions.TileSize), "-s", "--tile-size");
        Map(nameof(MosaicOptions.SignatureGrid), "-g", "--signature-grid");
        Map(nameof(MosaicOptions.ColorAdjustStrength), "-c", "--color-adjust");
        Map(nameof(MosaicOptions.MaxTileReuse), "-r", "--max-reuse");
        Map(nameof(MosaicOptions.RepeatAvoidanceRadius), "-d", "--repeat-distance");
        Map(nameof(MosaicOptions.Recursive), "", "--recursive");
        Map(nameof(MosaicOptions.Overwrite), "-f", "--overwrite");
        Map(nameof(MosaicOptions.NoCache), "", "--no-cache");
        Map(nameof(MosaicOptions.CacheDirectory), "", "--cache-dir");
        Map(nameof(MosaicOptions.ClearCache), "", "--clear-cache");
        Map(nameof(MosaicOptions.Quiet), "-q", "--quiet");

        return mappings;
    }

    /// <summary>
    /// Splits <paramref name="args"/> into positional values mapped onto configuration keys and the
    /// remaining switches, which are handed to the configuration provider.
    /// </summary>
    public static (Dictionary<string, string?> Positional, string[] Remaining) Parse(string[] args)
    {
        var positional = new Dictionary<string, string?>();
        var remaining = new List<string>();
        var keys = new[]
        {
            $"{MosaicOptions.SectionName}:{nameof(MosaicOptions.BaseImage)}",
            $"{MosaicOptions.SectionName}:{nameof(MosaicOptions.TilesFolder)}",
        };
        var next = 0;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];

            if (IsSwitch(arg))
            {
                if (IsBooleanFlag(arg))
                {
                    // The binder needs an explicit value; a bare flag means "true".
                    remaining.Add($"{arg}=true");
                    continue;
                }

                remaining.Add(arg);

                // A switch given as "--key value" consumes the next token, which must not be
                // mistaken for a positional argument — otherwise "gridart -o out.png base tiles"
                // would treat out.png as the base image.
                if (!arg.Contains('=') && i + 1 < args.Length && !IsSwitch(args[i + 1]))
                {
                    remaining.Add(args[++i]);
                }

                continue;
            }

            if (next < keys.Length)
            {
                positional[keys[next++]] = arg;
                continue;
            }

            remaining.Add(arg);
        }

        return (positional, remaining.ToArray());
    }

    /// <summary>
    /// Returns the first unrecognised switch in <paramref name="args"/>, or null if all are known.
    /// This is checked explicitly because the configuration provider only rejects an unknown short
    /// switch when it carries an inline value: <c>-Z=9</c> throws, but <c>-Z 9</c> is silently
    /// discarded, which would turn a typo into a run with unintended defaults.
    /// </summary>
    public static string? FindUnknownSwitch(string[] args)
    {
        foreach (var arg in args)
        {
            if (!IsSwitch(arg))
            {
                continue;
            }

            var name = arg.Split('=', 2)[0];

            // Fully-qualified configuration keys stay supported under either switch prefix the
            // configuration provider accepts, as do the help flags.
            if (name.StartsWith("--Mosaic:", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("/Mosaic:", StringComparison.OrdinalIgnoreCase) ||
                name is "-h" or "--help" or "-?" or "/?")
            {
                continue;
            }

            if (!SwitchMappings.ContainsKey(name))
            {
                return name;
            }
        }

        return null;
    }

    /// <summary>
    /// Decides whether a token is an option rather than a value.
    /// </summary>
    /// <remarks>
    /// A leading '/' is deliberately NOT enough. The configuration provider accepts Windows-style
    /// <c>/key=value</c> switches, but on this platform a token starting with '/' is far more often a
    /// path — MSYS/Git Bash hands over things like <c>/c/photos/base.png</c>, and treating those as
    /// options made the worker reject a perfectly good path and print its usage instead. So '/' is
    /// only an option when it actually names a known switch.
    /// </remarks>
    private static bool IsSwitch(string arg)
    {
        if (arg.Length < 2)
        {
            return false;
        }

        if (arg.StartsWith('-'))
        {
            return true;
        }

        if (!arg.StartsWith('/'))
        {
            return false;
        }

        var name = arg.Split('=', 2)[0];
        return name is "/?"
            || SwitchMappings.ContainsKey(name)
            || name.StartsWith("/Mosaic:", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBooleanFlag(string arg)
    {
        // "--overwrite=false" is already explicit and must be left alone.
        var name = arg.Split('=', 2)[0];
        return arg.Length == name.Length
            && BooleanFlags.Contains(name, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>True when the arguments ask for help rather than a mosaic run.</summary>
    public static bool WantsHelp(string[] args) => args.Any(a =>
        a is "-h" or "--help" or "-?" or "/?" or "help");
}
