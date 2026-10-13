using System.Diagnostics;

namespace RelayHub.Core.Telemetry;

/// <summary>Activity source for RelayHub's own spans (worker processing). Exported through OpenTelemetry.</summary>
public static class RelayHubTelemetry
{
    public const string SourceName = "RelayHub";

    public static readonly ActivitySource Source = new(SourceName);
}
