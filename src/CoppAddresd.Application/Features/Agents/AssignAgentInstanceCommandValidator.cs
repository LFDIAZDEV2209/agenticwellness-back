using FluentValidation;

namespace CoppAddresd.Application.Features.Agents;

/// <summary>Identificadores obligatorios; UserId es auth.users.Id, nunca patient_profiles.Id.</summary>
public sealed class AssignAgentInstanceCommandValidator : AbstractValidator<AssignAgentInstanceCommand>
{
    public AssignAgentInstanceCommandValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.AgentTypeId).NotEmpty();
    }
}
