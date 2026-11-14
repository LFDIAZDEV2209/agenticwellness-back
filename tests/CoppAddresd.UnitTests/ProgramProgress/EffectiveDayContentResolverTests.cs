using System.Text.Json;
using CoppAddresd.Application.Features.ProgramProgress.Commands.BulkAssignProgramContent;
using CoppAddresd.Application.Services.ProgramProgress;
using CoppAddresd.Domain.Entities;
using CoppAddresd.Domain.Entities.ProgramProgress;
using CoppAddresd.Domain.Enums;
using CoppAddresd.Domain.Enums.ProgramProgress;
using CoppAddresd.Domain.Exceptions;
using NSubstitute;

namespace CoppAddresd.UnitTests.ProgramProgress;

public sealed class EffectiveDayContentResolverTests
{
    private sealed class Case
    {
        public readonly IEffectiveDayContentStore Store = Substitute.For<IEffectiveDayContentStore>();
        public readonly DateOnly Date = new(2026, 11, 2);
        public readonly Guid Generic = Guid.NewGuid(), Specific = Guid.NewGuid(), Personal = Guid.NewGuid();
        public readonly Guid Routine = Guid.NewGuid(), OtherRoutine = Guid.NewGuid(), Plan = Guid.NewGuid();
        public ProgramEnrollment Enrollment { get; }
        public EffectiveContentData Data { get; set; }
        public Case()
        {
            var template = new ProgramTemplate { Id = Guid.NewGuid(), Code = "programa-83d", Name = "General", TotalWeeks = 12 };
            Enrollment = new() { Id = Guid.NewGuid(), PatientId = Guid.NewGuid(), Template = template, TemplateId = template.Id, StartLocalDate = Date };
            template.DayTemplates = Enumerable.Range(1, 7).SelectMany(d => new[] {
                new WeeklyDayTemplate { Weekday = (short)d, TaskCode = TaskCode.podcast, MediaId = Generic },
                new WeeklyDayTemplate { Weekday = (short)d, TaskCode = TaskCode.ejercicio, RoutineId = Routine },
                new WeeklyDayTemplate { Weekday = (short)d, TaskCode = TaskCode.nut, NutritionPlanId = Plan },
            }).ToList();
            var plan = new NutritionPlan { Id = Plan, Name = "General", Status = NutritionPlanStatus.Active,
                Days = Enumerable.Range(1, 7).Select(d => new NutritionPlanDay { DayNumber = (short)d }).ToList() };
            Data = new() { Enrollments = [Enrollment], PublishedPodcasts = new HashSet<Guid> { Generic, Specific, Personal },
                ActiveRoutines = new HashSet<Guid> { Routine, OtherRoutine }, Plans = [new(plan)] };
        }
        public EffectiveDayContent Result(DateOnly? date = null) => new EffectiveDayContentResolver(Store).Resolve(Data, Enrollment, date ?? Date, CancellationToken.None);
        public void Snapshot(params WeekSnapshotTaskRow[] rows) => Data = Data with { Weeks = [new() {
            Id = Guid.NewGuid(), EnrollmentId = Enrollment.Id, WeekNumber = 1,
            TasksSnapshot = WeekSnapshotJson.Serialize(rows), WeekStartDateLocal = Date, WeekEndDateLocal = Date.AddDays(6) }] };
        public void Remove(TaskCode code) => Enrollment.Template!.DayTemplates = Enrollment.Template.DayTemplates.Where(d => d.TaskCode != code).ToList();
        public NutritionPlan LegacyPlan(int menus = 30) => new() { Id = Guid.NewGuid(), Name = "Legado", PatientId = Enrollment.PatientId,
            IsTemplate = false, Status = NutritionPlanStatus.Active, ApprovedAt = Date.ToDateTime(TimeOnly.MinValue), ApprovedBy = Guid.NewGuid(),
            ReviewStatus = "approved", DurationDays = 30, Days = Enumerable.Range(1, menus).Select(d => new NutritionPlanDay { DayNumber = (short)d }).ToList() };
    }

    [Theory]
    [InlineData("individual", true, true, true, "individual")]
    [InlineData(null, true, true, true, "individual")]
    [InlineData("program", true, true, true, "template_week")]
    [InlineData("individual", false, true, true, "template_week")]
    [InlineData("individual", false, false, true, "template_generic")]
    [InlineData("individual", false, false, false, "missing")]
    public void Podcast_PrecedenciaYPublicacion(string? origin, bool personal, bool specific, bool generic, string source)
    {
        var c = new Case(); c.Enrollment.Template!.DayTemplates.Add(new() { Weekday = 1, WeekNumber = 1, TaskCode = TaskCode.podcast, MediaId = c.Specific });
        c.Snapshot(new WeekSnapshotTaskRow(1, "podcast", 10, 1, MediaId: c.Personal, Origin: origin));
        c.Data = c.Data with { PublishedPodcasts = new[] { personal ? c.Personal : Guid.Empty, specific ? c.Specific : Guid.Empty, generic ? c.Generic : Guid.Empty }.ToHashSet() };
        Assert.Equal(source, c.Result().Podcast.Source);
    }

    [Theory]
    [InlineData(ProgramWeekStatus.Active, "snapshot")]
    [InlineData(ProgramWeekStatus.Completed, "snapshot")]
    [InlineData(ProgramWeekStatus.Locked, "template_week")]
    public void PublicarCambio_ConservaProgramaIniciadoYSoloActualizaFuturas(ProgramWeekStatus status, string source)
    {
        var c = new Case();
        c.Enrollment.Template!.DayTemplates.Add(new() { Weekday = 1, WeekNumber = 1, TaskCode = TaskCode.podcast, MediaId = c.Specific });
        c.Snapshot(new WeekSnapshotTaskRow(1, "podcast", 10, 1, MediaId: c.Personal, Origin: "program"));
        c.Data.Weeks[0].Status = status;
        Assert.Equal(source, c.Result().Podcast.Source);
        Assert.Equal(status == ProgramWeekStatus.Locked ? c.Specific : c.Personal, c.Result().Podcast.Id);
        Assert.Equal(c.Personal, c.Data.Weeks[0].TasksSnapshot[0].GetProperty("media_id").GetGuid());
    }

    [Theory]
    [InlineData(true, true, true, "rest")]
    [InlineData(false, true, true, "snapshot")]
    [InlineData(false, false, true, "template_generic")]
    [InlineData(false, false, false, "legacy")]
    public void Rutina_DescansoTerminaCadena(bool rest, bool snapshot, bool template, string source)
    {
        var c = new Case();
        if (!template) c.Remove(TaskCode.ejercicio);
        c.Snapshot(new WeekSnapshotTaskRow(1, "ejercicio", 10, 1, RoutineId: snapshot ? c.OtherRoutine : null, Rest: rest));
        c.Data = c.Data with { Legacy = [new(c.Enrollment.PatientId, c.Routine, c.Date.AddDays(-1), null, false)] };
        var result = c.Result().Routine; Assert.Equal(source, result.Source); Assert.Equal(rest, result.Rest);
        if (rest) Assert.Null(result.Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SemanaNoMaterializadaOVacia_NoEsHuecoYSinTestsRecibeGenerales(bool materialized)
    {
        var c = new Case();
        if (materialized) c.Snapshot();
        Assert.False(c.Result().HasGaps);
        Assert.Equal("engine", c.Result().Nutrition.Source);
        Assert.False(c.Result(c.Date.AddDays(35)).HasGaps);
    }

    [Fact]
    public void PlantillaArchivadaDefault83w_ConservaResolucionYSusLimites()
    {
        var c = new Case(); c.Enrollment.Template!.Code = "default-83w";
        c.Enrollment.Template.TotalWeeks = 83; c.Enrollment.Template.Status = TemplateStatus.Archived;
        var resolver = new EffectiveDayContentResolver(c.Store);
        var days = resolver.ResolveRange(c.Data, c.Enrollment, c.Date, c.Date.AddDays(580), CancellationToken.None);
        Assert.Equal(581, days.Count); Assert.All(days, d => Assert.False(d.HasGaps));
        Assert.Empty(c.Store.ReceivedCalls());
        Assert.Throws<UnprocessableEntityException>(() => c.Result(c.Date.AddDays(581)));
    }

    [Fact]
    public void ProgramaActual_Dia83PermitidoY84Rechazado()
    {
        var c = new Case(); Assert.False(c.Result(c.Date.AddDays(82)).HasGaps);
        Assert.Throws<UnprocessableEntityException>(() => c.Result(c.Date.AddDays(83)));
        Assert.Throws<UnprocessableEntityException>(() => c.Result(c.Date.AddDays(-1)));
    }

    [Fact]
    public void Nutricion_VigenteContinuidadUltimoMenuYSucesoraPorFechaLocal()
    {
        var c = new Case(); var old = Guid.NewGuid(); var next = Guid.NewGuid();
        c.Data = c.Data with { Prescriptions = [new(old, c.Enrollment.PatientId, c.Date.AddDays(-30), c.Date.AddDays(-1), null, [30]),
            new(next, c.Enrollment.PatientId, c.Date.AddDays(2), c.Date.AddDays(31), null, [1, 2])] };
        Assert.Equal(new EffectiveContentRef(old, "continuity", 30, true), c.Result().Nutrition);
        Assert.Equal(old, c.Result(c.Date.AddDays(1)).Nutrition.Id);
        Assert.Equal(new EffectiveContentRef(next, "prescription", 1, true), c.Result(c.Date.AddDays(2)).Nutrition);
    }

    [Fact]
    public void Publicacion_DeVersionSeComparaConFechaLocal_NoConFechaUtc()
    {
        var c = new Case(); var id = Guid.NewGuid();
        c.Data = c.Data with { Prescriptions = [new(id, c.Enrollment.PatientId, c.Date, c.Date.AddDays(29), new DateTime(2026, 11, 3, 2, 0, 0, DateTimeKind.Utc), [1])] };
        c.Enrollment.Timezone = "America/Bogota"; Assert.Equal(id, c.Result().Nutrition.Id);
        c.Enrollment.Timezone = "UTC"; Assert.Equal("engine", c.Result().Nutrition.Source);
    }

    [Fact]
    public void PrescripcionInvalidaOAjena_CaeAlMotorYNuncaFiltraOtroPaciente()
    {
        var c = new Case(); var selected = c.LegacyPlan(); selected.IsTemplate = true; selected.PatientId = null;
        c.Data = c.Data with { Prescriptions = [new(Guid.NewGuid(), Guid.NewGuid(), c.Date, c.Date.AddDays(29), null, [1])],
            Plans = [.. c.Data.Plans, new(selected)], EngineNutrition = new Dictionary<Guid, Guid> { [c.Enrollment.Id] = selected.Id } };
        Assert.Equal(new EffectiveContentRef(selected.Id, "engine", 1), c.Result().Nutrition);
        selected.PatientId = Guid.NewGuid(); Assert.Equal(c.Plan, c.Result().Nutrition.Id);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Nutricion_VersionFinalEnContinuidad_NoReviveOtraVersionSiFaltaMenu(bool complete)
    {
        var c = new Case(); var first = Guid.NewGuid(); var last = Guid.NewGuid();
        c.Data = c.Data with { Prescriptions = [
            new(first, c.Enrollment.PatientId, c.Date, c.Date.AddDays(29), null, [30], 1),
            new(last, c.Enrollment.PatientId, c.Date, c.Date.AddDays(29), null, complete ? [30] : [], 2)] };
        var day = c.Result(c.Date.AddDays(35));
        Assert.Equal(complete ? "continuity" : "engine", day.Nutrition.Source);
        Assert.Equal(complete ? last : c.Plan, day.Nutrition.Id);
        Assert.False(day.Nutrition.Missing);
    }

    [Theory]
    [InlineData(30, 5, 6)]
    [InlineData(7, 10, 4)]
    public void Legado_MenuDayUsaTranscurridos_SinAsumirWeekday(int menus, int elapsed, int expected)
    {
        var c = new Case(); c.Remove(TaskCode.nut); var plan = c.LegacyPlan(menus);
        c.Data = c.Data with { Plans = [new(plan)], Legacy = [new(c.Enrollment.PatientId, plan.Id, c.Date.AddDays(-elapsed), null, true)] };
        Assert.Equal(new EffectiveContentRef(plan.Id, "legacy", expected), c.Result().Nutrition);
        plan.ApprovedBy = null; Assert.True(c.Result().Nutrition.Missing);
        plan.ApprovedBy = Guid.NewGuid(); plan.PatientId = Guid.NewGuid(); Assert.True(c.Result().Nutrition.Missing);
    }

    [Fact]
    public void LegadoVencidoYNingunRespaldo_ProduceHuecoPorClase()
    {
        var c = new Case(); c.Remove(TaskCode.nut); c.Remove(TaskCode.podcast); c.Remove(TaskCode.ejercicio);
        var plan = c.LegacyPlan(); c.Data = c.Data with { Plans = [new(plan)],
            Legacy = [new(c.Enrollment.PatientId, plan.Id, c.Date.AddDays(-30), null, true)] };
        var day = c.Result(); Assert.True(day.Podcast.Missing); Assert.True(day.Routine.Missing); Assert.True(day.Nutrition.Missing);
    }

    [Fact]
    public async Task Cancelacion_AbortaAntesDeLaPrecarga()
    {
        var c = new Case(); using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new EffectiveDayContentResolver(c.Store).ResolveAsync(c.Enrollment.Id, c.Date, cancellation.Token));
        Assert.Empty(c.Store.ReceivedCalls());
    }
    [Theory]
    [InlineData("applied", true)]
    [InlineData("empty", false)]
    [InlineData("malformed", false)]
    public void Motor_DecisionFinalNoRevivePropuesta(string scenario, bool selected)
    {
        var original = Guid.NewGuid(); var final = Guid.NewGuid();
        var json = scenario == "malformed" ? "{" : System.Text.Json.JsonSerializer.Serialize(new
        {
            nutritionPlan = new { id = original },
            appliedContent = new { nutritionPlan = scenario == "applied" ? new { id = final } : null },
        });
        Assert.Equal(selected ? final : (Guid?)null, EffectiveDayContentResolver.EnginePlanId(json));
    }

    [Theory]
    [InlineData(false, false, 1, 0, 0)]
    [InlineData(true, false, 0, 1, 0)]
    [InlineData(false, true, 0, 0, 1)]
    public void Impacto_FuturoPersonalizadoOIniciado(bool personal, bool started, int receivers, int personalized, int omitted)
    {
        var c = new Case(); c.Enrollment.Template!.TotalWeeks = 1;
        c.Remove(TaskCode.ejercicio); c.Remove(TaskCode.nut);
        var rows = Enumerable.Range(1, 7).Select(d => new WeekSnapshotTaskRow((short)d, "podcast", 1, 1,
            MediaId: c.Personal, Origin: personal ? "individual" : "program")).ToList();
        c.Data = c.Data with { Weeks = [new() { EnrollmentId = c.Enrollment.Id, WeekNumber = 1,
            WeekStartDateLocal = c.Date, WeekEndDateLocal = c.Date.AddDays(6),
            Status = started ? ProgramWeekStatus.Active : ProgramWeekStatus.Locked, TasksSnapshot = WeekSnapshotJson.Serialize(rows) }] };
        var before = c.Data.Weeks[0].TasksSnapshot.GetRawText();
        var result = TemplateImpactCalculator.Calculate(c.Data, new EffectiveDayContentResolver(c.Store),
            c.Date.AddDays(-1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc), CancellationToken.None);
        Assert.Equal(new TemplateImpactDto(receivers, personalized, omitted), result);
        Assert.Equal(before, c.Data.Weeks[0].TasksSnapshot.GetRawText());
        Assert.Empty(c.Store.ReceivedCalls());
    }

    [Theory]
    [InlineData(TaskCode.podcast)]
    [InlineData(TaskCode.ejercicio)]
    [InlineData(TaskCode.nut)]
    public void Publicacion_ListaHuecoDelDia83PorClase_SinDia84(TaskCode code)
    {
        var c = new Case(); c.Remove(code);
        for (var d = 1; d <= 82; d++)
            c.Enrollment.Template!.DayTemplates.Add(new() { WeekNumber = (d - 1) / 7 + 1, Weekday = (short)((d - 1) % 7 + 1),
                TaskCode = code, MediaId = c.Generic, RoutineId = c.Routine, NutritionPlanId = c.Plan });
        var resolver = new EffectiveDayContentResolver(c.Store);
        var gaps = CoppAddresd.Application.Features.ProgramProgress.Commands.PublishTemplate.TemplatePublication.Gaps(c.Enrollment.Template!, c.Data, resolver, CancellationToken.None);
        var gap = Assert.Single(gaps); Assert.Equal(83, gap.Day); Assert.Equal(code.ToString(), gap.TaskCode);
        c.Enrollment.Template!.DayTemplates.Add(new() { WeekNumber = 12, Weekday = 6, TaskCode = code,
            MediaId = c.Generic, RoutineId = c.Routine, NutritionPlanId = c.Plan });
        Assert.Empty(CoppAddresd.Application.Features.ProgramProgress.Commands.PublishTemplate.TemplatePublication.Gaps(c.Enrollment.Template, c.Data, resolver, CancellationToken.None));
    }

    [Fact]
    public async Task Publicacion_ProblemDetails422IncluyeListaEstructurada()
    {
        var error = new CoppAddresd.Application.Features.ProgramProgress.Commands.PublishTemplate.TemplateContentGapsException([
            new(83, 12, 6, "podcast")]);
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext(); context.Response.Body = new MemoryStream();
        var middleware = new CoppAddresd.Api.Middleware.ExceptionHandlingMiddleware(_ => throw error,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<CoppAddresd.Api.Middleware.ExceptionHandlingMiddleware>.Instance);
        await middleware.InvokeAsync(context); context.Response.Body.Position = 0;
        using var json = await JsonDocument.ParseAsync(context.Response.Body);
        Assert.Equal(422, context.Response.StatusCode); Assert.StartsWith("application/problem+json", context.Response.ContentType);
        var gap = Assert.Single(json.RootElement.GetProperty("gaps").EnumerateArray());
        Assert.Equal(83, gap.GetProperty("day").GetInt32()); Assert.Equal("podcast", gap.GetProperty("taskCode").GetString());
    }

}
