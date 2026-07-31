using System.ComponentModel.DataAnnotations;

namespace gridart;

/// <summary>
/// Everything that shapes the output mosaic. Bound from the "Mosaic" configuration section, which
/// merges appsettings.json, environment variables and command-line switches; the two positional
/// arguments are translated into <see cref="BaseImage"/> and <see cref="TilesFolder"/> before
/// binding.
/// </summary>
public sealed class MosaicOptions
{
    public const string SectionName = "Mosaic";

    /// <summary>The image whose appearance the mosaic reproduces when viewed from a distance.</summary>
    [Required(AllowEmptyStrings = false)]
    public string BaseImage { get; set; } = string.Empty;

    /// <summary>Folder scanned for the tile images that make up the mosaic.</summary>
    [Required(AllowEmptyStrings = false)]
    public string TilesFolder { get; set; } = string.Empty;

    /// <summary>
    /// Where the mosaic is written. Defaults to "&lt;base-image-name&gt;.mosaic.png" next to the
    /// base image.
    /// </summary>
    public string? OutputPath { get; set; }

    /// <summary>
    /// Number of tiles along each axis, so the grid is <c>TilesAcross × TilesAcross</c> cells. Drives
    /// how far you must zoom out.
    /// </summary>
    /// <remarks>
    /// The grid is square because the <i>cells</i> carry the base image's aspect ratio — see
    /// <see cref="TileSize"/> and <c>MosaicBuilder.ResolveGrid</c>. The output is still shaped like the
    /// base image; the proportion simply lives in the tile shape rather than in the cell counts.
    /// </remarks>
    [Range(1, 4096)]
    public int TilesAcross { get; set; } = 64;

    /// <summary>
    /// Length of a tile's <b>longer</b> edge in pixels. The shorter edge follows the base image's
    /// aspect ratio, so a tile is shaped like the base image rather than square. Larger values keep the
    /// individual pictures legible when you zoom in, at the cost of output size: the mosaic is roughly
    /// TilesAcross × TileSize pixels on its long edge.
    /// </summary>
    /// <remarks>
    /// Tiles used to be square regardless of the base image. Square cells do tile a proportional grid
    /// exactly, so the *output* kept the right proportions — but every source photo was then
    /// centre-cropped to a square, which throws away the sides of every landscape shot and the top and
    /// bottom of every portrait one. A tile shaped like the base image crops far less and looks like the
    /// picture it came from. See <c>MosaicBuilder.ResolveTileSize</c>.
    /// </remarks>
    [Range(4, 1024)]
    public int TileSize { get; set; } = 48;

    /// <summary>
    /// Patches per axis used when fingerprinting a cell or tile. 1 matches on average colour only;
    /// 2-4 also matches internal structure and gives a noticeably sharper mosaic.
    /// </summary>
    [Range(1, 16)]
    public int SignatureGrid { get; set; } = 3;

    /// <summary>
    /// How strongly each placed tile is tinted toward the colour of the cell it covers.
    /// 0 leaves tiles untouched, 1 replaces them with flat colour.
    /// </summary>
    /// <remarks>
    /// <b>Defaults to 0: a source image's colours are never altered unless explicitly asked for.</b>
    /// This used to default to 0.35, which silently tinted every tile in every run — the mosaic was
    /// built from recoloured copies of the photos, not the photos. Tinting is a legitimate thing to
    /// want, so the option stays, but it is opt-in. Do not give this a non-zero default again.
    /// </remarks>
    [Range(0d, 1d)]
    public double ColorAdjustStrength { get; set; }

    /// <summary>
    /// How many times one source image may be placed. <b>Defaults to 1: each image is used at most
    /// once.</b> 0 means unlimited.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This defaulted to 0 (unlimited), which meant an ordinary run reused images heavily and said so
    /// only in a statistic nobody reads: 3,000 photos covering a 120×90 grid produced "715 distinct
    /// tile(s)" for 10,800 cells — every image about 15 times over. <c>--repeat-distance</c> does not
    /// prevent that; it is a local radius, so it only stops a repeat appearing *near* its twin.
    /// </para>
    /// <para>
    /// "Do not reuse an image" is the obvious reading of a photomosaic built from a folder of photos, so
    /// it is the default. It requires at least as many images as the grid has cells, and the run says so
    /// up front when the folder is too small rather than silently reusing. Pass <c>-r 0</c> to allow
    /// unlimited reuse, or <c>-r N</c> to cap it at N.
    /// </para>
    /// </remarks>
    [Range(0, int.MaxValue)]
    public int MaxTileReuse { get; set; } = 1;

    /// <summary>
    /// Radius, in cells, within which the same tile image is not repeated. Breaks up the visible
    /// clumping that pure nearest-colour matching produces in flat areas.
    /// </summary>
    [Range(0, 64)]
    public int RepeatAvoidanceRadius { get; set; } = 2;

    /// <summary>Whether <see cref="TilesFolder"/> is searched recursively.</summary>
    public bool Recursive { get; set; } = true;

    /// <summary>
    /// Disables the on-disk cache of decoded, cell-sized tiles. The cache is keyed on file identity
    /// and <see cref="TileSize"/> only, so it is safe to leave on while other options change.
    /// </summary>
    public bool NoCache { get; set; }

    /// <summary>
    /// Where cache files live. Defaults to <c>%LOCALAPPDATA%/GridArt/cache</c>.
    /// </summary>
    public string? CacheDirectory { get; set; }

    /// <summary>Deletes all cache files before running.</summary>
    public bool ClearCache { get; set; }

    /// <summary>Suppresses progress output. Errors and warnings are still logged.</summary>
    public bool Quiet { get; set; }

    /// <summary>
    /// How often, in seconds, an intermediate mosaic is written while tiles are still loading, as
    /// <c>&lt;output&gt;.stage-NNN.&lt;ext&gt;</c>. 0 disables it.
    /// </summary>
    /// <remarks>
    /// With tens of thousands of images, loading alone runs for many minutes; a stage file makes that
    /// development visible instead of leaving a run with nothing to look at until it finishes. Stages
    /// are previews built from the tiles loaded so far — see <c>MosaicBuilder.StageWriter</c> for how
    /// they differ from the final mosaic.
    /// </remarks>
    /// <remarks>
    /// Fractional values are accepted so tests can exercise stages without running for a minute.
    /// </remarks>
    [Range(0d, 86400d)]
    public double StageIntervalSeconds { get; set; } = 60;

    /// <summary>Overwrite <see cref="OutputPath"/> if it already exists.</summary>
    public bool Overwrite { get; set; }

    /// <summary>
    /// Resolves <see cref="OutputPath"/>, falling back to a name derived from the base image.
    /// </summary>
    public string ResolveOutputPath()
    {
        if (!string.IsNullOrWhiteSpace(OutputPath))
        {
            return Path.GetFullPath(OutputPath);
        }

        var basePath = Path.GetFullPath(BaseImage);
        var directory = Path.GetDirectoryName(basePath) ?? ".";
        var name = Path.GetFileNameWithoutExtension(basePath);
        return Path.Combine(directory, $"{name}.mosaic.png");
    }
}
