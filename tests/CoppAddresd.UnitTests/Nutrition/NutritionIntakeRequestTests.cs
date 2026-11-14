using System.Text.Json;
using CoppAddresd.Api.Controllers;
using CoppAddresd.Application.Features.ProgramProgress.Commands.LogNutrition;
using CoppAddresd.Application.Features.ProgramProgress.DTOs.Nutrition;
using CoppAddresd.Domain.Enums.ProgramProgress;

namespace CoppAddresd.UnitTests.Nutrition;

public sealed class NutritionIntakeRequestTests
{
    [Fact]
    public void Huella_NormalizaDecimalesYDefaults_PeroDistingueComidaFechaYFoto()
    {
        var p = new NutritionIntakePayload(100m, null, null, null, null, null, null, null);
        var day = new DateOnly(2026, 11, 2);
        string Hash(NutritionIntakePayload? value) => NutritionIntakeRequestFingerprint.Create(MealCode.des, day, value);
        Assert.Equal(Hash(p), Hash(p with { Calories = 100.00m, Source = "manual" }));
        Assert.NotEqual(Hash(p), Hash(p with { FoodAnalysisId = Guid.NewGuid() }));
        Assert.NotEqual(Hash(p), NutritionIntakeRequestFingerprint.Create(MealCode.cen, day, p));
        Assert.NotEqual(Hash(p), NutritionIntakeRequestFingerprint.Create(MealCode.des, day.AddDays(1), p));
    }

    [Fact]
    public void Contrato_ClaveSnakeCaseOpcional_ValidaForma()
    {
        var request = new LogNutritionRequest(MealCode.des, null, null, "photo-retry");
        var json = JsonSerializer.Serialize(request, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains("\"client_request_id\":\"photo-retry\"", json);
        Assert.Equal("photo-retry", JsonSerializer.Deserialize<LogNutritionRequest>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.ClientRequestId);
        var validator = new LogNutritionCommandValidator();
        Assert.True(validator.Validate(new LogNutritionCommand(Guid.NewGuid(), MealCode.des)).IsValid);
        Assert.False(validator.Validate(new LogNutritionCommand(Guid.NewGuid(), MealCode.des, ClientRequestId: " ")).IsValid);
    }
}
