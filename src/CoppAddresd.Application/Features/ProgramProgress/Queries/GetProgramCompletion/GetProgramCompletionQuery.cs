using CoppAddresd.Application.DTOs.ProgramProgress;
using CoppAddresd.Application.Interfaces;
using MediatR;

namespace CoppAddresd.Application.Features.ProgramProgress.Queries.GetProgramCompletion;

/// <summary>El PatientId procede de la identidad autenticada, nunca de la ruta pública.</summary>
public sealed record GetProgramCompletionQuery(Guid PatientId) : IRequest<ProgramCompletionResponse>;

public sealed class GetProgramCompletionQueryHandler(IProgramRepository repository)
    : IRequestHandler<GetProgramCompletionQuery, ProgramCompletionResponse>
{
    public async Task<ProgramCompletionResponse> Handle(GetProgramCompletionQuery request, CancellationToken ct)
        => new(await repository.GetCompletionAsync(request.PatientId, ct));
}
