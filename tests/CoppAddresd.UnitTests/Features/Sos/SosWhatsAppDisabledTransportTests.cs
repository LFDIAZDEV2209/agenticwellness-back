using CoppAddresd.Application.Interfaces;
using CoppAddresd.Infrastructure.Persistence;
using CoppAddresd.Infrastructure.Sos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace CoppAddresd.UnitTests.Features.Sos;

public sealed class SosWhatsAppDisabledTransportTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SinTransporteGlobal_ElHostArrancaYNoProcesaElOutbox(bool sosEnabled)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SosWhatsApp:Enabled"] = sosEnabled.ToString(),
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        // No existe esta DB: el canal desactivado no debe siquiera consultarla.
        services.AddDbContext<AppDbContext>(o => o.UseNpgsql("Host=127.0.0.1;Port=1;Database=disabled_transport;Username=unused"));
        services.AddScoped(_ => Substitute.For<ISosAlertRepository>());
        services.AddSosWhatsApp(config);
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true, ValidateScopes = true,
        });
        using var scope = provider.CreateScope();
        var processor = scope.ServiceProvider.GetRequiredService<SosWhatsAppProcessor>();
        Assert.False(await processor.ProcessNextAsync(CancellationToken.None));
        Assert.Same(processor, scope.ServiceProvider.GetRequiredService<ISosWhatsAppStatusReceiver>());
    }
}
