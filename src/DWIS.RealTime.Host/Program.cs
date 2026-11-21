// The real-time process host (specification 3.1, section 8). Milestone 4.0: the OPC Foundation server with the
// DDHubServer façade in its shape, Core wired to it, the lab security profile. The injection (M4.1) and the
// queries (M4.2) fill the façade; the image and the pipelines come with M4.3.

using DWIS.Blackboard.Configuration;
using DWIS.RealTime.Core;
using DWIS.RealTime.Host;
using DWIS.RealTime.OpcFoundation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

// The five configuration layers of section 8 (R-CFG-1): defaults, the shared file, environment (DWIS__RealTime__…),
// the command line (--RealTime:EndpointUrl=…), on top of the host's own sources.
var layers = new DwisConfigurationOptions
{
    BasePath = builder.Environment.ContentRootPath,
    EnvironmentName = builder.Environment.EnvironmentName,
    CommandLineArguments = args,
};
foreach (var (key, value) in RealTimeOptions.Defaults)
{
    layers.Defaults[key] = value;
}
builder.Configuration.AddDwisLayers(layers);
builder.Services.Configure<RealTimeOptions>(builder.Configuration.GetSection(RealTimeOptions.Section));
builder.Services.Configure<HostSettings>(builder.Configuration.GetSection(RealTimeOptions.Section));
builder.Services.AddHostedService<RealTimeService>();

var host = builder.Build();

// The effective configuration with the source of each value (R-CFG-2), this process's section only.
var effective = EffectiveConfiguration.Describe((IConfigurationRoot)host.Services.GetRequiredService<IConfiguration>())
    .Where(e => e.Key.StartsWith(RealTimeOptions.Section + ":", StringComparison.OrdinalIgnoreCase))
    .ToList();
var hostLogger = host.Services.GetRequiredService<ILogger<Program>>();
if (hostLogger.IsEnabled(LogLevel.Information))
{
    var values = string.Join(Environment.NewLine, effective.Select(e => $"  {e.Key} = {e.Value ?? "(null)"}  [{e.Source}]"));
    HostLog.EffectiveConfiguration(hostLogger, effective.Count, EffectiveConfiguration.Mask, values);
}

await host.RunAsync().ConfigureAwait(false);

/// <summary>Marker for the host's logger category.</summary>
internal sealed partial class Program;

internal static partial class HostLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Effective configuration ({Count} values, secrets as {Mask}): {Values}")]
    public static partial void EffectiveConfiguration(ILogger logger, int count, string mask, string values);
}
