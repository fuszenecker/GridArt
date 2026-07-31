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

    /// <summary>Number of tiles along the longer axis of the output. Drives how far you must zoom out.</summary>
    [Range(1, 4096)]
    public int TilesAcross { get; set; } = 64;

    /// <summary>
    /// Resolution each tile is rendered at. Larger values keep the individual pictures legible when
    /// you zoom in, at the cost of output size: the mosaic is roughly
    /// TilesAcross × TileSize pixels on its long edge.
    /// </summary>
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
    /// 0 leaves tiles untouched (most detail visible up close, weakest likeness from afar);
    /// 1 replaces them with flat colour. Around 0.3 reads well at both distances.
    /// </summary>
    [Range(0d, 1d)]
    public double ColorAdjustStrength { get; set; } = 0.35;

    /// <summary>
    /// How many times one tile image may be placed. 0 means unlimited. Low values force variety but
    /// require enough source images to cover every cell.
    /// </summary>
    [Range(0, int.MaxValue)]
    public int MaxTileReuse { get; set; }

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
