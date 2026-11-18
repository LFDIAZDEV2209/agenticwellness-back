using CoppAddresd.Auth.Models;

namespace CoppAddresd.Auth.Interfaces;

public interface IUserService
{
    Task<UserResponse?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<IEnumerable<UserResponse>> GetAllAsync(CancellationToken ct = default);
    /// <summary>grantErpAccess proviene del contexto ERP autorizado, nunca del body público.</summary>
    Task<(bool Success, string? Error, UserResponse? User)> CreateAsync(CreateUserRequest request, CancellationToken ct = default, bool grantErpAccess = false);
    Task<UserEmailAvailabilityResponse> GetEmailAvailabilityAsync(string email, CancellationToken ct = default);
    Task<(bool Success, string? Error, UserResponse? User, bool NotFound)> LinkAsync(LinkUserAccountRequest request, CancellationToken ct = default);
    Task<BulkCreateUsersResult> CreateBulkAsync(BulkCreateUsersRequest request, CancellationToken ct = default, bool grantErpAccess = false);
    Task<(bool Success, string? Error, bool NotFound)> GrantErpAccessAsync(Guid id, CancellationToken ct = default);
    Task<(bool Success, string? Error, bool NotFound)> UpdateAsync(Guid id, UpdateUserRequest request, CancellationToken ct = default);
    Task<(bool Success, string? Error, bool NotFound)> DeleteAsync(Guid id, CancellationToken ct = default);
}
