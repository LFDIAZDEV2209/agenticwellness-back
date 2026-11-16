using System.Security.Cryptography;
using CoppAddresd.Api.Extensions;
using CoppAddresd.Api.Security;
using CoppAddresd.Application.Features.Agents.Vita;
using CoppAddresd.Domain.Enums.ProgramProgress;
using CoppAddresd.Domain.Exceptions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CoppAddresd.UnitTests.Features.Agents;

public sealed class VitaSharedProtectionTests
{
    private static ServiceProvider Instance(string? secret, bool enabled = true, string? internalKey = null)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Vita:TaskCompletionEnabled"] = enabled.ToString(), ["Vita:TaskProposalSecret"] = secret,
            ["AiService:InternalApiKey"] = internalKey }).Build();
        return new ServiceCollection().AddVita(config).BuildServiceProvider();
    }

    [Fact]
    public void DosInstancias_CompartenClaveConfiguradaYPropositoSinKeyringEfimero()
    {
        var shared = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        using var a = Instance(shared); using var b = Instance(shared);
        var proposal = new VitaTaskProposal(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), TaskCode.podcast,
            new(2026, 11, 14), new string('a', 64), DateTimeOffset.UtcNow.AddMinutes(5));
        var token = a.GetRequiredService<IVitaProposalTokens>().Protect(proposal);
        Assert.Equal(proposal, b.GetRequiredService<IVitaProposalTokens>().Unprotect(token));
        Assert.DoesNotContain(proposal.UserId.ToString(), token);
        using var different = Instance(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        Assert.Throws<ForbiddenException>(() => different.GetRequiredService<IVitaProposalTokens>().Unprotect(token));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("short")]
    public void AccionesActivasSinClaveCompartida_ConfiguracionFallaCerrada(string? key)
    {
        using var services = Instance(key);
        Assert.Throws<OptionsValidationException>(() => services.GetRequiredService<IOptions<VitaTaskProtectionSettings>>().Value);
    }

    [Fact]
    public void ClaveInternaNoSePuedeReutilizarParaPropuestas()
    {
        var shared = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        using var services = Instance(shared, internalKey: shared);
        Assert.Throws<OptionsValidationException>(() => services.GetRequiredService<IOptions<VitaTaskProtectionSettings>>().Value);
    }

    [Fact]
    public void FeatureApagada_NoExigeCrearSecretosNiUsaProtectorEfimero()
    {
        using var services = Instance(null, enabled: false);
        Assert.False(services.GetRequiredService<IOptions<VitaTaskProtectionSettings>>().Value.TaskCompletionEnabled);
        Assert.Throws<InvalidOperationException>(() => services.GetRequiredService<IVitaProposalTokens>());
    }
}
