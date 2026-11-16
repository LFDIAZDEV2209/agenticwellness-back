using System.Net;
using System.Security.Claims;
using CoppAddresd.Api.Controllers;
using CoppAddresd.Application.Common;
using CoppAddresd.Application.Features.Agents.Vita;
using CoppAddresd.Application.Features.Threads;
using CoppAddresd.Application.Interfaces;
using CoppAddresd.Domain.Entities;
using CoppAddresd.Domain.Exceptions;
using CoppAddresd.Infrastructure.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace CoppAddresd.UnitTests.Chat;

public sealed class ThreadsVoiceSyncTests
{
    private readonly Guid owner = Guid.NewGuid();
    private readonly IPatientRepository patients = Substitute.For<IPatientRepository>();
    private readonly ILegalConsentService consents = Substitute.For<ILegalConsentService>();
    private readonly IAiServiceClient ai = Substitute.For<IAiServiceClient>();
    private readonly PatientProfile patient;
    private static readonly ThreadStateResult Existing = new("thread", 1, "Texto existente",
        [new("bot", "Texto existente", "message-1")]);
    public ThreadsVoiceSyncTests()
    {
        patient = new() { Id = Guid.NewGuid(), UserId = owner, Status = "Activo" };
        patients.GetByUserIdAsync(owner, Arg.Any<CancellationToken>()).Returns(patient);
        ai.GetThreadStateAsync("thread", owner.ToString(), null, null, Arg.Any<CancellationToken>()).Returns(Existing);
        ai.GetThreadStateAsync("thread", owner.ToString(), null, null, Arg.Any<CancellationToken>(), true).Returns(Existing);
        ai.GetThreadStateWithVoiceSyncAsync("thread", owner.ToString(), null, null, Arg.Any<CancellationToken>(), true)
            .Returns(Existing with { VoiceSyncStatus = "processing" });
    }
    private ThreadsController Controller(string audience = "app") => new(ai, NullLogger<ThreadsController>.Instance,
        new(patients, consents, TimeProvider.System))
    { ControllerContext = new() { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(
        [new(ClaimTypes.NameIdentifier, owner.ToString()), new("aud", audience)], "test")) } } };

    [Fact]
    public async Task SinSyncVoz_RegistraAperturaAppSinChecksNiProcesamientoDeVoz()
    {
        var result = await Controller().GetMessages("thread", Guid.NewGuid().ToString(), null, null, default);
        Assert.Same(Existing, Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Empty(patients.ReceivedCalls()); Assert.Empty(consents.ReceivedCalls());
        await ai.Received(1).GetThreadStateAsync("thread", owner.ToString(), null, null, Arg.Any<CancellationToken>(), true);
        Assert.DoesNotContain(ai.ReceivedCalls(), c => c.GetMethodInfo().Name == nameof(IAiServiceClient.GetThreadStateWithVoiceSyncAsync));
    }

    [Fact]
    public async Task OptInValido_ExigeAmbosConsentimientosYPropietarioJwt()
    {
        using var cts = new CancellationTokenSource();
        var result = await Controller().GetMessages("thread", Guid.NewGuid().ToString(), null, null, cts.Token, syncVoice: true);
        Assert.Equal("processing", Assert.IsType<ThreadStateResult>(Assert.IsType<OkObjectResult>(result.Result).Value).VoiceSyncStatus);
        await consents.Received(1).EnsureUserAsync(owner, "chat", cts.Token);
        await consents.Received(1).EnsureUserAsync(owner, "voice", cts.Token);
        await ai.Received(1).GetThreadStateWithVoiceSyncAsync("thread", owner.ToString(), null, null, cts.Token, true);
        await ai.DidNotReceive().GetThreadStateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>(), true);
    }

    [Theory]
    [InlineData("voice", 403)]
    [InlineData("voice", 503)]
    [InlineData("chat", 403)]
    public async Task ConsentimientoAusenteORevocado_LeeChatNormalSinSync(string scope, int status)
    {
        consents.EnsureUserAsync(owner, scope, Arg.Any<CancellationToken>()).ThrowsAsync(new AiConsentException("CONSENT_REQUIRED", scope, status));
        var result = await Controller().GetMessages("thread", null, null, null, default, syncVoice: true);
        Assert.Same(Existing, Assert.IsType<OkObjectResult>(result.Result).Value);
        await ai.Received(1).GetThreadStateAsync("thread", owner.ToString(), null, null, Arg.Any<CancellationToken>(), true);
        Assert.DoesNotContain(ai.ReceivedCalls(), c => c.GetMethodInfo().Name == nameof(IAiServiceClient.GetThreadStateWithVoiceSyncAsync));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PacienteInactivoOIdentidadDiscordante_NoAutorizaSync(bool inactive)
    {
        if (inactive) patient.Status = "Inactivo"; else patient.UserId = Guid.NewGuid();
        var result = await Controller().GetMessages("thread", null, null, null, default, syncVoice: true);
        Assert.Same(Existing, Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.DoesNotContain(ai.ReceivedCalls(), c => c.GetMethodInfo().Name == nameof(IAiServiceClient.GetThreadStateWithVoiceSyncAsync));
    }

    [Fact]
    public async Task BoolEnSesionErp_NoEsAutorizacionDePaciente()
    {
        var result = await Controller("erp").GetMessages("thread", null, null, null, default, syncVoice: true);
        Assert.Same(Existing, Assert.IsType<OkObjectResult>(result.Result).Value); Assert.Empty(patients.ReceivedCalls());
        await ai.Received(1).GetThreadStateAsync("thread", owner.ToString(), null, null, Arg.Any<CancellationToken>());
        await ai.DidNotReceive().GetThreadStateAsync("thread", owner.ToString(), null, null, Arg.Any<CancellationToken>(), true);
    }

    [Fact]
    public async Task ConsentimientoRevocadoEnFronteraHttp_ConservaTextoSinSync()
    {
        ai.GetThreadStateWithVoiceSyncAsync("thread", owner.ToString(), null, null, Arg.Any<CancellationToken>(), true)
            .ThrowsAsync(new AiConsentException("CONSENT_REQUIRED", "voice", 403));
        var result = await Controller().GetMessages("thread", null, null, null, default, syncVoice: true);
        Assert.Same(Existing, Assert.IsType<OkObjectResult>(result.Result).Value);
        await ai.Received(1).GetThreadStateAsync("thread", owner.ToString(), null, null, Arg.Any<CancellationToken>(), true);
    }

    [Fact]
    public async Task FalloSync_NoBorraChatExistenteYMarcaUnavailable()
    {
        ai.GetThreadStateWithVoiceSyncAsync("thread", owner.ToString(), null, null, Arg.Any<CancellationToken>(), true).ThrowsAsync(new HttpRequestException());
        var result = Assert.IsType<ThreadStateResult>(Assert.IsType<OkObjectResult>((await Controller()
            .GetMessages("thread", null, null, null, default, syncVoice: true)).Result).Value);
        Assert.Equal("unavailable", result.VoiceSyncStatus); Assert.Equal(Existing.Messages, result.Messages);
        await ai.Received(1).GetThreadStateAsync("thread", owner.ToString(), null, null, Arg.Any<CancellationToken>(), true);
    }

    private sealed class Transport : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { Calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetConSync_NoHeredaBypassDeConsentimientoDeGetNormal(bool supportsVita)
    {
        using var services = new ServiceCollection().AddSingleton(patients).AddSingleton(consents)
            .AddSingleton(TimeProvider.System).AddScoped<VitaPatientAccess>().BuildServiceProvider();
        consents.EnsureUserAsync(owner, "voice", Arg.Any<CancellationToken>()).ThrowsAsync(new AiConsentException("CONSENT_REQUIRED", "voice", 403));
        var transport = new Transport();
        using var http = new HttpClient(new AiConsentHttpHandler(services.GetRequiredService<IServiceScopeFactory>(), new HttpContextAccessor())
            { InnerHandler = transport });
        var client = new AiServiceClient(http, Options.Create(new AiServiceSettings { BaseUrl = "http://ai.test" }), NullLogger<AiServiceClient>.Instance);
        await Assert.ThrowsAsync<AiConsentException>(() => client.GetThreadStateWithVoiceSyncAsync("thread", owner.ToString(), null, null, default, supportsVita));
        Assert.Equal(0, transport.Calls);
    }
}
