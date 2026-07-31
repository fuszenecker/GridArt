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
          -c, --color-adjust <0-1>       Tint tiles toward the base image (default 0 = off)
          -r, --max-reuse <n>            Max placements per image, 0 = unlimited (default 0)
          -d, --repeat-distance <n>      Cells that must separate two uses of one image (default 2)
              --recursive <bool>         Scan subfolders (default true)
          -f, --overwrite                Replace an existing output file
              --no-cache                 Skip the decoded-tile cache for this run
              --cache-dir <path>         Cache location (default: %LOCALAPPDATA%\GridArt\cache)
              --clear-cache              Delete all cache files before running
              --stage-interval <s>       Seconds between intermediate stage images, 0 = off (default 60)
          -q, --quiet                    Suppress progress output
          -h, --help                     Show this help

        Decoded, cell-sized tiles are cached on disk as they load and reused when a source file's
        size and timestamp are unchanged, so an interrupted run resumes instead of starting over.
        The cache is keyed on the tiles folder and --tile-size only, so changing any other option
        still hits the cache.

        --repeat-distance is absolute: no two cells within that many cells of each other ever get the
        same image. It is never relaxed. If the folder holds too few images for the grid, the run
        fails up front saying how many are needed — add more images, lower --repeat-distance, or
        lower --tiles-across.

        Source images are reproduced with their own colours. Nothing tints, brightens or recolours a
        tile unless you pass --color-adjust above 0.

        While tiles load, a preview mosaic is written every --stage-interval seconds as
        <output>.stage-NNN.<ext>, so the development is visible on long runs over many images.

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
    /// Every switch that binds to a <c>bool</c> property. The configuration binder needs
    /// <c>key=value</c>, so a bare <c>--overwrite</c> is expanded to <c>--overwrite=true</c> before
    /// parsing.
    /// </summary>
    /// <remarks>
    /// <c>--recursive</c> belongs here even though it is documented as taking a value. It was missing,
    /// so it fell through to the "--key value" branch and swallowed the following token:
    /// <c>gridart base tiles --recursive -n 120</c> bound "-n" to Mosaic:Recursive and died with
    /// "Failed to convert configuration value '-n'". Being in this list does not stop
    /// <c>--recursive false</c> from working — see <see cref="Parse"/>, which still consumes a
    /// following token when it actually looks like a boolean.
    /// </remarks>
    private static readonly string[] BooleanFlags =
    [
        "-f", "--overwrite", "--no-cache", "--clear-cache", "-q", "--quiet", "--recursive",
    ];

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
        Map(nameof(MosaicOptions.StageIntervalSeconds), "", "--stage-interval");

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
                    // "--recursive false" is the documented spelling for turning one off, so a
                    // following token is consumed when — and only when — it actually reads as a
                    // boolean. Anything else (a path, "-n", the next switch) is left where it is:
                    // guessing that it belonged to the flag is what made "--recursive -n 120" bind
                    // "-n" to Mosaic:Recursive and abort the run.
                    if (i + 1 < args.Length && IsBooleanValue(args[i + 1]))
                    {
                        remaining.Add($"{arg}={args[++i]}");
                        continue;
                    }

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

    /// <summary>
    /// Whether a token is a value a bool property would accept.
    /// </summary>
    /// <remarks>
    /// Exactly <see cref="bool.TryParse"/>, and deliberately no wider. The configuration binder
    /// converts through <c>TypeDescriptor</c>, whose bool converter throws a FormatException on "1"
    /// and "0" — accepting those here would consume the token and then abort the run, which is the
    /// failure this whole path exists to prevent.
    /// </remarks>
    private static bool IsBooleanValue(string arg) => bool.TryParse(arg, out _);

    /// <summary>True when the arguments ask for help rather than a mosaic run.</summary>
    public static bool WantsHelp(string[] args) => args.Any(a =>
        a is "-h" or "--help" or "-?" or "/?" or "help");
}
