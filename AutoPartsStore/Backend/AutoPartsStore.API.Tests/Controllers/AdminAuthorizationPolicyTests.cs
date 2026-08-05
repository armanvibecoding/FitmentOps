using System.Reflection;
using AutoPartsStore.API.Controllers;
using AutoPartsStore.API.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Routing;
using Xunit;

namespace AutoPartsStore.API.Tests.Controllers;

public sealed class AdminAuthorizationPolicyTests
{
    private static readonly IReadOnlyDictionary<(Type Controller, string Action), string>
        ExpectedEndpointPolicies = new Dictionary<(Type, string), string>
        {
            [(typeof(AdminController), nameof(AdminController.GetAllProducts))] = AdminPolicyNames.Catalog,
            [(typeof(AdminController), nameof(AdminController.CreateProduct))] = AdminPolicyNames.Catalog,
            [(typeof(AdminController), nameof(AdminController.UpdateProduct))] = AdminPolicyNames.Catalog,
            [(typeof(AdminController), nameof(AdminController.DeleteProduct))] = AdminPolicyNames.Catalog,
            [(typeof(AdminController), nameof(AdminController.GetAllOrders))] = AdminPolicyNames.OperationsRead,
            [(typeof(AdminController), nameof(AdminController.GetAllPayments))] = AdminPolicyNames.Finance,
            [(typeof(AdminController), nameof(AdminController.UpdateOrderStatus))] = AdminPolicyNames.Support,
            [(typeof(AdminController), nameof(AdminController.MarkPaymentPaid))] = AdminPolicyNames.Finance,
            [(typeof(AdminController), nameof(AdminController.GetAllUsers))] = AdminPolicyNames.SuperAdmin,
            [(typeof(AdminController), nameof(AdminController.UpdateUserRole))] = AdminPolicyNames.SuperAdmin,
            [(typeof(AdminController), nameof(AdminController.GetStats))] = AdminPolicyNames.SuperAdmin,

            [(typeof(AdminOperationsController), nameof(AdminOperationsController.GetShipments))] = AdminPolicyNames.Warehouse,
            [(typeof(AdminOperationsController), nameof(AdminOperationsController.CreateShipment))] = AdminPolicyNames.Warehouse,
            [(typeof(AdminOperationsController), nameof(AdminOperationsController.MarkLabelPending))] = AdminPolicyNames.Warehouse,
            [(typeof(AdminOperationsController), nameof(AdminOperationsController.MarkReadyToShip))] = AdminPolicyNames.Warehouse,
            [(typeof(AdminOperationsController), nameof(AdminOperationsController.MarkShipped))] = AdminPolicyNames.Warehouse,
            [(typeof(AdminOperationsController), nameof(AdminOperationsController.MarkDelivered))] = AdminPolicyNames.Warehouse,
            [(typeof(AdminOperationsController), nameof(AdminOperationsController.MarkShipmentFailed))] = AdminPolicyNames.Warehouse,
            [(typeof(AdminOperationsController), nameof(AdminOperationsController.CancelShipment))] = AdminPolicyNames.Warehouse,
            [(typeof(AdminOperationsController), nameof(AdminOperationsController.GetReturns))] = AdminPolicyNames.Returns,
            [(typeof(AdminOperationsController), nameof(AdminOperationsController.CreateReturn))] = AdminPolicyNames.Returns,
            [(typeof(AdminOperationsController), nameof(AdminOperationsController.ApproveReturn))] = AdminPolicyNames.Returns,
            [(typeof(AdminOperationsController), nameof(AdminOperationsController.RejectReturn))] = AdminPolicyNames.Returns,
            [(typeof(AdminOperationsController), nameof(AdminOperationsController.ReceiveReturn))] = AdminPolicyNames.Returns,
            [(typeof(AdminOperationsController), nameof(AdminOperationsController.InspectReturn))] = AdminPolicyNames.Returns,
            [(typeof(AdminOperationsController), nameof(AdminOperationsController.CancelReturn))] = AdminPolicyNames.Returns,
            [(typeof(AdminOperationsController), nameof(AdminOperationsController.CloseReturn))] = AdminPolicyNames.Returns,
            [(typeof(AdminOperationsController), nameof(AdminOperationsController.GetIntegrationCapabilities))] = AdminPolicyNames.AdminAccess,

            [(typeof(AdminFitmentController), nameof(AdminFitmentController.UpsertVehicle))] = AdminPolicyNames.Catalog,
            [(typeof(AdminFitmentController), nameof(AdminFitmentController.UpsertProductFitment))] = AdminPolicyNames.Catalog,
            [(typeof(AdminFitmentController), nameof(AdminFitmentController.UpsertProductIdentifier))] = AdminPolicyNames.Catalog,
            [(typeof(AdminFitmentController), nameof(AdminFitmentController.GetQuality))] = AdminPolicyNames.Catalog,

            [(typeof(AdminAuditController), nameof(AdminAuditController.GetEvents))] = AdminPolicyNames.SuperAdmin,
            [(typeof(AdminAuditController), nameof(AdminAuditController.VerifyChain))] = AdminPolicyNames.SuperAdmin,
            [(typeof(AdminGarageController), nameof(AdminGarageController.GetSummary))] = AdminPolicyNames.Support,
            [(typeof(AdminGarageController), nameof(AdminGarageController.GetUserGarage))] = AdminPolicyNames.Support,
            [(typeof(AdminLegalController), nameof(AdminLegalController.GetDocuments))] = AdminPolicyNames.AdminAccess,
            [(typeof(AdminLegalController), nameof(AdminLegalController.CreateDraft))] = AdminPolicyNames.SuperAdmin,
            [(typeof(AdminLegalController), nameof(AdminLegalController.Publish))] = AdminPolicyNames.SuperAdmin,
            [(typeof(AdminLegalController), nameof(AdminLegalController.Retire))] = AdminPolicyNames.SuperAdmin
        };

    [Fact]
    public void AdminHttpEndpoints_HaveExactlyTheExpectedPolicyMatrix()
    {
        var controllerTypes = ExpectedEndpointPolicies.Keys
            .Select(key => key.Controller)
            .Distinct()
            .ToArray();
        var discoveredEndpoints = controllerTypes
            .SelectMany(controller => controller
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                .Where(method => method.GetCustomAttributes(inherit: true)
                    .OfType<HttpMethodAttribute>()
                    .Any())
                .Select(method => (Controller: controller, Action: method.Name)))
            .ToHashSet();

        Assert.Equal(
            ExpectedEndpointPolicies.Keys.OrderBy(Format),
            discoveredEndpoints.OrderBy(Format));

        foreach (var (endpoint, expectedPolicy) in ExpectedEndpointPolicies)
        {
            var method = endpoint.Controller.GetMethod(
                endpoint.Action,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
            Assert.NotNull(method);

            var authorization = endpoint.Controller
                .GetCustomAttributes<AuthorizeAttribute>(inherit: true)
                .Concat(method!.GetCustomAttributes<AuthorizeAttribute>(inherit: true))
                .ToArray();
            var effectivePolicies = authorization
                .Select(attribute => attribute.Policy)
                .Where(policy => !string.IsNullOrWhiteSpace(policy))
                .Select(policy => policy!)
                .ToArray();

            Assert.Equal([expectedPolicy], effectivePolicies);
            Assert.All(authorization, attribute => Assert.True(
                string.IsNullOrWhiteSpace(attribute.Roles),
                $"{Format(endpoint)} must use policies instead of a direct role list."));
        }
    }

    [Fact]
    public void RolePermissionIntent_KeepsLegacyAndSuperAdminBroadAndSpecialistsScoped()
    {
        Assert.All(AdminPermissionNames.All, permission =>
        {
            Assert.True(AdminRolePermissionMatrix.IsAllowed(AdminAuditRoles.LegacyAdmin, permission));
            Assert.True(AdminRolePermissionMatrix.IsAllowed(AdminAuditRoles.SuperAdmin, permission));
        });

        var specialistPermissions = new Dictionary<string, string>
        {
            [AdminAuditRoles.Finance] = AdminPermissionNames.FinanceManage,
            [AdminAuditRoles.Warehouse] = AdminPermissionNames.WarehouseManage,
            [AdminAuditRoles.Catalog] = AdminPermissionNames.CatalogManage,
            [AdminAuditRoles.Support] = AdminPermissionNames.SupportManage
        };
        foreach (var (role, expectedPermission) in specialistPermissions)
        {
            Assert.Equal([expectedPermission], AdminRolePermissionMatrix.GetPermissions(role));
            Assert.All(
                AdminPermissionNames.All.Where(permission => permission != expectedPermission),
                permission => Assert.False(AdminRolePermissionMatrix.IsAllowed(role, permission)));
        }
    }

    private static string Format((Type Controller, string Action) endpoint)
    {
        return $"{endpoint.Controller.FullName}.{endpoint.Action}";
    }
}
