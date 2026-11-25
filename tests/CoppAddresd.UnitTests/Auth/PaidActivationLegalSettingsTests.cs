using CoppAddresd.Auth.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using NSubstitute;

namespace CoppAddresd.UnitTests.Auth;

public sealed class PaidActivationLegalSettingsTests
{
    private static PaidActivationDemoSettings Read(Dictionary<string, string?> overrides)
    {
        var values = new Dictionary<string, string?>
        {
            ["PaidActivationDemo:Enabled"] = "true",
            ["PaidActivationDemo:WebBaseUrl"] = "http://127.0.0.1:5174",
            ["PaidActivationDemo:AppBaseUrl"] = "http://127.0.0.1:5173",
            ["Email:SmtpHost"] = "127.0.0.1"
        };
        foreach (var (key, value) in overrides) values[key] = value;
        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(Environments.Development);
        return PaidActivationDemoSettings.Read(new ConfigurationBuilder().AddInMemoryCollection(values).Build(), environment, "Smtp");
    }

    [Fact]
    public void Configured_account_documents_replace_demo_codes_and_require_the_exact_pair()
    {
        var settings = Read(new()
        {
            ["AccountLegalConsents:EnforcementEnabled"] = "true",
            ["AccountLegalConsents:Documents:es:Terms"] = "test-public-terms",
            ["AccountLegalConsents:Documents:es:Privacy"] = "test-public-policy",
            ["AccountLegalConsents:Documents:en:Terms"] = "incomplete-en"
        });
        Assert.NotNull(settings.WebOrigin);
        var pair = Assert.IsType<PaidAccountLegalDocumentCodes>(settings.LegalDocumentsFor("es"));
        Assert.Equal("test-public-terms", pair.Terms);
        Assert.Equal("test-public-policy", pair.Privacy);
        Assert.Null(settings.LegalDocumentsFor("en"));
        Assert.Null(settings.LegalDocumentsFor("fr"));
    }

    [Fact]
    public void Disabled_enforcement_preserves_development_fixture_with_no_explicit_map()
    {
        Assert.Equal("paid-demo-privacy-en", Read([]).LegalDocumentsFor("en")?.Privacy);
        Assert.Null(Read(new() { ["AccountLegalConsents:EnforcementEnabled"] = "true" }).LegalDocumentsFor("en"));
    }
}
