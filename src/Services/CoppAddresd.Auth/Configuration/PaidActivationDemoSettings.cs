using System.Net;

namespace CoppAddresd.Auth.Configuration;

public sealed record PaidActivationDemoSettings(Uri? WebOrigin, TimeSpan LinkLifetime)
{
    public Uri? AppOrigin { get; init; }
    public string? AppUrl => AppOrigin is null ? null : new Uri(AppOrigin, "/?wellnessJourney=1").ToString();
    public IReadOnlyDictionary<string, PaidAccountLegalDocumentCodes> AccountDocuments { get; init; }
        = new Dictionary<string, PaidAccountLegalDocumentCodes>
        {
            ["es"] = new() { Terms = "paid-demo-terms-es", Privacy = "paid-demo-privacy-es" },
            ["en"] = new() { Terms = "paid-demo-terms-en", Privacy = "paid-demo-privacy-en" }
        };

    public PaidAccountLegalDocumentCodes? LegalDocumentsFor(string language)
    {
        if (language is not ("es" or "en") || !AccountDocuments.TryGetValue(language, out var codes)
            || string.IsNullOrWhiteSpace(codes.Terms) || string.IsNullOrWhiteSpace(codes.Privacy)
            || string.Equals(codes.Terms, codes.Privacy, StringComparison.Ordinal))
            return null;
        return codes;
    }

    public static PaidActivationDemoSettings Read(
        IConfiguration configuration,
        IHostEnvironment environment,
        string emailProvider)
    {
        var lifetime = Math.Clamp(configuration.GetValue("PaidActivationDemo:LinkLifetimeMinutes", 30), 1, 60);
        if (!environment.IsDevelopment()
            || !configuration.GetValue<bool>("PaidActivationDemo:Enabled")
            || !emailProvider.Equals("Smtp", StringComparison.OrdinalIgnoreCase)
            || !IsLoopbackSmtp(configuration["Email:SmtpHost"])
            || !Uri.TryCreate(configuration["PaidActivationDemo:WebBaseUrl"], UriKind.Absolute, out var origin)
            || origin.Scheme is not ("https" or "http")
            || !origin.IsLoopback
            || origin.PathAndQuery != "/"
            || origin.Fragment.Length != 0
            || origin.UserInfo.Length != 0)
        {
            return new(null, TimeSpan.FromMinutes(lifetime));
        }

        var appOrigin = Uri.TryCreate(configuration["PaidActivationDemo:AppBaseUrl"], UriKind.Absolute, out var app)
            && app.Scheme == origin.Scheme && app.Host == origin.Host
            && app.PathAndQuery == "/" && app.Fragment.Length == 0 && app.UserInfo.Length == 0
            ? app
            : null;
        var settings = new PaidActivationDemoSettings(origin, TimeSpan.FromMinutes(lifetime)) { AppOrigin = appOrigin };
        var documents = configuration.GetSection("AccountLegalConsents:Documents");
        if (documents.Exists() || configuration.GetValue<bool>("AccountLegalConsents:EnforcementEnabled"))
        {
            return settings with
            {
                AccountDocuments = documents.Get<Dictionary<string, PaidAccountLegalDocumentCodes>>() ?? []
            };
        }
        return settings;
    }

    private static bool IsLoopbackSmtp(string? host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
        || IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address);
}

public sealed class PaidAccountLegalDocumentCodes
{
    public string Terms { get; set; } = "";
    public string Privacy { get; set; } = "";
}
