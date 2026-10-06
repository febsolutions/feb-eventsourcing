using Prometheus;

namespace FEB.EventSourcing.Metrics;

public class PrometheusEventStoreMetrics : IEventStoreMetrics
{
    private static readonly string[] Labels = ["aggregate", "source"];

    private static readonly Counter ReadCounter = Prometheus.Metrics
        .CreateCounter("eventstore_db_reads_total", "Number of event store read operations", new CounterConfiguration
        {
            LabelNames = Labels
        });

    private static readonly Counter WriteCounter = Prometheus.Metrics
        .CreateCounter("eventstore_db_writes_total", "Number of event store write operations", new CounterConfiguration
        {
            LabelNames = Labels
        });

    private static readonly Histogram ReadDuration = Prometheus.Metrics
        .CreateHistogram("eventstore_read_duration_seconds", "Read duration", new HistogramConfiguration
        {
            LabelNames = Labels,
            Buckets = Histogram.ExponentialBuckets(0.005, 2, 10)
        });

    private static readonly Histogram WriteDuration = Prometheus.Metrics
        .CreateHistogram("eventstore_write_duration_seconds", "Write duration", new HistogramConfiguration
        {
            LabelNames = Labels,
            Buckets = Histogram.ExponentialBuckets(0.005, 2, 10)
        });

    public void RecordRead<T>(string source, double durationSeconds) => RecordRead(typeof(T), source, durationSeconds);

    public void RecordRead(Type type, string source, double durationSeconds)
        => ReadDuration.WithLabels(type.Name, source).Observe(durationSeconds);

    public void RecordWrite<T>(string source, double durationSeconds) => RecordWrite(typeof(T), source, durationSeconds);

    public void RecordWrite(Type type, string source, double durationSeconds)
        => WriteDuration.WithLabels(type.Name, source).Observe(durationSeconds);

    public void IncrementRead<T>(string source) => IncrementRead(typeof(T), source);

    public void IncrementRead(Type type, string source) => ReadCounter.WithLabels(type.Name, source).Inc();

    public void IncrementWrite<T>(string source) => IncrementWrite(typeof(T), source);

    public void IncrementWrite(Type type, string source) => WriteCounter.WithLabels(type.Name, source).Inc();
}
