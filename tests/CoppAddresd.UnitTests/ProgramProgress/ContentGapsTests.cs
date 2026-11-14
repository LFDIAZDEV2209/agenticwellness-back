using CoppAddresd.Application.Features.ProgramProgress.Queries.Erp;
using CoppAddresd.Application.Services.ProgramProgress;
using CoppAddresd.Domain.Entities.ProgramProgress;
using NSubstitute;

namespace CoppAddresd.UnitTests.ProgramProgress;

public sealed class ContentGapsTests
{
    [Theory]
    [InlineData(0, 20)] [InlineData(1, 0)] [InlineData(1, 101)]
    public void Validacion_RechazaPaginacionInvalida(int page, int size) =>
        Assert.False(new GetContentGapsQueryValidator().Validate(new GetContentGapsQuery(page, size)).IsValid);

    [Fact]
    public async Task Huecos_UsaFechaLocalNoIncluyePasadoYDescansoNoEsHueco()
    {
        var start = new DateOnly(2026, 11, 2);
        var enrollment = new ProgramEnrollment { Id = Guid.NewGuid(), PatientId = Guid.NewGuid(), StartLocalDate = start,
            Timezone = "America/Bogota", Template = new() { TotalWeeks = 12 } };
        var data = new EffectiveContentData { Enrollments = [enrollment] };
        var store = Substitute.For<IEffectiveDayContentStore>();
        store.LoadScopedActiveAsync(null, "Program.View", Arg.Any<CancellationToken>()).Returns(data);
        var resolver = Substitute.For<IEffectiveDayContentResolver>();
        resolver.ResolveRange(data, enrollment, start, start.AddDays(82), Arg.Any<CancellationToken>()).Returns([
            new(enrollment.Id, enrollment.PatientId, start, 1, 1, new(Guid.NewGuid(), "individual"), new(null, true, "rest"), new(Guid.NewGuid(), "engine")),
            new(enrollment.Id, enrollment.PatientId, start.AddDays(1), 1, 2, EffectiveContentRef.Gap, new(null, true, "rest"), EffectiveContentRef.Gap)]);
        var result = await new GetContentGapsQueryHandler(store, resolver, new Clock()).Handle(new(), CancellationToken.None);
        Assert.Equal(new[] { "podcast", "nut" }, Assert.Single(result.Items).MissingClasses);
    }
    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 11, 3, 2, 0, 0, TimeSpan.Zero);
    }
}
