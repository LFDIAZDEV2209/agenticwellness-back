using CoppAddresd.Auth.Data;
using CoppAddresd.Auth.Entities;
using CoppAddresd.Auth.Interfaces;
using CoppAddresd.Auth.Models;
using CoppAddresd.Auth.Services;
using CoppAddresd.UnitTests.Auth.TestDoubles;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CoppAddresd.UnitTests.Auth.Services;

/// <summary>
/// Vinculación de un alta a una cuenta existente (POST /api/auth/users/link):
/// unión pura de roles y permisos (nada se quita), cuenta inactiva rechazada
/// y preflight del correo. SQLite in-memory con Identity real.
/// </summary>
public class UserServiceLinkTests
{
    private sealed class FakeTokenInvalidationService : ITokenInvalidationService
    {
        public Task InvalidateUserTokensAsync(Guid userId, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task InvalidateUsersTokensAsync(
            IEnumerable<Guid> userIds,
            CancellationToken ct = default
        ) => Task.CompletedTask;
    }

    private sealed class Harness : IDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly ServiceProvider _provider;

        public Harness()
        {
            _connection = new SqliteConnection("DataSource=:memory:");
            _connection.Open();

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddDataProtection();
            services.AddDbContext<AuthDbContext>(options => options.UseSqlite(_connection));
            services
                .AddIdentityCore<ApplicationUser>(options =>
                {
                    options.User.RequireUniqueEmail = true;
                })
                .AddRoles<ApplicationRole>()
                .AddEntityFrameworkStores<AuthDbContext>()
                .AddDefaultTokenProviders();

            services.AddSingleton<ITokenInvalidationService>(new FakeTokenInvalidationService());

            _provider = services.BuildServiceProvider();
            Db.Database.EnsureCreated();
            Db.Applications.Add(new CoppAddresd.Auth.Entities.Application
            {
                Id = Guid.NewGuid(), Code = "erp", Name = "ERP", IsActive = true,
            });
            Db.SaveChanges();

            Users = new UserService(
                UserManager,
                RoleManager,
                Db,
                _provider.GetRequiredService<ITokenInvalidationService>(),
                Logs
            );
        }

        public AuthDbContext Db => _provider.GetRequiredService<AuthDbContext>();
        public UserService Users { get; }
        public RecordingLogger<UserService> Logs { get; } = new();
        public UserManager<ApplicationUser> UserManager =>
            _provider.GetRequiredService<UserManager<ApplicationUser>>();
        public RoleManager<ApplicationRole> RoleManager =>
            _provider.GetRequiredService<RoleManager<ApplicationRole>>();

        public async Task<ApplicationUser> CreateUserAsync(
            string email,
            bool isActive = true,
            string? password = "Test1234!"
        )
        {
            var user = new ApplicationUser
            {
                UserName = email,
                Email = email,
                FirstName = "Test",
                LastName = "User",
                IsActive = isActive,
                EmailConfirmed = true,
            };
            var result = password is null
                ? await UserManager.CreateAsync(user)
                : await UserManager.CreateAsync(user, password);
            Assert.True(result.Succeeded, string.Join(", ", result.Errors.Select(e => e.Description)));
            return user;
        }

        public async Task<ApplicationRole> CreateRoleAsync(string name)
        {
            var role = new ApplicationRole { Name = name };
            var result = await RoleManager.CreateAsync(role);
            Assert.True(result.Succeeded, string.Join(", ", result.Errors.Select(e => e.Description)));
            return role;
        }

        public async Task<Permission> CreatePermissionAsync(string code)
        {
            var permission = new Permission
            {
                Id = Guid.NewGuid(),
                Code = code,
                Name = code,
                Module = "Test",
            };
            Db.Permissions.Add(permission);
            await Db.SaveChangesAsync();
            return permission;
        }

        public void Dispose()
        {
            _provider.Dispose();
            _connection.Dispose();
        }
    }

    [Fact]
    public async Task LinkAsync_CuentaInexistente_DevuelveNotFound()
    {
        using var h = new Harness();

        var (success, error, user, notFound) = await h.Users.LinkAsync(
            new LinkUserAccountRequest { Email = "nadie@test.com" }
        );

        Assert.False(success);
        Assert.True(notFound);
        Assert.Null(user);
        Assert.Equal("Usuario no encontrado", error);
        Assert.DoesNotContain(h.Logs.Messages, m => m.Contains("nadie@test.com", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LinkAsync_CuentaInactiva_DevuelveConflicto()
    {
        using var h = new Harness();
        await h.CreateUserAsync("inactivo@test.com", isActive: false);

        var (success, error, user, notFound) = await h.Users.LinkAsync(
            new LinkUserAccountRequest { Email = "inactivo@test.com" }
        );

        Assert.False(success);
        Assert.False(notFound);
        Assert.Null(user);
        Assert.Contains("inactiva", error!);
        Assert.DoesNotContain(h.Logs.Messages, m => m.Contains("inactivo@test.com", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CreateAsync_CorreoDuplicado_NoLoEscribeEnLogs()
    {
        using var h = new Harness();
        const string duplicate = "duplicate@example.test";
        await h.CreateUserAsync(duplicate);

        var result = await h.Users.CreateAsync(new CreateUserRequest
        {
            Email = duplicate, Password = "Synthetic!Pass2026", FirstName = "Alta", LastName = "Prueba"
        });

        Assert.False(result.Success);
        Assert.Equal("Email ya está registrado", result.Error);
        Assert.NotEmpty(h.Logs.Messages);
        Assert.DoesNotContain(h.Logs.Messages, m => m.Contains(duplicate, StringComparison.Ordinal));
    }

    [Fact]
    public async Task LinkAsync_CuentaActiva_SumaRolesYPermisosSinQuitarLosExistentes()
    {
        using var h = new Harness();
        var user = await h.CreateUserAsync("vincular@test.com");

        var existingRole = await h.CreateRoleAsync("RolExistente");
        var newRole = await h.CreateRoleAsync("RolNuevo");
        await h.UserManager.AddToRoleAsync(user, existingRole.Name!);

        var existingPermission = await h.CreatePermissionAsync("Test.PermisoExistente");
        var newPermission = await h.CreatePermissionAsync("Test.PermisoNuevo");
        h.Db.UserPermissions.Add(
            new UserPermission { UserId = user.Id, PermissionId = existingPermission.Id }
        );
        await h.Db.SaveChangesAsync();

        var (success, error, linked, notFound) = await h.Users.LinkAsync(
            new LinkUserAccountRequest
            {
                Email = "vincular@test.com",
                RoleIds = [newRole.Id],
                PermissionIds = [newPermission.Id],
            }
        );

        Assert.True(success, error);
        Assert.False(notFound);
        Assert.NotNull(linked);

        var roles = await h.UserManager.GetRolesAsync(user);
        Assert.Contains("RolExistente", roles);
        Assert.Contains("RolNuevo", roles);

        var permissionIds = await h.Db.UserPermissions
            .Where(up => up.UserId == user.Id)
            .Select(up => up.PermissionId)
            .ToListAsync();
        Assert.Contains(existingPermission.Id, permissionIds);
        Assert.Contains(newPermission.Id, permissionIds);
    }

    [Fact]
    public async Task GetEmailAvailabilityAsync_CuentaExistente_DevuelveContexto()
    {
        using var h = new Harness();
        var user = await h.CreateUserAsync("cuenta@test.com");

        var result = await h.Users.GetEmailAvailabilityAsync("  cuenta@test.com ");

        Assert.True(result.Exists);
        Assert.True(result.IsActive);
        Assert.True(result.HasPassword);
        Assert.Equal(user.Id, result.UserId);
        Assert.Equal("Test", result.FirstName);
    }

    [Fact]
    public async Task GetEmailAvailabilityAsync_CorreoLibre_DevuelveSinCuenta()
    {
        using var h = new Harness();

        var result = await h.Users.GetEmailAvailabilityAsync("libre@test.com");

        Assert.False(result.Exists);
        Assert.False(result.IsActive);
        Assert.False(result.HasPassword);
        Assert.Null(result.UserId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreateAsync_ConcedeErpSoloCuandoElFlujoLoAutoriza(bool grantErpAccess)
    {
        using var h = new Harness();
        var result = await h.Users.CreateAsync(new CreateUserRequest
        {
            Email = "alta@example.test", Password = "Synthetic!Pass2026",
            FirstName = "Alta", LastName = "Prueba",
        }, grantErpAccess: grantErpAccess);

        Assert.True(result.Success, result.Error);
        Assert.Equal(grantErpAccess, await h.Db.UserApplications.AnyAsync());
        if (grantErpAccess)
            Assert.Equal(Guid.Parse(result.User!.Id), (await h.Db.UserApplications.SingleAsync()).UserId);
    }

    [Fact]
    public async Task CreateAsync_SinErp_NoDejaCuentaHuerfana()
    {
        using var h = new Harness();
        h.Db.Applications.RemoveRange(h.Db.Applications);
        await h.Db.SaveChangesAsync();
        var result = await h.Users.CreateAsync(new CreateUserRequest
        {
            Email = "sin-acceso@example.test", Password = "Synthetic!Pass2026",
            FirstName = "Alta", LastName = "Prueba",
        }, grantErpAccess: true);
        Assert.False(result.Success);
        h.Db.ChangeTracker.Clear();
        Assert.False(await h.Db.Users.AnyAsync());
        Assert.False(await h.Db.UserApplications.AnyAsync());
    }

    [Fact]
    public async Task LinkAsync_ReparaErpSinCambiarPasswordNiAccesoApp_YEsIdempotente()
    {
        using var h = new Harness();
        var user = await h.CreateUserAsync("existente@example.test");
        var passwordHash = user.PasswordHash;
        var app = new CoppAddresd.Auth.Entities.Application { Id = Guid.NewGuid(), Code = "app", Name = "App", IsActive = true };
        h.Db.Applications.Add(app);
        h.Db.UserApplications.Add(new UserApplication { UserId = user.Id, ApplicationId = app.Id, SessionVersion = 7 });
        await h.Db.SaveChangesAsync();

        for (var i = 0; i < 2; i++)
        {
            var result = await h.Users.LinkAsync(new LinkUserAccountRequest { Email = user.Email! });
            Assert.True(result.Success, result.Error);
        }
        Assert.Equal(2, await h.Db.UserApplications.CountAsync());
        Assert.Equal(7, (await h.Db.UserApplications.SingleAsync(a => a.ApplicationId == app.Id)).SessionVersion);
        Assert.Equal(passwordHash, (await h.UserManager.FindByIdAsync(user.Id.ToString()))!.PasswordHash);
    }

    [Fact]
    public async Task LinkAsync_ErrorDeAsignacion_RevierteElAccesoErp()
    {
        using var h = new Harness();
        var user = await h.CreateUserAsync("rollback@example.test");
        var result = await h.Users.LinkAsync(new LinkUserAccountRequest { Email = user.Email!, RoleIds = [Guid.NewGuid()] });
        Assert.False(result.Success);
        h.Db.ChangeTracker.Clear();
        Assert.False(await h.Db.UserApplications.AnyAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RepairAndLink_NuncaReactivanErpSuspendido(bool link)
    {
        using var h = new Harness();
        var user = await h.CreateUserAsync("suspendido@example.test");
        var erp = await h.Db.Applications.SingleAsync();
        h.Db.UserApplications.Add(new UserApplication { UserId = user.Id, ApplicationId = erp.Id, IsSuspended = true, SessionVersion = 9 });
        await h.Db.SaveChangesAsync();
        var success = link
            ? (await h.Users.LinkAsync(new LinkUserAccountRequest { Email = user.Email! })).Success
            : (await h.Users.GrantErpAccessAsync(user.Id)).Success;
        Assert.False(success);
        var access = await h.Db.UserApplications.SingleAsync();
        Assert.True(access.IsSuspended);
        Assert.Equal(9, access.SessionVersion);
    }

    [Fact]
    public async Task GrantErpAccessAsync_ReparaCuentaActivaSinCambiarRolesNiPassword()
    {
        using var h = new Harness();
        var user = await h.CreateUserAsync("reparar@example.test");
        var passwordHash = user.PasswordHash;
        Assert.True((await h.Users.GrantErpAccessAsync(user.Id)).Success);
        Assert.True((await h.Users.GrantErpAccessAsync(user.Id)).Success);
        Assert.Single(await h.Db.UserApplications.ToListAsync());
        Assert.Equal(passwordHash, (await h.UserManager.FindByIdAsync(user.Id.ToString()))!.PasswordHash);
        Assert.Empty(await h.UserManager.GetRolesAsync(user));
    }

    [Fact]
    public async Task GetEmailAvailabilityAsync_EntradaVacia_DevuelveSinCuenta()
    {
        using var h = new Harness();

        var result = await h.Users.GetEmailAvailabilityAsync("   ");

        Assert.False(result.Exists);
        Assert.Null(result.UserId);
    }
}
