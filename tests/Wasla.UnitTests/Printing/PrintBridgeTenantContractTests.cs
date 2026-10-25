using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Wasla.Application.Abstractions.Printing;
using Wasla.Contracts.Printing;
using ApiPrintBridgeContext = Wasla.Api.Printing.PrintBridgeContext;
using ApiPrintBridgeController = Wasla.Api.Controllers.PrintBridgeController;
using WebPrintBridgeContext = Wasla.Web.PrintBridge.PrintBridgeContext;

namespace Wasla.UnitTests.Printing;

/// <summary>
/// WAS-135: the server side of the Print Bridge contract names the restaurant account as the tenant. The authenticated
/// device context carries the tenant's id and name, and the health endpoints of Web and Api send the name as
/// <c>tenantName</c>. Health reads only the device context, so the controllers get no services here.
/// </summary>
public sealed class PrintBridgeTenantContractTests
{
    private static readonly string[] HealthProperties =
        ["success", "tenantName", "deviceName", "serverTimeUtc", "machineName", "installationId"];

    private static readonly PrintBridgeAuthContext Device = new()
    {
        DeviceId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        TenantName = "Lokanta Wasla",
        DeviceName = "Kasa 1",
        InstallationId = Guid.NewGuid(),
        MachineName = "KASA-PC"
    };

    [Fact]
    public void AuthContext_ExposesTheTenantsIdAndName()
    {
        Assert.Equal(
            [
                ("DeviceId", typeof(Guid)),
                ("TenantId", typeof(Guid)),
                ("TenantName", typeof(string)),
                ("DeviceName", typeof(string)),
                ("InstallationId", typeof(Guid?)),
                ("MachineName", typeof(string))
            ],
            typeof(PrintBridgeAuthContext).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .OrderBy(p => p.MetadataToken)
                .Select(p => (p.Name, p.PropertyType)));
    }

    [Fact]
    public void HealthResponse_SerializesExactlyTheTenantNameAndDeviceProperties()
    {
        var json = Serialize(new PrintBridgeHealthResponse(true, "Lokanta Wasla", "Kasa 1", DateTime.UtcNow, "KASA-PC", Guid.NewGuid()));

        Assert.Equal(HealthProperties, json.Select(p => p.Key));
        Assert.Equal("Lokanta Wasla", (string?)json["tenantName"]);
    }

    [Fact]
    public void WebHealth_SendsTheAuthenticatedDevicesTenantName()
    {
        var http = new DefaultHttpContext();
        WebPrintBridgeContext.Set(http, Device);
        var controller = new Wasla.Web.Controllers.PrintBridgeApiController(null!, null!, null!)
        {
            ControllerContext = new ControllerContext { HttpContext = http }
        };

        AssertHealth(controller.Health());
    }

    [Fact]
    public void ApiHealth_SendsTheAuthenticatedDevicesTenantName()
    {
        var http = new DefaultHttpContext();
        ApiPrintBridgeContext.Set(http, Device);
        var controller = new ApiPrintBridgeController(null!, null!, null!)
        {
            ControllerContext = new ControllerContext { HttpContext = http }
        };

        AssertHealth(controller.Health());
    }

    private static void AssertHealth(IActionResult result)
    {
        var json = Serialize(Assert.IsType<OkObjectResult>(result).Value!);

        Assert.Equal(HealthProperties, json.Select(p => p.Key));
        Assert.True((bool)json["success"]!);
        Assert.Equal(Device.TenantName, (string?)json["tenantName"]);
        Assert.Equal(Device.DeviceName, (string?)json["deviceName"]);
        Assert.Equal(Device.MachineName, (string?)json["machineName"]);
        Assert.Equal(Device.InstallationId, (Guid?)json["installationId"]);
    }

    // ASP.NET Core's JSON defaults: camelCase property names.
    private static JsonObject Serialize(object value) =>
        JsonNode.Parse(JsonSerializer.Serialize(value, value.GetType(), new JsonSerializerOptions(JsonSerializerDefaults.Web)))!.AsObject();
}
