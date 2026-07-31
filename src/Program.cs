using gridart;
using gridart.Imaging;

if (CommandLine.WantsHelp(args))
{
    Console.WriteLine(CommandLine.UsageText);
    return 0;
}

var (positional, remaining) = CommandLine.Parse(args);

var builder = Host.CreateApplicationBuilder(remaining);
builder.Configuration.AddInMemoryCollection(positional);

var section = builder.Configuration.GetSection(MosaicOptions.SectionName);

// The paths may also arrive from appsettings.json or the environment, so this check runs against the
// fully merged configuration rather than the arguments alone. Doing it here keeps the "no arguments"
// case a clean usage message instead of an options-validation dump.
if (string.IsNullOrWhiteSpace(section[nameof(MosaicOptions.BaseImage)]) ||
    string.IsNullOrWhiteSpace(section[nameof(MosaicOptions.TilesFolder)]))
{
    Console.Error.WriteLine(CommandLine.UsageText);
    return 1;
}

builder.Services
    .AddOptions<MosaicOptions>()
    .Bind(section)
    .ValidateDataAnnotations();

builder.Services.AddSingleton<MosaicBuilder>();
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
await host.RunAsync();

return Environment.ExitCode;
