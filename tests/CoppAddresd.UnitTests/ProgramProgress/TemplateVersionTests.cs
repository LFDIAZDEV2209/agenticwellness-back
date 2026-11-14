using CoppAddresd.Application.Features.ProgramProgress.Commands.CreateTemplate;
using CoppAddresd.Application.Interfaces;
using CoppAddresd.Domain.Entities.ProgramProgress;
using CoppAddresd.Domain.Enums.ProgramProgress;
using CoppAddresd.Domain.Exceptions;
using NSubstitute;

namespace CoppAddresd.UnitTests.ProgramProgress;

public sealed class TemplateVersionTests
{
    [Fact]
    public void Copia83Celdas_EspecificaAntesQueGenerica_YMetadatosIndependientes()
    {
        var source = new ProgramTemplate { Id = Guid.NewGuid(), Code = "programa", Name = "Fuente", Status = TemplateStatus.Active, TotalWeeks = 12,
            Version = 4, StreakMinTasks = 2, EssentialTaskCodes = ["ejercicio"], PublishedAt = DateTime.UtcNow,
            DayTemplates = Enumerable.Range(1, 7).Select(d => new WeeklyDayTemplate { Id = Guid.NewGuid(), Weekday = (short)d, TaskCode = TaskCode.podcast, Points = 10, MediaId = Guid.NewGuid() }).ToList() };
        var specific = new WeeklyDayTemplate { Id = Guid.NewGuid(), WeekNumber = 2, Weekday = 3, TaskCode = TaskCode.podcast, Points = 99, MediaId = Guid.NewGuid() };
        source.DayTemplates.Add(specific);
        source.DayTemplates.Add(new() { Id = Guid.NewGuid(), Weekday = 1, TaskCode = TaskCode.ejercicio, RoutineId = Guid.NewGuid(), Points = 20 });
        var actor = Guid.NewGuid(); var copy = TemplateVersionCopier.Build(source, actor, default);
        Assert.Equal(TemplateStatus.Draft, copy.Template.Status); Assert.Equal(5, copy.Template.Version); Assert.Null(copy.Template.PublishedAt);
        Assert.Equal(source.Name, copy.Template.Name); Assert.Equal(source.StreakMinTasks, copy.Template.StreakMinTasks); Assert.NotEqual(source.Id, copy.Template.Id); Assert.NotEqual(source.Code, copy.Template.Code);
        Assert.Equal(83, copy.Days.Count(d => d.TaskCode == TaskCode.podcast));
        Assert.DoesNotContain(copy.Days, d => d.WeekNumber == 12 && d.Weekday == 7);
        Assert.Equal(specific.MediaId, Assert.Single(copy.Days, d => d.TaskCode == TaskCode.podcast && d.WeekNumber == 2 && d.Weekday == 3).MediaId);
        Assert.Single(copy.Days, d => d.TaskCode == TaskCode.ejercicio);
        Assert.DoesNotContain(copy.Days, row => source.DayTemplates.Any(old => old.Id == row.Id));
        copy.Template.EssentialTaskCodes.Clear(); Assert.Single(source.EssentialTaskCodes);
        Assert.Equal(TemplateStatus.Active, source.Status); Assert.Equal(4, source.Version);
    }

    [Fact]
    public async Task SinPermiso_NoLeeLaPlantillaNiCreaUnBorrador()
    {
        var repo = Substitute.For<IProgramRepository>(); var scope = Substitute.For<IPatientScopeGuard>();
        scope.GetFilterAsync("Program.Edit", Arg.Any<CancellationToken>()).Returns(Task.FromException<PatientScopeFilter>(new ForbiddenException("Sin permiso")));
        await Assert.ThrowsAsync<ForbiddenException>(() => new CreateTemplateVersionHandler(repo, scope, Substitute.For<IPatientScopeActor>()).Handle(new(Guid.NewGuid()), default));
        await repo.DidNotReceive().GetTemplateAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }
}
