using CoppAddresd.Application.Interfaces;
using CoppAddresd.Application.Services.ProgramProgress;
using CoppAddresd.Domain.Exceptions;
using MediatR;

namespace CoppAddresd.Application.Features.ProgramProgress.Commands.PublishTemplate;

public sealed record PreviewTemplateImpactQuery(Guid Id) : IRequest<TemplateImpactDto>;

public sealed class PreviewTemplateImpactQueryHandler(IProgramRepository programs, IEffectiveDayContentStore store,
    IEffectiveDayContentResolver resolver, TimeProvider clock) : IRequestHandler<PreviewTemplateImpactQuery, TemplateImpactDto>
{
    public async Task<TemplateImpactDto> Handle(PreviewTemplateImpactQuery request, CancellationToken ct)
    {
        var template = await programs.GetTemplateAsync(request.Id, ct) ?? throw new NotFoundException("Plantilla no encontrada.");
        if (template.Status == CoppAddresd.Domain.Enums.ProgramProgress.TemplateStatus.Archived)
            throw new BusinessRuleViolationException("TEMPLATE_STATE: no se puede publicar una plantilla archivada.");
        var data = await store.LoadScopedActiveAsync(request.Id, "Program.Edit", ct);
        return TemplateImpactCalculator.Calculate(data, resolver, clock.GetUtcNow().UtcDateTime, ct);
    }
}
