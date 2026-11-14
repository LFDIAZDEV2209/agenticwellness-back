using CoppAddresd.Application.Features.ProgramProgress.DTOs.ActivityLog;
using CoppAddresd.Application.Interfaces;
using FluentValidation;
using MediatR;

namespace CoppAddresd.Application.Features.ProgramProgress.Queries.Erp;

public sealed record GetWeekPlanChangesQuery(Guid EnrollmentId, int WeekNumber, int Page = 1, int PageSize = 20) : IRequest<WeekPlanChangesDto>;
public sealed class GetWeekPlanChangesValidator : AbstractValidator<GetWeekPlanChangesQuery>
{
    public GetWeekPlanChangesValidator()
    {
        RuleFor(x => x.EnrollmentId).NotEmpty(); RuleFor(x => x.WeekNumber).GreaterThan(0);
        RuleFor(x => x.Page).GreaterThan(0); RuleFor(x => x.PageSize).InclusiveBetween(1, 100);
    }
}
public sealed class GetWeekPlanChangesHandler(IProgramRepository repository, IPatientScopeGuard scope)
    : IRequestHandler<GetWeekPlanChangesQuery, WeekPlanChangesDto>
{
    public async Task<WeekPlanChangesDto> Handle(GetWeekPlanChangesQuery request, CancellationToken ct)
    {
        await scope.EnsureResourceAsync(PatientResource.Enrollment, request.EnrollmentId, "Program.Profile360View", ct);
        return await repository.GetWeekPlanChangesAsync(request.EnrollmentId, request.WeekNumber, request.Page, request.PageSize, ct);
    }
}
