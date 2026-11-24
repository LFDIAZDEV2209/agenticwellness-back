using System.Threading.Channels;
using CoppAddresd.Auth.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Caching.Memory;

namespace CoppAddresd.Auth.Services;

internal sealed record PasswordResetEmailJob(string Email, string Language);

public sealed class PasswordResetEmailQueue(Uri? destination)
{
    private readonly Channel<PasswordResetEmailJob> _jobs =
        Channel.CreateBounded<PasswordResetEmailJob>(
            new BoundedChannelOptions(128) { SingleReader = true }
        );

    public Uri? Destination { get; } = destination;

    internal ChannelReader<PasswordResetEmailJob> Reader => _jobs.Reader;

    public bool TryEnqueue(string email, string language) =>
        Destination is not null && _jobs.Writer.TryWrite(new(email, language));
}

internal sealed class PasswordResetEmailWorker(
    PasswordResetEmailQueue queue,
    IServiceScopeFactory scopes,
    ILogger<PasswordResetEmailWorker> logger
) : BackgroundService
{
    private readonly MemoryCache _cooldowns = new(new MemoryCacheOptions { SizeLimit = 4096 });

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (queue.Destination is not { } destination)
            return;

        await foreach (var job in queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
                var key = users.NormalizeEmail(job.Email);
                if (key is null || _cooldowns.TryGetValue(key, out _))
                    continue;

                _cooldowns.Set(key, true, new MemoryCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1),
                    Size = 1,
                });
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                deadline.CancelAfter(TimeSpan.FromSeconds(20));
                var service = scope.ServiceProvider.GetRequiredService<EmailPasswordResetService>();
                await service.DeliverAsync(key, job.Language, destination, deadline.Token);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error)
            {
                logger.LogWarning("No se pudo procesar un correo de recuperación ({ErrorType}).", error.GetType().Name);
            }
        }
    }

    public override void Dispose()
    {
        base.Dispose();
        _cooldowns.Dispose();
    }
}
