using CoppAddresd.Application.Interfaces;
using FluentValidation;
using MediatR;

namespace CoppAddresd.Application.Features.Nutrition.Queries;

public sealed record CycleClosingNoticeDto(Guid Id, Guid CycleId, Guid PatientId, string PatientName, int CycleNumber,
    DateOnly EndDate, DateTimeOffset NotifiedAt, string TitleEs, string TitleEn, string MessageEs, string MessageEn);
public sealed record CycleClosingNoticePage(IReadOnlyList<CycleClosingNoticeDto> Items, int Total, int Page, int PageSize);
public interface INutritionCycleClosingNoticeStore
{
    Task<int> EmitAsync(DateTimeOffset utcNow, CancellationToken ct);
    Task<CycleClosingNoticePage> ListAsync(Guid userId, PatientScopeFilter scope, int page, int pageSize, CancellationToken ct);
}
public sealed record GetCycleClosingNotices(int Page = 1, int PageSize = 20) : IRequest<CycleClosingNoticePage>;
public sealed class CycleClosingNoticesHandler(IPatientScopeGuard scope, IPatientScopeActor actor,
    INutritionCycleClosingNoticeStore store, INutritionCycleReadStore reading) : IRequestHandler<GetCycleClosingNotices, CycleClosingNoticePage>
{
    public async Task<CycleClosingNoticePage> Handle(GetCycleClosingNotices request, CancellationToken ct)
    {
        var filter = await scope.GetFilterAsync("Nutrition.View", ct);
        if (request.Page < 1 || request.PageSize is < 1 or > 100 || (long)(request.Page - 1) * request.PageSize > int.MaxValue)
            throw new ValidationException("Paginación inválida: tamaño 1..100.");
        return await reading.ReadAsync(readCt => store.ListAsync(actor.UserId!.Value, filter, request.Page, request.PageSize, readCt), ct);
    }
}
