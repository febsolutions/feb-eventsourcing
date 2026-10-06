# FEB.EventSourcing.Metrics

Prometheus metrics for the FEB.EventSourcing framework: read/write counters and
duration histograms per aggregate type and source (`eventStore`, `redis`,
`mongodb-snapshots`, …) via `es.UsePrometheusMetrics()`.
