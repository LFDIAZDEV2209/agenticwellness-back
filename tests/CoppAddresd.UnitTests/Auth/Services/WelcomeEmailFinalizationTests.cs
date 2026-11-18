using System.Net;
using CoppAddresd.Api.Configuration;
using CoppAddresd.Api.Security;
using CoppAddresd.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CoppAddresd.UnitTests.Auth.Services;

public sealed class WelcomeEmailFinalizationTests
{
    [Fact]
    public async Task Finalization_RetriesAfterProcessRestart_WithoutCreatingAnotherAccount()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var user = Guid.NewGuid();
        var invitation = Guid.NewGuid();
        var handler = new Handler();
        using var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://auth.example.invalid"),
        };
        await using (var db = new AppDbContext(options))
        {
            var client = new AuthInvitationsClient(
                http,
                Options.Create(new AuthServiceSettings()),
                db,
                NullLogger<AuthInvitationsClient>.Instance
            );
            await client.CompleteWelcomeAsync(user, invitation);
            var pending = await db.WelcomeEmailFinalizations.SingleAsync();
            Assert.Null(pending.CompletedAt);
            Assert.Equal(1, pending.Attempts);
        }
        handler.Status = HttpStatusCode.NoContent;
        await using (var db = new AppDbContext(options))
        {
            var client = new AuthInvitationsClient(
                http,
                Options.Create(new AuthServiceSettings()),
                db,
                NullLogger<AuthInvitationsClient>.Instance
            );
            await client.FlushWelcomeAsync(user, default);
            await client.CompleteWelcomeAsync(user, invitation);
            Assert.NotNull((await db.WelcomeEmailFinalizations.SingleAsync()).CompletedAt);
        }
        Assert.Equal(2, handler.Calls.Count);
        Assert.All(
            handler.Calls,
            x => Assert.Equal($"/api/auth/internal/users/{user}/welcome/complete", x)
        );
    }

    private sealed class Handler : HttpMessageHandler
    {
        public HttpStatusCode Status = HttpStatusCode.ServiceUnavailable;
        public List<string> Calls = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            Calls.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(Status));
        }
    }
}
