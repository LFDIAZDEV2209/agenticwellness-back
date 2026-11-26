using System.Diagnostics;
using System.Diagnostics.Metrics;

sealed class FixtureMotorDiagnostics : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly ActivityListener _traces = new();
    private TraceSummary? _annual;
    private long _hits;
    private long _misses;

    public FixtureMotorDiagnostics()
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == "CoppAddresd.Motor365" &&
                instrument is Counter<long> { Name: "motor365.plan.cache.requests" })
                listener.EnableMeasurementEvents(instrument);
        };
        _listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key != "result") continue;
                if (Equals(tag.Value, "hit")) Interlocked.Add(ref _hits, value);
                if (Equals(tag.Value, "miss")) Interlocked.Add(ref _misses, value);
            }
        });
        _listener.Start();
        _traces.ShouldListenTo = source => source.Name == "CoppAddresd.Motor365";
        _traces.Sample = (ref ActivityCreationOptions<ActivityContext> options) => ActivitySamplingResult.AllData;
        _traces.ActivityStopped = activity =>
        {
            if (activity.OperationName == "motor365.annual")
                Volatile.Write(ref _annual, new TraceSummary(activity.TagObjects.ToDictionary(tag => tag.Key, tag => tag.Value)));
        };
        ActivitySource.AddActivityListener(_traces);
    }

    public object Read() => new { hits = Interlocked.Read(ref _hits), misses = Interlocked.Read(ref _misses) };

    public object ReadAnnualTrace() => Volatile.Read(ref _annual) ?? new TraceSummary(new Dictionary<string, object?>());

    public void Dispose()
    {
        _traces.Dispose();
        _listener.Dispose();
    }

    private sealed record TraceSummary(IReadOnlyDictionary<string, object?> Tags);
}
