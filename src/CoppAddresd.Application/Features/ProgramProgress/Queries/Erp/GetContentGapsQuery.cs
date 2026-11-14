using CoppAddresd.Application.Services.ProgramProgress;
using FluentValidation;
using MediatR;

namespace CoppAddresd.Application.Features.ProgramProgress.Queries.Erp;

public sealed record GetContentGapsQuery(int Page = 1, int PageSize = 20) : IRequest<ContentGapsDto>;
public sealed record ContentGapDto(Guid PatientId, Guid EnrollmentId, DateOnly Date, int WeekNumber,
    short Weekday, IReadOnlyList<string> MissingClasses);
public sealed record ContentGapsDto(IReadOnlyList<ContentGapDto> Items, int Total, int Page, int PageSize, int TotalPages);

public sealed class GetContentGapsQueryValidator : AbstractValidator<GetContentGapsQuery>
{
    public GetContentGapsQueryValidator()
    {
        RuleFor(x => x.Page).GreaterThan(0);
        RuleFor(x => x.PageSize).InclusiveBetween(1, 100);
    }
}

/// <summary>Audita activos × días restantes con la misma precedencia que consume el paciente.</summary>
public sealed class GetContentGapsQueryHandler(IEffectiveDayContentStore store,
    IEffectiveDayContentResolver resolver, TimeProvider clock) : IRequestHandler<GetContentGapsQuery, ContentGapsDto>
{
    public async Task<ContentGapsDto> Handle(GetContentGapsQuery request, CancellationToken ct)
    {
        var data = await store.LoadScopedActiveAsync(null, "Program.View", ct);
        var now = clock.GetUtcNow().UtcDateTime;
        var gaps = new List<ContentGapDto>();
        foreach (var enrollment in data.Enrollments.OrderBy(e => e.PatientId).ThenBy(e => e.Id))
        {
            ct.ThrowIfCancellationRequested();
            var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(now,
                TimeZoneInfo.FindSystemTimeZoneById(enrollment.Timezone)));
            var end = enrollment.StartLocalDate.AddDays(EffectiveDayContentResolver.DurationDays(enrollment.Template!) - 1);
            foreach (var day in resolver.ResolveRange(data, enrollment, today, end, ct))
            {
                if (!day.HasGaps) continue;
                var missing = new List<string>(3);
                if (day.Podcast.Missing) missing.Add("podcast");
                if (day.Routine.Missing) missing.Add("ejercicio");
                if (day.Nutrition.Missing) missing.Add("nut");
                gaps.Add(new(day.PatientId, day.EnrollmentId, day.Date, day.WeekNumber, day.Weekday, missing));
            }
        }
        var offset = ((long)request.Page - 1) * request.PageSize;
        return new(offset >= gaps.Count ? [] : gaps.Skip((int)offset).Take(request.PageSize).ToArray(),
            gaps.Count, request.Page, request.PageSize, (int)Math.Ceiling((double)gaps.Count / request.PageSize));
    }
}
