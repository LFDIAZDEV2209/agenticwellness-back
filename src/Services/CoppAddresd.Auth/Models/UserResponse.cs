namespace CoppAddresd.Auth.Models;

/// <summary>
/// Rol con scope (clínica/organización) asignado al usuario.
/// Se agrega de forma aditiva a la lista de usuarios sin romper el
/// shape existente del DTO.
/// </summary>
public record UserScopedRoleResponse(
    string RoleName,
    string ScopeType,
    string? ScopeName);

public record UserPatientLinkResponse(
    Guid Id,
    string? DocumentNumber,
    string? Status);

public record UserResponse(
    string Id,
    string? Email,
    string FirstName,
    string LastName,
    bool IsActive,
    DateTime CreatedAt,
    string[] Roles,
    UserScopedRoleResponse[]? ScopedRoles = null,
    string[]? Applications = null,
    string? PhoneNumber = null,
    UserPatientLinkResponse? Patient = null,
    string AccountType = "erp");
