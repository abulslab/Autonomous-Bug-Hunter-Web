using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace BugHunter.Api.Common;

// One ActivitySource and one Meter for the whole service. Exported over OTLP when OTEL_EXPORTER_OTLP_ENDPOINT is set
// (Jaeger, Grafana, Azure Monitor, ...); otherwise they cost nothing.
public static class Telemetry
{
    public const string Name = "BugHunter";
    public static readonly ActivitySource Source = new(Name);
    static readonly Meter Meter = new(Name);

    public static readonly Counter<long> Hunts = Meter.CreateCounter<long>("bughunter.hunts", description: "Hunts finished, by status and strategy");
    public static readonly Histogram<double> HuntMinutes = Meter.CreateHistogram<double>("bughunter.hunt.duration", "min", "Wall-clock time per hunt");
    public static readonly Counter<long> Findings = Meter.CreateCounter<long>("bughunter.findings", description: "Findings, by provenance and replay result");
    public static readonly Counter<long> ModelCalls = Meter.CreateCounter<long>("bughunter.model.calls", description: "Model calls, by provider, model and purpose");
    public static readonly Counter<long> ModelTokens = Meter.CreateCounter<long>("bughunter.model.tokens", description: "Model tokens, by direction");
    public static readonly Counter<double> ModelCost = Meter.CreateCounter<double>("bughunter.model.cost", "USD", "Model spend");
    public static readonly Counter<long> ProbeRequests = Meter.CreateCounter<long>("bughunter.probe.requests", description: "API requests sent by the probe phase");
}
