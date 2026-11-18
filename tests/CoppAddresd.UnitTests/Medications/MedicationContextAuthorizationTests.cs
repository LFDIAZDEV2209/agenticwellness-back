using System.Security.Claims;
using CoppAddresd.Api.Authorization;
using CoppAddresd.Api.Context;
using CoppAddresd.Api.Controllers;
using CoppAddresd.Application.Features.Medications;
using CoppAddresd.Application.Interfaces;
using CoppAddresd.Domain.Exceptions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace CoppAddresd.UnitTests.Medications;

public sealed class MedicationContextAuthorizationTests
{
    [Theory]
    [InlineData(nameof(MedicationControlController.Patients), "Medications.View")]
    [InlineData(nameof(MedicationControlController.Patient), "Medications.View")]
    [InlineData(nameof(MedicationControlController.Access), "Medications.ManageAccess")]
    [InlineData(nameof(MedicationControlController.Create), "Medications.Manage")]
    [InlineData(nameof(MedicationControlController.Record), "Medications.Manage")]
    [InlineData(nameof(MedicationControlController.Status), "Medications.Manage")]
    public async Task ErpActions_UseEffectiveContextPermission_WithoutGlobalClaim(string action, string permission)
    {
        var method = typeof(MedicationControlController).GetMethod(action)!;
        Assert.Empty(method.GetCustomAttributes(true).OfType<RequirePermissionAttribute>());
        var filter = Assert.Single(method.GetCustomAttributes(true).OfType<RequireContextPermissionAttribute>());
        Assert.Equal(permission, filter.PermissionCode);
        var current = Substitute.For<ICurrentContext>();
        current.HasPermissionAsync(permission, Arg.Any<CancellationToken>()).Returns(true);
        using var services = new ServiceCollection().AddSingleton(current).BuildServiceProvider();
        var http = new DefaultHttpContext
        {
            RequestServices = services,
            // El JWT de un rol scoped no trae el permiso entre sus claims globales.
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("aud", "erp")], "test")),
        };
        var context = new AuthorizationFilterContext(new ActionContext(http, new RouteData(), new ActionDescriptor()), []);

        await filter.OnAuthorizationAsync(context);

        Assert.Null(context.Result);
        await current.Received(1).HasPermissionAsync(permission, http.RequestAborted);
        current.HasPermissionAsync(permission, Arg.Any<CancellationToken>()).Returns(false);
        await filter.OnAuthorizationAsync(context);
        Assert.IsType<ForbidResult>(context.Result);
    }

    [Fact]
    public async Task StaffAuthorization_KeepsPatientAssignmentAndClinicChecks()
    {
        var patientId = Guid.NewGuid();
        var clinicId = Guid.NewGuid();
        var organizationId = Guid.NewGuid();
        var scope = Substitute.For<IPatientScopeGuard>();
        var store = Substitute.For<IMedicationControlStore>();
        var settings = Substitute.For<IOptionsSnapshot<MedicationControlSettings>>();
        settings.Value.Returns(new MedicationControlSettings { Enabled = true });
        var service = new MedicationControlService(store, scope, settings, TimeProvider.System);

        await service.EnsureStaffAsync(patientId, "Medications.Manage", clinicId, organizationId, default);

        Received.InOrder(() =>
        {
            scope.EnsureAsync(patientId, "Medications.Manage", CancellationToken.None);
            store.EnsureContextAsync(patientId, clinicId, organizationId, CancellationToken.None);
        });
        scope.EnsureAsync(patientId, "Medications.Manage", Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new ForbiddenException("Sin permiso")));
        store.ClearReceivedCalls();
        await Assert.ThrowsAsync<ForbiddenException>(() => service.EnsureStaffAsync(patientId,
            "Medications.Manage", clinicId, organizationId, default));
        await store.DidNotReceive().EnsureContextAsync(Arg.Any<Guid>(), Arg.Any<Guid?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
    }
}
