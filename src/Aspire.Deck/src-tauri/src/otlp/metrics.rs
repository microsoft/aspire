//! Time-series storage and queries for metrics.
//!
//! The OTLP endpoint records every metric data point into a bounded per-series
//! ring buffer keyed by (metric name, resource). Unlike the summary view (which
//! only keeps the last value), this retains real timestamped history so the UI
//! can draw time-axis charts with zoom, pan and a selectable window.
//!
//! Instrument semantics are preserved so the chart shows something meaningful:
//!   - Gauge / non-monotonic Sum  -> raw value over time.
//!   - Monotonic Sum (counter)    -> per-second rate (delta of the cumulative
//!     value divided by elapsed time; for delta-temporality the point already is
//!     the interval delta).
//!   - Histogram                  -> p50/p90/p99 latency lines, computed from the
//!     bucket distribution observed in each interval.

use serde::Serialize;
use std::collections::VecDeque;

/// Max samples retained per series. At a typical 1-5s export cadence this covers
/// well over an hour; older points are also trimmed by [`MAX_AGE_MS`].
const MAX_SAMPLES: usize = 4000;

/// Max age of a retained sample. Bounds memory for long-lived, slow series.
const MAX_AGE_MS: i64 = 6 * 60 * 60 * 1000; // 6 hours

/// How the UI should chart a metric.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub enum MetricKind {
    /// Raw instantaneous value.
    Gauge,
    /// Monotonic cumulative counter; charted as a per-second rate.
    Counter,
    /// Non-monotonic sum; charted as the raw value.
    UpDownCounter,
    /// Distribution; charted as percentile lines.
    Histogram,
}

impl MetricKind {
    #[allow(dead_code)]
    pub fn as_str(self) -> &'static str {
        match self {
            MetricKind::Gauge => "gauge",
            MetricKind::Counter => "counter",
            MetricKind::UpDownCounter => "upDownCounter",
            MetricKind::Histogram => "histogram",
        }
    }
}

/// A single recorded sample. Histogram samples carry precomputed percentiles for
/// the interval so we don't retain full bucket arrays per point.
#[derive(Debug, Clone)]
struct Sample {
    t_ms: i64,
    /// Gauge/up-down: raw value. Counter: cumulative total (rate derived later).
    /// Delta counter: the interval delta. Unused for histograms.
    value: f64,
    /// True when `value` is already an interval delta (delta-temporality counter),
    /// so the rate is value/dt rather than (value - prev)/dt.
    is_delta: bool,
    hist: Option<HistSample>,
}

#[derive(Debug, Clone, Copy)]
struct HistSample {
    p50: f64,
    p90: f64,
    p99: f64,
}

/// One stored time series, identified by (metric name, resource).
pub struct MetricSeries {
    pub name: String,
    pub resource_name: Option<String>,
    pub unit: Option<String>,
    pub kind: MetricKind,
    samples: VecDeque<Sample>,
    /// Previous cumulative histogram bucket counts, kept so we can diff successive
    /// cumulative points into per-interval distributions for "recent" percentiles.
    prev_buckets: Option<Vec<u64>>,
}

impl MetricSeries {
    fn new(name: String, resource_name: Option<String>, unit: Option<String>, kind: MetricKind) -> Self {
        MetricSeries {
            name,
            resource_name,
            unit,
            kind,
            samples: VecDeque::new(),
            prev_buckets: None,
        }
    }

    fn push(&mut self, sample: Sample) {
        // Drop out-of-order samples; charts assume monotonic time.
        if let Some(back) = self.samples.back() {
            if sample.t_ms < back.t_ms {
                return;
            }
        }
        self.samples.push_back(sample);

        let newest = self.samples.back().map(|s| s.t_ms).unwrap_or(0);
        while self.samples.len() > MAX_SAMPLES {
            self.samples.pop_front();
        }
        while let Some(front) = self.samples.front() {
            if newest - front.t_ms > MAX_AGE_MS {
                self.samples.pop_front();
            } else {
                break;
            }
        }
    }

    /// The latest raw value (cumulative for counters), for the summary list.
    pub fn last_value(&self) -> Option<f64> {
        self.samples.back().map(|s| s.value)
    }

    pub fn point_count(&self) -> u64 {
        self.samples.len() as u64
    }
}

/// Stable key for a series.
pub fn series_key(name: &str, resource: Option<&str>) -> String {
    match resource {
        Some(r) => format!("{name}\u{0}{r}"),
        None => name.to_string(),
    }
}

/// A bounded collection of metric series keyed by (name, resource).
#[derive(Default)]
pub struct MetricStore {
    series: indexmap::IndexMap<String, MetricSeries>,
    /// Total data points ingested across all series (for the telemetry summary).
    pub point_count: u64,
}

impl MetricStore {
    pub fn series_mut(
        &mut self,
        name: &str,
        resource: Option<&str>,
        unit: Option<String>,
        kind: MetricKind,
    ) -> &mut MetricSeries {
        let key = series_key(name, resource);
        self.series.entry(key).or_insert_with(|| {
            MetricSeries::new(name.to_string(), resource.map(|s| s.to_string()), unit, kind)
        })
    }

    pub fn iter(&self) -> impl Iterator<Item = &MetricSeries> {
        self.series.values()
    }

    /// Looks up a series for a query. When `resource` is `None`, returns the first
    /// series with the given name (covers single-resource metrics addressed by name).
    pub fn get(&self, name: &str, resource: Option<&str>) -> Option<&MetricSeries> {
        if let Some(r) = resource {
            return self.series.get(&series_key(name, Some(r)));
        }
        self.series.values().find(|s| s.name == name)
    }

    pub fn clear(&mut self, resource: Option<&str>) {
        if let Some(resource) = resource {
            self.series
                .retain(|_, series| series.resource_name.as_deref() != Some(resource));
        } else {
            self.series.clear();
        }
        self.point_count = self.series.values().map(MetricSeries::point_count).sum();
    }
}

/// Query response: uPlot-friendly aligned arrays. Non-histogram metrics fill
/// `values`; histograms fill `p50`/`p90`/`p99`. All share `timestamps_ms`.
#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct MetricSeriesResponse {
    pub name: String,
    pub resource_name: Option<String>,
    pub unit: Option<String>,
    pub kind: MetricKind,
    pub timestamps_ms: Vec<f64>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub values: Option<Vec<f64>>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub p50: Option<Vec<f64>>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub p90: Option<Vec<f64>>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub p99: Option<Vec<f64>>,
}

impl MetricSeries {
    /// Builds a query response within `window_ms` of the latest sample,
    /// downsampled to at most `max_points` per line.
    pub fn query(&self, window_ms: i64, max_points: usize) -> MetricSeriesResponse {
        let newest = self.samples.back().map(|s| s.t_ms).unwrap_or(0);
        let cutoff = newest - window_ms;
        let in_window: Vec<&Sample> = self
            .samples
            .iter()
            .filter(|s| s.t_ms >= cutoff)
            .collect();

        let mut response = MetricSeriesResponse {
            name: self.name.clone(),
            resource_name: self.resource_name.clone(),
            unit: self.unit.clone(),
            kind: self.kind,
            timestamps_ms: Vec::new(),
            values: None,
            p50: None,
            p90: None,
            p99: None,
        };

        match self.kind {
            MetricKind::Histogram => {
                let mut ts = Vec::new();
                let mut p50 = Vec::new();
                let mut p90 = Vec::new();
                let mut p99 = Vec::new();
                for s in &in_window {
                    if let Some(h) = s.hist {
                        ts.push(s.t_ms as f64);
                        p50.push(h.p50);
                        p90.push(h.p90);
                        p99.push(h.p99);
                    }
                }
                let (ts, lines) = downsample_multi(ts, vec![p50, p90, p99], max_points);
                response.timestamps_ms = ts;
                let mut it = lines.into_iter();
                response.p50 = it.next();
                response.p90 = it.next();
                response.p99 = it.next();
            }
            MetricKind::Counter => {
                // Convert the cumulative counter to a per-second rate between points.
                let mut ts = Vec::new();
                let mut vs = Vec::new();
                for pair in in_window.windows(2) {
                    let (prev, cur) = (pair[0], pair[1]);
                    let dt_s = (cur.t_ms - prev.t_ms) as f64 / 1000.0;
                    if dt_s <= 0.0 {
                        continue;
                    }
                    let rate = if cur.is_delta {
                        cur.value / dt_s
                    } else {
                        let delta = cur.value - prev.value;
                        // A negative delta means the counter reset; treat the new
                        // cumulative value as the interval's observations.
                        if delta < 0.0 { cur.value / dt_s } else { delta / dt_s }
                    };
                    ts.push(cur.t_ms as f64);
                    vs.push(rate);
                }
                let (ts, vs) = downsample(ts, vs, max_points);
                response.timestamps_ms = ts;
                response.values = Some(vs);
            }
            MetricKind::Gauge | MetricKind::UpDownCounter => {
                let ts: Vec<f64> = in_window.iter().map(|s| s.t_ms as f64).collect();
                let vs: Vec<f64> = in_window.iter().map(|s| s.value).collect();
                let (ts, vs) = downsample(ts, vs, max_points);
                response.timestamps_ms = ts;
                response.values = Some(vs);
            }
        }

        response
    }
}

/// Computes p50/p90/p99 from an interval's histogram bucket distribution using
/// linear interpolation within the containing bucket.
///
/// `bounds` are the explicit upper bounds (length N); `counts` are per-bucket
/// counts (length N+1, the last being the +inf overflow bucket). When the top
/// bucket is unbounded its representative value is its lower bound.
pub fn percentiles_from_buckets(bounds: &[f64], counts: &[u64]) -> Option<HistSampleData> {
    let total: u64 = counts.iter().sum();
    if total == 0 || counts.is_empty() {
        return None;
    }
    let pct = |q: f64| -> f64 {
        let rank = q * total as f64;
        let mut cumulative = 0u64;
        for (i, &c) in counts.iter().enumerate() {
            let next = cumulative + c;
            if (next as f64) >= rank {
                // Interpolate within bucket i: (lower, upper].
                let lower = if i == 0 { 0.0 } else { bounds[i - 1] };
                let upper = if i < bounds.len() { bounds[i] } else { lower };
                if c == 0 || upper <= lower {
                    return upper;
                }
                let within = (rank - cumulative as f64) / c as f64;
                return lower + (upper - lower) * within;
            }
            cumulative = next;
        }
        *bounds.last().unwrap_or(&0.0)
    };
    Some(HistSampleData {
        p50: pct(0.50),
        p90: pct(0.90),
        p99: pct(0.99),
    })
}

/// Public mirror of [`HistSample`] returned by [`percentiles_from_buckets`].
#[derive(Debug, Clone, Copy)]
pub struct HistSampleData {
    pub p50: f64,
    pub p90: f64,
    pub p99: f64,
}

// ---------------------------------------------------------------------------
// Downsampling (Largest-Triangle-Three-Buckets)
// ---------------------------------------------------------------------------

/// Downsamples a single line to at most `threshold` points using LTTB, which
/// preserves visual peaks far better than naive striding.
fn downsample(xs: Vec<f64>, ys: Vec<f64>, threshold: usize) -> (Vec<f64>, Vec<f64>) {
    let n = xs.len();
    if threshold == 0 || n <= threshold || n < 3 {
        return (xs, ys);
    }

    let mut out_x = Vec::with_capacity(threshold);
    let mut out_y = Vec::with_capacity(threshold);
    out_x.push(xs[0]);
    out_y.push(ys[0]);

    let bucket_size = (n - 2) as f64 / (threshold - 2) as f64;
    let mut a = 0usize;

    for i in 0..(threshold - 2) {
        let range_start = ((i + 1) as f64 * bucket_size).floor() as usize + 1;
        let range_end = (((i + 2) as f64 * bucket_size).floor() as usize + 1).min(n);

        // Average point of the *next* bucket (the LTTB look-ahead). For the final
        // bucket there is no next bucket, so fall back to the current one.
        let avg_range_start = range_end;
        let avg_range_end = (((i + 3) as f64 * bucket_size).floor() as usize + 1).min(n);
        let (mut avg_x, mut avg_y, mut avg_n) = (0.0, 0.0, 0.0);
        for j in avg_range_start.min(n)..avg_range_end {
            avg_x += xs[j];
            avg_y += ys[j];
            avg_n += 1.0;
        }
        if avg_n == 0.0 {
            for j in range_start..range_end {
                avg_x += xs[j];
                avg_y += ys[j];
                avg_n += 1.0;
            }
        }
        if avg_n > 0.0 {
            avg_x /= avg_n;
            avg_y /= avg_n;
        }

        // Pick the point in the current bucket forming the largest triangle.
        let (mut max_area, mut next_a) = (-1.0, range_start);
        let point_a_x = out_x[out_x.len() - 1];
        let point_a_y = out_y[out_y.len() - 1];
        for j in range_start..range_end {
            let area = ((point_a_x - avg_x) * (ys[j] - point_a_y)
                - (point_a_x - xs[j]) * (avg_y - point_a_y))
                .abs()
                * 0.5;
            if area > max_area {
                max_area = area;
                next_a = j;
            }
        }
        out_x.push(xs[next_a]);
        out_y.push(ys[next_a]);
        a = next_a;
    }

    let _ = a;
    out_x.push(xs[n - 1]);
    out_y.push(ys[n - 1]);
    (out_x, out_y)
}

/// Downsamples multiple y-lines that share the same x-axis, keeping a common set
/// of x indices so the lines stay aligned for uPlot.
fn downsample_multi(xs: Vec<f64>, lines: Vec<Vec<f64>>, threshold: usize) -> (Vec<f64>, Vec<Vec<f64>>) {
    let n = xs.len();
    if threshold == 0 || n <= threshold || n < 3 || lines.is_empty() {
        return (xs, lines);
    }
    // Choose indices using the first line, then sample every line at those indices
    // so all percentile lines share identical timestamps.
    let (kept_x, _) = downsample(xs.clone(), lines[0].clone(), threshold);
    // Map kept x values back to indices (x is strictly increasing).
    let mut idx = Vec::with_capacity(kept_x.len());
    let mut cursor = 0usize;
    for &kx in &kept_x {
        while cursor < n && xs[cursor] < kx {
            cursor += 1;
        }
        idx.push(cursor.min(n - 1));
    }
    let out_lines = lines
        .into_iter()
        .map(|line| idx.iter().map(|&i| line[i]).collect())
        .collect();
    (kept_x, out_lines)
}

// ---------------------------------------------------------------------------
// Recording (called from the OTLP ingestion path)
// ---------------------------------------------------------------------------

impl MetricSeries {
    pub fn record_number(&mut self, t_ms: i64, value: f64, is_delta: bool) {
        self.push(Sample { t_ms, value, is_delta, hist: None });
    }

    /// Records a histogram data point. `bounds`/`counts` are the point's bucket
    /// distribution; for cumulative temporality the per-interval distribution is
    /// derived by diffing against the previous point so percentiles reflect recent
    /// observations rather than the lifetime aggregate.
    pub fn record_histogram_point(&mut self, t_ms: i64, bounds: &[f64], counts: &[u64], is_delta: bool) {
        let interval_counts: Vec<u64> = if is_delta {
            counts.to_vec()
        } else {
            match &self.prev_buckets {
                Some(prev) if prev.len() == counts.len() => counts
                    .iter()
                    .zip(prev.iter())
                    .map(|(c, p)| c.saturating_sub(*p))
                    .collect(),
                // First point, or the bucket layout changed / counter reset: use
                // the cumulative counts directly for this interval.
                _ => counts.to_vec(),
            }
        };
        if !is_delta {
            self.prev_buckets = Some(counts.to_vec());
        }
        if let Some(data) = percentiles_from_buckets(bounds, &interval_counts) {
            self.push(Sample {
                t_ms,
                value: 0.0,
                is_delta: false,
                hist: Some(HistSample { p50: data.p50, p90: data.p90, p99: data.p99 }),
            });
        }
    }
}
