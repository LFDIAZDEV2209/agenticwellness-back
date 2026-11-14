using CoppAddresd.Application.Features.Agents.Vita;
using CoppAddresd.Application.Interfaces;
using CoppAddresd.Domain.Entities;
using CoppAddresd.Domain.Enums;
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
            new AgentType { Id = Guid.NewGuid(), Status = AgentStatus.Activo } });
        var handler = new GetVitaAgentsQueryHandler(new(patients, Substitute.For<ILegalConsentService>(), TimeProvider.System), reader);
        var result = Assert.Single(await handler.Handle(new(user), default));
        Assert.Equal(id.ToString(), result.RuntimeKey); Assert.Equal(version, result.VersionId);
        Assert.Equal(new[] { "text", "voice" }, result.Routing.Channels);
        var wire = System.Text.Json.JsonSerializer.Serialize(result, VitaVersions.JsonOptions);
        Assert.DoesNotContain("secreto", wire); Assert.DoesNotContain("permissions", wire);
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
