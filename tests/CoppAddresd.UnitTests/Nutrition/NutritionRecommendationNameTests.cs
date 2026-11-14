using CoppAddresd.Application.Features.Nutrition.Commands;
using CoppAddresd.Application.Features.Nutrition.Queries;
using CoppAddresd.Application.Interfaces;
using NSubstitute;

namespace CoppAddresd.UnitTests.Nutrition;

public sealed class NutritionRecommendationNameTests
{
    [Theory]
    [InlineData("Daniela Ruiz · Nutricionista", "Daniela Ruiz")]
    [InlineData("Daniela Ruiz · Nutricionista · Registro", "Daniela Ruiz")]
    [InlineData("Daniela Ruiz", "Daniela Ruiz")]
    [InlineData("  Ana-María De la Cruz  ", "Ana-María De la Cruz")]
    [InlineData("", "")]
    public async Task Recomendacion_DevuelveNombreSinRol_EnAmbosIdiomas_SinModificarCabecera(string stored, string expected)
    {
        var user = Guid.NewGuid(); var patient = Guid.NewGuid(); var start = new DateOnly(2026, 11, 1);
        var scope = Substitute.For<IPatientScopeGuard>(); scope.EnsureSelfAsync(user, Arg.Any<CancellationToken>()).Returns(patient);
        var reading = Substitute.For<INutritionCycleReadStore>();
        reading.ReadAsync(Arg.Any<Func<CancellationToken, Task<RecommendationReadData>>>(), Arg.Any<CancellationToken>())
            .Returns(c => c.Arg<Func<CancellationToken, Task<RecommendationReadData>>>()(c.Arg<CancellationToken>()));
        var store = Substitute.For<INutritionRecommendationStore>();
        var header = new RecommendationReadHeader(Guid.NewGuid(), 1, start, start.AddDays(29), new(2026, 12, 1, 18, 0, 0, DateTimeKind.Utc), stored, "{\"es\":\"Mensaje\",\"en\":\"Message\"}");
        store.ReadAsync(patient, Arg.Any<CancellationToken>()).Returns(new RecommendationReadData([header], []));
        var handler = new MyNutritionRecommendationsHandler(scope, reading, store, Substitute.For<INutritionAttachmentUrlSigner>(), TimeProvider.System);
        foreach (var language in new[] { "es", "en" })
        {
            var result = await handler.Handle(new(user, language), default);
            Assert.Equal(expected, result.Current!.ProfessionalName); Assert.Equal(language == "en" ? "Message" : "Mensaje", result.Current.PatientMessage);
        }
        Assert.Equal(stored, header.ProfessionalName);
    }
}
