using CoppAddresd.Application.Features.Agents.Vita;
using CoppAddresd.Application.Interfaces;
using CoppAddresd.Domain.Entities;
using CoppAddresd.Domain.Enums;
using CoppAddresd.Domain.Exceptions;
using NSubstitute;

namespace CoppAddresd.UnitTests.Features.Agents;

public sealed class VitaCatalogTests
{
    [Fact]
    public async Task Catalogo_UsaGuidVersionActivaYMetadataSinPromptsNiPermisos()
    {
        var user = Guid.NewGuid(); var id = Guid.NewGuid(); var version = Guid.NewGuid();
        var patients = Substitute.For<IPatientRepository>();
        patients.GetByUserIdAsync(user, Arg.Any<CancellationToken>()).Returns(
            new PatientProfile { Id = Guid.NewGuid(), UserId = user, Status = "Activo" });
        var reader = Substitute.For<IVitaAgentCatalogReader>();
        var valid = new AgentType { Id = id, Name = "Especialista nuevo", Slug = "alias-no-unico", Status = AgentStatus.Activo,
            ActiveVersionId = version, ActiveVersion = new() { Id = version, AgentTypeId = id, IsActive = true, VersionNumber = 4, Config = "prompt secreto" },
            Metadata = """{"routing":{"topics":["ejercicio"],"keywords":["rutina"],"channels":["text","voice","admin"],"permissions":["System.AdminSettings"]}}""" };
        reader.ListAuthorizedAsync(user, Arg.Any<CancellationToken>()).Returns(new[] { valid,
            new AgentType { Id = Guid.NewGuid(), Status = AgentStatus.Inactivo },
            new AgentType { Id = Guid.NewGuid(), Status = AgentStatus.Activo },
            new AgentType { Id = id, Status = AgentStatus.Borrador, ActiveVersionId = version, ActiveVersion = valid.ActiveVersion },
            new AgentType { Id = id, Status = AgentStatus.Activo, ActiveVersionId = Guid.NewGuid(), ActiveVersion = valid.ActiveVersion },
            new AgentType { Id = Guid.NewGuid(), Status = AgentStatus.Activo, ActiveVersionId = version, ActiveVersion = valid.ActiveVersion },
            new AgentType { Id = id, Status = AgentStatus.Activo, ActiveVersionId = version,
                ActiveVersion = new() { Id = version, AgentTypeId = id, IsActive = false } } });
        var handler = new GetVitaAgentsQueryHandler(new(patients, Substitute.For<ILegalConsentService>(), TimeProvider.System), reader);
        var result = Assert.Single(await handler.Handle(new(user), default));
        Assert.Equal(id.ToString(), result.RuntimeKey); Assert.Equal(version, result.VersionId);
        Assert.Equal(new[] { "text", "voice" }, result.Routing.Channels);
        var wire = System.Text.Json.JsonSerializer.Serialize(result, VitaVersions.JsonOptions);
        Assert.DoesNotContain("secreto", wire); Assert.DoesNotContain("permissions", wire);
    }

    [Theory]
    [InlineData("inactive")]
    [InlineData("deleted")]
    [InlineData("missing")]
    [InlineData("different-user")]
    public async Task CatalogoComun_NoOmiteEstadoNiIdentidadDelPaciente(string scenario)
    {
        var user = Guid.NewGuid();
        var patients = Substitute.For<IPatientRepository>();
        if (scenario != "missing") patients.GetByUserIdAsync(user, Arg.Any<CancellationToken>()).Returns(
            new PatientProfile { Id = Guid.NewGuid(), UserId = scenario == "different-user" ? Guid.NewGuid() : user,
                Status = scenario == "inactive" ? "Inactivo" : "Activo",
                DeletedAt = scenario == "deleted" ? DateTime.UtcNow : null });
        var reader = Substitute.For<IVitaAgentCatalogReader>(); var consents = Substitute.For<ILegalConsentService>();
        var handler = new GetVitaAgentsQueryHandler(new(patients, consents, TimeProvider.System), reader);
        await Assert.ThrowsAsync<ForbiddenException>(() => handler.Handle(new(user), default));
        Assert.Empty(reader.ReceivedCalls()); Assert.Empty(consents.ReceivedCalls());
    }

    [Fact]
    public async Task CatalogoComun_ConsentimientoRevocado_NoLeeTipos()
    {
        var user = Guid.NewGuid(); var patients = Substitute.For<IPatientRepository>();
        patients.GetByUserIdAsync(user, Arg.Any<CancellationToken>()).Returns(
            new PatientProfile { Id = Guid.NewGuid(), UserId = user, Status = "Activo" });
        var reader = Substitute.For<IVitaAgentCatalogReader>(); var consents = Substitute.For<ILegalConsentService>();
        using var cts = new CancellationTokenSource();
        consents.EnsureUserAsync(user, "chat", cts.Token)
            .Returns(Task.FromException(new AiConsentException("CONSENT_REQUIRED", "chat", 403)));
        var handler = new GetVitaAgentsQueryHandler(new(patients, consents, TimeProvider.System), reader);
        await Assert.ThrowsAsync<AiConsentException>(() => handler.Handle(new(user), cts.Token));
        await patients.Received(1).GetByUserIdAsync(user, cts.Token);
        Assert.Empty(reader.ReceivedCalls());
    }

    [Fact]
    public async Task CatalogoComun_Cancelacion_NoLeePacienteNiTipos()
    {
        var reader = Substitute.For<IVitaAgentCatalogReader>(); var patients = Substitute.For<IPatientRepository>();
        var handler = new GetVitaAgentsQueryHandler(new(patients, Substitute.For<ILegalConsentService>(), TimeProvider.System), reader);
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => handler.Handle(new(Guid.NewGuid()), cts.Token));
        Assert.Empty(patients.ReceivedCalls()); Assert.Empty(reader.ReceivedCalls());
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("invalid")]
    [InlineData("{\"routing\":null}")]
    public void MetadataInvalida_NoAmpliaCapacidades(string metadata)
    {
        var result = GetVitaAgentsQueryHandler.Routing(metadata);
        Assert.Empty(result.Topics); Assert.Empty(result.Keywords);
    }
}
