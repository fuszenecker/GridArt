using gridart.Imaging;
using Microsoft.Extensions.Options;
using SixLabors.ImageSharp;

namespace gridart;

/// <summary>
/// Runs one mosaic job and then stops the host. This is a batch worker, not a long-lived service:
/// the exit code is 0 on success and 1 on failure so it composes with scripts and CI.
/// </summary>
public sealed class Worker(
    IOptions<MosaicOptions> options,
    MosaicBuilder builder,
    IHostApplicationLifetime lifetime,
    ILogger<Worker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Accessing .Value here triggers DataAnnotations validation, so bad options surface as a
            // clean message rather than an unhandled startup exception.
            var mosaicOptions = options.Value;

            Validate(mosaicOptions);

            var outputPath = mosaicOptions.ResolveOutputPath();
            if (File.Exists(outputPath) && !mosaicOptions.Overwrite)
            {
                throw new InvalidOperationException(
                    $"'{outputPath}' already exists. Pass --Mosaic:Overwrite=true to replace it, " +
                    "or choose another --Mosaic:OutputPath.");
            }

            using var result = await builder.BuildAsync(mosaicOptions, stoppingToken);

            var directory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await result.Image.SaveAsync(outputPath, stoppingToken);

            logger.LogInformation(
                "Wrote {Path} ({Width}x{Height}).",
                outputPath, result.Image.Width, result.Image.Height);

            Environment.ExitCode = 0;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            logger.LogWarning("Cancelled before the mosaic was written.");
            Environment.ExitCode = 1;
        }
        catch (Exception ex)
        {
            // The message is the user-facing contract; the stack trace goes to debug level so normal
            // runs stay readable.
            logger.LogError("{Message}", ex.Message);
            logger.LogDebug(ex, "Mosaic run failed.");
            Environment.ExitCode = 1;
        }
        finally
        {
            lifetime.StopApplication();
        }
    }

    private static void Validate(MosaicOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.BaseImage) || string.IsNullOrWhiteSpace(options.TilesFolder))
        {
            throw new InvalidOperationException(CommandLine.UsageText);
        }

        if (!File.Exists(options.BaseImage))
        {
            throw new FileNotFoundException($"Base image not found: '{options.BaseImage}'.", options.BaseImage);
        }

        if (!Directory.Exists(options.TilesFolder))
        {
            throw new DirectoryNotFoundException($"Tiles folder not found: '{options.TilesFolder}'.");
        }
    }
}
