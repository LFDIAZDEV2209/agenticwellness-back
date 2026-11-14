using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CoppAddresd.Application.Features.ProgramProgress.DTOs.Nutrition;
using CoppAddresd.Domain.Enums.ProgramProgress;

namespace CoppAddresd.Application.Features.ProgramProgress.Commands.LogNutrition;

/// <summary>Huella estable del alta; no se modifica cuando se corrige después la comida.</summary>
public static class NutritionIntakeRequestFingerprint
{
    public static string Create(MealCode meal, DateOnly? date, NutritionIntakePayload? intake)
    {
        static string? Number(decimal? value) => value?.ToString("G29", CultureInfo.InvariantCulture);
        var data = JsonSerializer.Serialize(new { meal, date, Calories = Number(intake?.Calories), Protein = Number(intake?.ProteinG),
            Carbs = Number(intake?.CarbsG), Fat = Number(intake?.FatG), Fiber = Number(intake?.FiberG), intake?.WaterMl,
            Source = string.IsNullOrWhiteSpace(intake?.Source) ? "manual" : intake.Source, intake?.FoodAnalysisId });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(data)));
    }
}
