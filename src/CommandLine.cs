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

        Options (any MosaicOptions property works the same way):
          --Mosaic:OutputPath=<path>          Default: <base-image>.mosaic.png
          --Mosaic:TilesAcross=<n>            Tiles along the long axis (default 64)
          --Mosaic:TileSize=<px>              Pixels per tile (default 48)
          --Mosaic:SignatureGrid=<n>          Match patches per axis, 1-16 (default 3)
          --Mosaic:ColorAdjustStrength=<0-1>  Tint toward the base image (default 0.35)
          --Mosaic:MaxTileReuse=<n>           0 = unlimited (default)
          --Mosaic:RepeatAvoidanceRadius=<n>  Cells (default 2)
          --Mosaic:Recursive=<bool>           Scan subfolders (default true)
          --Mosaic:Overwrite=<bool>           Replace an existing output (default false)
        """;

    /// <summary>
    /// Splits <paramref name="args"/> into positional values mapped onto configuration keys and the
    /// remaining switches, which are passed through untouched.
    /// </summary>
    public static (Dictionary<string, string?> Positional, string[] Remaining) Parse(string[] args)
    {
        var positional = new Dictionary<string, string?>();
        var remaining = new List<string>();
        var keys = new[] { $"{MosaicOptions.SectionName}:{nameof(MosaicOptions.BaseImage)}", $"{MosaicOptions.SectionName}:{nameof(MosaicOptions.TilesFolder)}" };
        var next = 0;

        foreach (var arg in args)
        {
            if (next < keys.Length && !arg.StartsWith('-') && !arg.StartsWith('/'))
            {
                positional[keys[next++]] = arg;
                continue;
            }

            remaining.Add(arg);
        }

        return (positional, remaining.ToArray());
    }

    /// <summary>True when the arguments ask for help rather than a mosaic run.</summary>
    public static bool WantsHelp(string[] args) => args.Any(a =>
        a is "-h" or "--help" or "-?" or "/?" or "help");
}
