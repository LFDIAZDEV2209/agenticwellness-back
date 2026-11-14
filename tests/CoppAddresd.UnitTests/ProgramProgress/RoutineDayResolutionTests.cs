using System.Text.Json;
using CoppAddresd.Application.Features.ProgramProgress.Commands.BulkAssignProgramContent;
using CoppAddresd.Application.Interfaces;
using CoppAddresd.Application.Services.ProgramProgress;
using CoppAddresd.Domain.Entities.ProgramProgress;
using CoppAddresd.Domain.Enums.ProgramProgress;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace CoppAddresd.UnitTests.ProgramProgress;

public sealed class RoutineDayResolutionTests
{
    [Theory]
    [InlineData("rest", "rest")]
    [InlineData("snapshot", "snapshot")]
    [InlineData("template", "template_week")]
    [InlineData("legacy", "legacy")]
    [InlineData("old", "legacy")]
    [InlineData("foreign", "missing")]
    public async Task Precedencia_ResolucionSimpleYRangoCoinciden(string scenario, string expected)
    {
        var day = new DateOnly(2026, 11, 2); var snapshotRoutine = Guid.NewGuid(); var templateRoutine = Guid.NewGuid(); var legacyRoutine = Guid.NewGuid();
        var template = new ProgramTemplate { Id = Guid.NewGuid(), TotalWeeks = 12 };
        if (scenario is "rest" or "snapshot" or "template")
            template.DayTemplates.Add(new() { Weekday = 1, WeekNumber = 1, TaskCode = TaskCode.ejercicio, RoutineId = templateRoutine });
        var enrollment = new ProgramEnrollment { Id = Guid.NewGuid(), PatientId = Guid.NewGuid(), StartLocalDate = day, Template = template };
        var rows = scenario == "old" ? JsonSerializer.Deserialize<JsonElement>("""[{"weekday":1,"task_code":"ejercicio","points":10,"sort_order":1}]""")
            : WeekSnapshotJson.Serialize([new(1, "ejercicio", 10, 1, RoutineId: scenario is "rest" or "snapshot" ? snapshotRoutine : null, Rest: scenario == "rest")]);
        var data = new EffectiveContentData { Enrollments = [enrollment], ActiveRoutines = new HashSet<Guid> { snapshotRoutine, templateRoutine, legacyRoutine },
            Weeks = [new() { EnrollmentId = enrollment.Id, WeekNumber = 1, TasksSnapshot = rows }],
            Legacy = [new(scenario == "foreign" ? Guid.NewGuid() : enrollment.PatientId, legacyRoutine, day.AddDays(-1), null, false)] };
        var store = Substitute.For<IEffectiveDayContentStore>();
        store.LoadPatientAsync(enrollment.PatientId, day, day, Arg.Any<CancellationToken>()).Returns(data);
        var effective = new EffectiveDayContentResolver(store);
        var wellness = Substitute.For<IWellnessRepository>(); var prescriptions = Substitute.For<INutritionPrescriptionRepository>();
        var bridge = new ProgramContentResolver(wellness, NullLogger<ProgramContentResolver>.Instance, prescriptions, store, effective);
        var single = await bridge.ResolveAsync(enrollment.PatientId, day);
        var range = await bridge.ResolveRangeAsync(enrollment.PatientId, day, day);
        Assert.Equal(single, range[day]); Assert.Equal(expected, single!.RoutineSource);
        Assert.Equal(scenario == "rest", single.Rest);
        var expectedId = expected switch { "snapshot" => snapshotRoutine, "template_week" => templateRoutine, "legacy" => legacyRoutine, _ => (Guid?)null };
        Assert.Equal(expectedId, single.ExerciseRoutineId);
        Assert.Empty(wellness.ReceivedCalls()); Assert.Empty(prescriptions.ReceivedCalls());
    }

    [Fact]
    public void Descanso_ContradictorioEnSnapshotAntiguo_GanaASuRutinaYAlRespaldo()
    {
        var id = Guid.NewGuid();
        var result = EffectiveRoutineResolver.Resolve(new() { ActiveRoutines = new HashSet<Guid> { id } }, Guid.NewGuid(), new(2026, 11, 2),
            [new(1, "ejercicio", 10, 1, RoutineId: id), new(1, "ejercicio", 10, 1, Rest: true)], []);
        Assert.True(result.Rest); Assert.Null(result.Id);
    }
}
