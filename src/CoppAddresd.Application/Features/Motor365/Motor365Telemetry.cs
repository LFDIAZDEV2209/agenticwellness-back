using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Text;
using CoppAddresd.Domain.Wellness365;

namespace CoppAddresd.Application.Features.Motor365;

/// <summary>Métricas de baja cardinalidad y trazas seudonimizadas del motor.</summary>
public sealed class Motor365Telemetry(string? traceKey)
{
    public const string SourceName = "CoppAddresd.Motor365";
    private static readonly Meter Meter = new(SourceName);
    private static readonly ActivitySource Activities = new(SourceName);
    private static readonly Histogram<double> Latency = Meter.CreateHistogram<double>("motor365.plan.duration", "ms");
    private static readonly Counter<long> Errors = Meter.CreateCounter<long>("motor365.plan.errors");
    private static readonly Counter<long> Blocked = Meter.CreateCounter<long>("motor365.plan.blocked");
    private static readonly Counter<long> CacheRequests = Meter.CreateCounter<long>("motor365.plan.cache.requests");
    private static readonly Counter<long> CacheWriteFailures = Meter.CreateCounter<long>("motor365.plan.cache.write_failures");
    private static readonly Counter<long> Adjustments = Meter.CreateCounter<long>("motor365.plan.adjustments");
    private static readonly KeyValuePair<string, object?> Version = new("engine.version", WellnessEngine.ReferenceVersion);
    private readonly byte[] _traceKey = string.IsNullOrWhiteSpace(traceKey)
        ? RandomNumberGenerator.GetBytes(32) : Encoding.UTF8.GetBytes(traceKey);

    internal async Task<T> MeasureAsync<T>(string operation, Guid id, Func<Task<T>> work)
    {
        using var span = Activities.StartActivity($"motor365.{operation}");
        if (span?.IsAllDataRequested == true)
        {
            span.SetTag(Version.Key, Version.Value);
            span.SetTag("person.pseudonym", Convert.ToHexStringLower(HMACSHA256.HashData(_traceKey, id.ToByteArray()).AsSpan(0, 16)));
        }
        var started = Stopwatch.GetTimestamp();
        KeyValuePair<string, object?> operationTag = new("operation", operation);
        try
        {
            return await work();
        }
        catch (MotorException ex) when (ex.Code == "plan_locked")
        {
            Blocked.Add(1, operationTag, Version);
            span?.SetTag("outcome", "blocked");
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Errors.Add(1, operationTag, Version);
            span?.SetStatus(ActivityStatusCode.Error);
            throw;
        }
        finally
        {
            Latency.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds, operationTag, Version);
        }
    }

    internal static void Cache(bool hit)
    {
        CacheRequests.Add(1, new("result", hit ? "hit" : "miss"), Version);
        if (Activity.Current?.Source.Name == SourceName) Activity.Current.SetTag("cache.hit", hit);
    }
    internal static void CacheWriteFailed() => CacheWriteFailures.Add(1, Version);
    internal static void Applied(int count)
    {
        if (count > 0) Adjustments.Add(count, Version);
    }
}

public interface IMotor365SnapshotQueue
{
    void TryEnqueue(Guid personId);
}
