using CoppAddresd.Application.Features.LegalDocuments;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using CoppAddresd.Application.Interfaces;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

sealed class FixturePlanAuth
{
    internal static readonly Guid AppUser = new("71e0e3ce-8d21-4bfa-b20e-3f14b7ad6a01");
    internal static readonly Guid ErpUser = new("71e0e3ce-8d21-4bfa-b20e-3f14b7ad6a02");
    private static readonly Guid UnassignedUser = new("71e0e3ce-8d21-4bfa-b20e-3f14b7ad6a03");
    private readonly SymmetricSecurityKey _key = new(RandomNumberGenerator.GetBytes(32));

    public void Configure(IServiceCollection services)
    {
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
        {
            options.TokenValidationParameters = new()
            {
                ValidIssuer = "motor365-test",
                ValidAudiences = ["app", "erp"],
                IssuerSigningKey = _key,
                ValidateIssuerSigningKey = true
            };
        });
        services.AddAuthorizationBuilder().AddPolicy(CoppAddresd.Api.Controllers.Motor365PlanController.PatientWritePolicy,
            p => p.RequireAuthenticatedUser().RequireClaim("aud", "app"));
        services.AddHttpContextAccessor();
        services.AddScoped<IPatientScopeActor, FixturePlanActor>();
    }

    public string Token(string client)
    {
        var isApp = client == "app";
        var actor = isApp ? AppUser : client == "erp-unassigned" ? UnassignedUser : ErpUser;
        Claim[] claims = [new(JwtRegisteredClaimNames.Sub, actor.ToString()),
            new("allowed", client is "erp" or "erp-unassigned" ? "yes" : "no"),
            new(ClaimTypes.Role, isApp ? "Patient" : "Professional")];
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken("motor365-test",
            isApp ? "app" : "erp", claims, expires: DateTime.UtcNow.AddMinutes(20),
            signingCredentials: new SigningCredentials(_key, SecurityAlgorithms.HmacSha256)));
    }
}

sealed class FixturePlanActor(IHttpContextAccessor http) : IPatientScopeActor
{
    public Guid? UserId => Guid.TryParse(http.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier),
        out var id) ? id : null;
    public bool IsAdmin => false;
    public Task<Guid?> ProfessionalIdAsync(CancellationToken ct) => Task.FromResult(UserId);
    public Task<bool> HasPermissionAsync(string permission, CancellationToken ct) =>
        Task.FromResult((permission is "Program.Profile360View" or "Program.Export" or "Wellness.View") &&
            http.HttpContext?.User.HasClaim("allowed", "yes") == true);
}

sealed class FixturePatientScope(Func<Guid> patient) : IPatientScopeStore
{
    public Task<Guid?> SelfPatientAsync(Guid userId, CancellationToken ct) =>
        Task.FromResult<Guid?>(userId == FixturePlanAuth.AppUser ? patient() : null);
    public IQueryable<Guid> VisiblePatients(Guid? professionalId, bool isAdmin) =>
        (professionalId == FixturePlanAuth.ErpUser ? new[] { patient() } : []).AsQueryable();
    public Task<bool> ContainsAsync(IQueryable<Guid> ids, Guid patientId, CancellationToken ct) =>
        Task.FromResult(ids.Contains(patientId));
    public Task<Guid?> OwnerAsync(PatientResource resource, Guid id, CancellationToken ct) =>
        throw new NotSupportedException();
    public Task<int> CountVisibleNutritionPlansAsync(Guid[] ids, IQueryable<Guid> patients, CancellationToken ct) =>
        throw new NotSupportedException();
}

// El alcance y la membresía usan producción. Los recibos legales son sintéticos.
sealed class FixtureAccountConsent : IAccountLegalConsentService
{
    public Task<AccountLegalConsentStatus> GetJourneyStatusAsync(Guid userId, string locale, CancellationToken ct) =>
        Task.FromResult(new AccountLegalConsentStatus(locale, true, []));
    public Task<AccountLegalConsentStatus> GetStatusAsync(Guid userId, string locale, CancellationToken ct) =>
        GetJourneyStatusAsync(userId, locale, ct);
    public Task<LegalConsentReceiptDto> AcceptAsync(Guid userId, AcceptAccountLegalDocument request,
        AccountLegalAcceptanceMetadata metadata, CancellationToken ct) => throw new NotSupportedException();
}

sealed class FixtureMotorActor(IHttpContextAccessor http, IPatientScopeActor actor)
    : CoppAddresd.Application.Features.Motor365.IMotor365Actor
{
    public Guid? UserId => actor.UserId;
    public bool IsApp => http.HttpContext?.User.HasClaim("aud", "app") == true;
}
