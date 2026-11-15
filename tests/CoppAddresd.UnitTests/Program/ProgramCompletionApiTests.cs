using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using CoppAddresd.Application.DTOs.ProgramProgress;

namespace CoppAddresd.UnitTests.Program;

public sealed partial class ProgramSelfServicePermissionTests
{
    [Fact]
    public async Task Completion_SinSesion_401()
    {
        await using var factory = NewFactory();
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/program/me/completion")).StatusCode);
    }

    [Fact]
    public async Task Completion_SinProgramaTerminado_200ConNull()
    {
        await using var factory = NewFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", MakePatientToken());
        var response = await client.GetAsync("/api/v1/program/me/completion");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, json.RootElement.GetProperty("completion").ValueKind);
        Assert.Null((await response.Content.ReadFromJsonAsync<ProgramCompletionResponse>())!.Completion);
    }

    [Fact]
    public async Task Completion_PacienteSinRoles_ResumenPropioYAccionManualDisponible()
    {
        var start = new DateOnly(2026, 8, 24);
        var summary = new ProgramCompletionDto(StubProgramActorContext.TestEnrollmentId, "Programa QA", "America/Bogota",
            start, start.AddDays(82), DateTime.UtcNow, 83, 12, 70, 40, 200, 18000, 15, true);
        await using var factory = NewFactory(completion: summary);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", MakePatientToken());
        // Una identidad ajena en query no cambia el paciente resuelto por el actor del JWT.
        var response = await client.GetAsync("/api/v1/program/me/completion?patientId=" + Guid.NewGuid());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(summary, (await response.Content.ReadFromJsonAsync<ProgramCompletionResponse>())!.Completion);
    }
}
