using System.Collections.Concurrent;
using System.Threading.Channels;
using CoppAddresd.Application.Features.Motor365;

namespace CoppAddresd.Api.Features.Wellness365;

/// <summary>Prepara planes anuales sin bloquear las lecturas diarias ni las escrituras.</summary>
internal sealed class Motor365SnapshotWorker(IServiceScopeFactory scopes, ILogger<Motor365SnapshotWorker> logger)
    : BackgroundService, IMotor365SnapshotQueue
{
    private readonly Channel<Guid> _pending = Channel.CreateBounded<Guid>(new BoundedChannelOptions(128)
    {
        SingleReader = true,
    });
    private readonly ConcurrentDictionary<Guid, byte> _scheduled = new();

    public void TryEnqueue(Guid personId)
    {
        if (_scheduled.TryAdd(personId, 0) && !_pending.Writer.TryWrite(personId))
            _scheduled.TryRemove(personId, out _);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var id in _pending.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<MotorPeople>().WarmPlanAsync(id, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (MotorException ex) when (ex.Code is "plan_locked" or "person_not_found") { }
            catch (Exception ex)
            {
                logger.LogWarning("No se pudo preparar el snapshot Motor 365 ({ErrorType}).", ex.GetType().Name);
            }
            finally
            {
                _scheduled.TryRemove(id, out _);
            }
        }
    }
}
