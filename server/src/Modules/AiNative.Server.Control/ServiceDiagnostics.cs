using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;

namespace AiNative.Server.Control;

// Only fixed method names and roles become dimensions; identities and credentials never become telemetry tags.
public static class ServiceDiagnostics
{
    public const string Name = "AiNative.Server.Control";
    public static readonly ActivitySource Activities = new(Name);
    public static readonly Meter Meter = new(Name);
    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>("server.rpc.duration", "ms");
    private static readonly Counter<long> Failures = Meter.CreateCounter<long>("server.pump.failures");
    private static readonly HashSet<string> Methods = typeof(ServiceMethods).GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(x => x.FieldType == typeof(string)).Select(x => (string)x.GetValue(null)!).ToHashSet(StringComparer.Ordinal);
    public static string Method(string method) => Methods.Contains(method) ? method : "unknown";
    public static void RecordRpc(string method, ServiceRole role, long started, bool success)
    {
        TagList tags = new() { { "rpc.method", Method(method) }, { "service.role", role.ToString() }, { "rpc.success", success } };
        Duration.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds, tags);
    }
    public static void RecordPumpFailure() => Failures.Add(1);
}
