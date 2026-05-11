using System.Text;

public sealed class MetricsStore
{
    private readonly object _syncRoot = new();
    private long _successfulTransitions;
    private long _failedTransitions;
    private long _duplicateDeliveries;
    private long _compensations;
    private readonly Dictionary<string, LatencyAccumulator> _latencyByStep = new();

    public void RegisterSuccessfulTransition(string step, double elapsedMs)
    {
        lock (_syncRoot)
        {
            _successfulTransitions++;
            RegisterLatency(step, elapsedMs);
        }
    }

    public void RegisterFailedTransition(string step, double elapsedMs)
    {
        lock (_syncRoot)
        {
            _failedTransitions++;
            RegisterLatency(step, elapsedMs);
        }
    }

    public void RegisterDuplicateDelivery(string step)
    {
        lock (_syncRoot)
        {
            _duplicateDeliveries++;
            RegisterLatency(step, 0);
        }
    }

    public void RegisterCompensation()
    {
        lock (_syncRoot)
        {
            _compensations++;
        }
    }

    public string ToPrometheusText()
    {
        lock (_syncRoot)
        {
            var text = new StringBuilder();
            text.AppendLine("# TYPE booking_transition_success_total counter");
            text.AppendLine($"booking_transition_success_total {_successfulTransitions}");
            text.AppendLine("# TYPE booking_transition_error_total counter");
            text.AppendLine($"booking_transition_error_total {_failedTransitions}");
            text.AppendLine("# TYPE booking_duplicate_delivery_total counter");
            text.AppendLine($"booking_duplicate_delivery_total {_duplicateDeliveries}");
            text.AppendLine("# TYPE booking_compensation_total counter");
            text.AppendLine($"booking_compensation_total {_compensations}");
            text.AppendLine("# TYPE booking_step_latency_ms gauge");

            foreach (var item in _latencyByStep.OrderBy(item => item.Key, StringComparer.Ordinal))
            {
                var average = item.Value.Count == 0 ? 0 : item.Value.TotalMs / item.Value.Count;
                text.AppendLine(FormattableString.Invariant(
                    $"booking_step_latency_ms{{step=\"{EscapeLabel(item.Key)}\",kind=\"avg\"}} {average:0.###}"));
                text.AppendLine(FormattableString.Invariant(
                    $"booking_step_latency_ms{{step=\"{EscapeLabel(item.Key)}\",kind=\"max\"}} {item.Value.MaxMs:0.###}"));
            }

            return text.ToString();
        }
    }

    private void RegisterLatency(string step, double elapsedMs)
    {
        if (!_latencyByStep.TryGetValue(step, out var accumulator))
        {
            accumulator = new LatencyAccumulator();
            _latencyByStep[step] = accumulator;
        }

        accumulator.Count++;
        accumulator.TotalMs += elapsedMs;
        accumulator.MaxMs = Math.Max(accumulator.MaxMs, elapsedMs);
    }

    private static string EscapeLabel(string value)
    {
        return value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);
    }

    private sealed class LatencyAccumulator
    {
        public long Count { get; set; }
        public double TotalMs { get; set; }
        public double MaxMs { get; set; }
    }
}
