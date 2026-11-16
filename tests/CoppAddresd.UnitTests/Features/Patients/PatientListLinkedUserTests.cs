using System.Text.Json;
using CoppAddresd.Application.Features.Patients;
using CoppAddresd.Application.Interfaces;
using CoppAddresd.Domain.Entities;
using NSubstitute;

namespace CoppAddresd.UnitTests.Features.Patients;

public sealed class PatientListLinkedUserTests
{
    [Fact]
    public async Task Pagina_ProyectaUserIdVinculadoONullSinCambiarIdentidadAlcanceNiConsultarPorCorreo()
    {
        var linked = new PatientProfile { Id = Guid.NewGuid(), UserId = Guid.NewGuid(), FirstName = "Vinculado", LastName = "Fixture" };
        var unlinked = new PatientProfile { Id = Guid.NewGuid(), UserId = null, FirstName = "Sin cuenta", LastName = "Fixture" };
        var repository = Substitute.For<IPatientRepository>();
        var clinic = Guid.NewGuid(); var professional = Guid.NewGuid(); using var cts = new CancellationTokenSource();
        repository.ListAsync(1, 20, null, null, null, clinic, professional, null, "desc", null, null, cts.Token)
            .Returns(((IReadOnlyList<PatientProfile>)new[] { linked, unlinked }, 2));
        var result = await new ListPatientsQueryHandler(repository).Handle(new(ClinicId: clinic, OwnProfessionalId: professional), cts.Token);
        Assert.Equal(linked.Id, result.Data[0].Id); Assert.Equal(linked.UserId, result.Data[0].UserId);
        Assert.NotEqual(result.Data[0].Id, result.Data[0].UserId); Assert.Null(result.Data[1].UserId);
        var wire = JsonSerializer.SerializeToElement(result, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(linked.UserId, wire.GetProperty("data")[0].GetProperty("userId").GetGuid());
        Assert.Equal(JsonValueKind.Null, wire.GetProperty("data")[1].GetProperty("userId").ValueKind);
        await repository.Received(1).ListAsync(1, 20, null, null, null, clinic, professional, null, "desc", null, null, cts.Token);
        Assert.Single(repository.ReceivedCalls());
    }
}
