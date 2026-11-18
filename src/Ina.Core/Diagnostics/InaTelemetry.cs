using System.Diagnostics;

namespace Ina.Diagnostics;

/// <summary>Tracing for the build pipeline. Every phase is an activity, so listeners can observe where time goes.</summary>
public static class InaTelemetry
{
    public const string SourceName = "Ina";

    public static ActivitySource ActivitySource { get; } = new(SourceName);
}
