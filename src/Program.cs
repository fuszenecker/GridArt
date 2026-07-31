using gridart;
using gridart.Imaging;
using gridart.Progress;
using Microsoft.Extensions.Options;

if (CommandLine.WantsHelp(args))
{
    Console.WriteLine(CommandLine.UsageText);
    return 0;
}

if (CommandLine.FindUnknownSwitch(args) is { } unknown)
{
    Console.Error.WriteLine($"Unknown option '{unknown}'.");

    if (File.Exists(unknown) || Directory.Exists(unknown))
    {
        Console.Error.WriteLine(
            "That looks like an existing path. Paths starting with '-' must be passed as an " +
            "absolute or './'-prefixed path so they are not read as an option.");
    }

    Console.Error.WriteLine();
    Console.Error.WriteLine(CommandLine.UsageText);
    return 1;
}

var (positional, remaining) = CommandLine.Parse(args);

// The host is built without args so its own command-line provider doesn't see the short aliases and
// reject them; the aliased provider below is added explicitly instead, last so it still wins.
var builder = Host.CreateApplicationBuilder();
builder.Configuration.AddInMemoryCollection(positional);
builder.Configuration.AddCommandLine(remaining, CommandLine.SwitchMappings.ToDictionary());

var section = builder.Configuration.GetSection(MosaicOptions.SectionName);

// The paths may also arrive from appsettings.json or the environment, so this check runs against the
// fully merged configuration rather than the arguments alone.
var hasBaseImage = !string.IsNullOrWhiteSpace(section[nameof(MosaicOptions.BaseImage)]);
var hasTilesFolder = !string.IsNullOrWhiteSpace(section[nameof(MosaicOptions.TilesFolder)]);

if (!hasBaseImage || !hasTilesFolder)
{
    // Say which argument is missing and echo what was actually received. Printing bare usage here
    // left no way to tell "you forgot an argument" apart from "your path was swallowed".
    Console.Error.WriteLine(hasBaseImage
        ? "Missing the tiles folder (second argument)."
        : "Missing the base image (first argument).");

    if (args.Length > 0)
    {
        Console.Error.WriteLine($"Received {args.Length} argument(s): {string.Join(" ", args.Select(a => $"'{a}'"))}");
    }

    Console.Error.WriteLine();
    Console.Error.WriteLine(CommandLine.UsageText);
    return 1;
}

builder.Services
    .AddOptions<MosaicOptions>()
    .Bind(section)
    .ValidateDataAnnotations();

// One line per message instead of the default two, and no category/event-id noise: this is a CLI, so
// the log is the user interface.
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o =>
{
    o.SingleLine = true;
    o.IncludeScopes = false;
    o.TimestampFormat = null;
});

// Progress goes through ILogger like everything else, so --quiet, log-level filters and any extra
// provider apply to it uniformly.
builder.Services.AddSingleton<IProgressReporter>(sp =>
    sp.GetRequiredService<IOptions<MosaicOptions>>().Value.Quiet
        ? NullProgressReporter.Instance
        : new LoggingProgressReporter(sp.GetRequiredService<ILogger<LoggingProgressReporter>>()));

builder.Services.AddSingleton<MosaicBuilder>();
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
await host.RunAsync();

return Environment.ExitCode;
