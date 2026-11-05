using System.Reflection;
using System.Runtime.CompilerServices;

namespace Wasla.UnitTests.Terminology;

/// <summary>
/// WAS-139 / WAS-140 under the WAS-134 terminology policy: in the public surface of the Wasla assemblies (type names,
/// public and protected members, and their parameters) "Customer" names only the person who placed an order. The
/// assertion is on the exact set, so a new Customer name fails here until it is reviewed and added as an order-customer
/// concept; a tenant concept uses Tenant naming instead.
/// </summary>
public sealed class OrderCustomerNameInventoryTests
{
    private static readonly string[] AssemblyNames =
        ["Wasla.Domain", "Wasla.Application", "Wasla.Contracts", "Wasla.Infrastructure", "Wasla.Web", "Wasla.Api", "Wasla.Cli"];

    private const BindingFlags Declared =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    /// <summary>Every public name that says Customer: the order's end customer, its receipt visibility and demo name.</summary>
    private static readonly string[] OrderCustomerNames =
    [
        "Wasla.Application.Abstractions.Dashboard.DashboardResult+RecentOrderRow.CustomerName",
        "Wasla.Application.Abstractions.Dashboard.DashboardResult+RecentOrderRow.ctor(CustomerName)",
        "Wasla.Application.Abstractions.Orders.LiveScreenOrderDto.CustomerAddress",
        "Wasla.Application.Abstractions.Orders.LiveScreenOrderDto.CustomerName",
        "Wasla.Application.Abstractions.Orders.LiveScreenOrderDto.CustomerNote",
        "Wasla.Application.Abstractions.Orders.LiveScreenOrderDto.ctor(CustomerAddress)",
        "Wasla.Application.Abstractions.Orders.LiveScreenOrderDto.ctor(CustomerName)",
        "Wasla.Application.Abstractions.Orders.LiveScreenOrderDto.ctor(CustomerNote)",
        "Wasla.Application.Abstractions.Orders.OrderDetailResult.CustomerAddress",
        "Wasla.Application.Abstractions.Orders.OrderDetailResult.CustomerName",
        "Wasla.Application.Abstractions.Orders.OrderDetailResult.CustomerNote",
        "Wasla.Application.Abstractions.Orders.OrderDetailResult.CustomerPhone",
        "Wasla.Application.Abstractions.Orders.OrderListResult+Row.CustomerName",
        "Wasla.Application.Abstractions.Orders.OrderListResult+Row.ctor(CustomerName)",
        "Wasla.Application.Abstractions.Printing.PrintJobHistoryItemDto.OrderCustomerName",
        "Wasla.Application.Abstractions.Printing.PrintJobHistoryItemDto.ctor(OrderCustomerName)",
        "Wasla.Application.Abstractions.Printing.ReceiptTemplateSettings.ShowCustomerName",
        "Wasla.Application.Abstractions.Printing.ReceiptTemplateSettings.ShowCustomerPhone",
        "Wasla.Application.Abstractions.Printing.UpdateReceiptTemplateSettingsCommand.ShowCustomerName",
        "Wasla.Application.Abstractions.Printing.UpdateReceiptTemplateSettingsCommand.ShowCustomerPhone",
        "Wasla.Application.Abstractions.Printing.UpdateReceiptTemplateSettingsCommand.ctor(ShowCustomerName)",
        "Wasla.Application.Abstractions.Printing.UpdateReceiptTemplateSettingsCommand.ctor(ShowCustomerPhone)",
        "Wasla.Application.Demos.GuidedDemoScenario.CustomerNameKey",
        "Wasla.Application.Demos.GuidedDemoScenario.ctor(CustomerNameKey)",
        "Wasla.Application.Demos.GuidedDemoSessionState.CustomerNameKey",
        "Wasla.Application.Demos.GuidedDemoSessionState.ctor(CustomerNameKey)",
        "Wasla.Application.Platform.Dtos.ExternalOrderDto.CustomerAddress",
        "Wasla.Application.Platform.Dtos.ExternalOrderDto.CustomerName",
        "Wasla.Application.Platform.Dtos.ExternalOrderDto.CustomerNote",
        "Wasla.Application.Platform.Dtos.ExternalOrderDto.CustomerPhone",
        "Wasla.Application.Platform.Dtos.ExternalOrderDto.ctor(CustomerAddress)",
        "Wasla.Application.Platform.Dtos.ExternalOrderDto.ctor(CustomerName)",
        "Wasla.Application.Platform.Dtos.ExternalOrderDto.ctor(CustomerNote)",
        "Wasla.Application.Platform.Dtos.ExternalOrderDto.ctor(CustomerPhone)",
        "Wasla.Application.Printing.ReceiptLabelLocalizer.Customer",
        "Wasla.Contracts.Orders.OrderDetailDto.CustomerName",
        "Wasla.Contracts.Orders.OrderDetailDto.ctor(CustomerName)",
        "Wasla.Contracts.Orders.OrderListItemDto.CustomerName",
        "Wasla.Contracts.Orders.OrderListItemDto.ctor(CustomerName)",
        "Wasla.Domain.Entities.TenantDb.GuidedDemoSession.CustomerNameKey",
        "Wasla.Domain.Entities.TenantDb.Order.CustomerAddress",
        "Wasla.Domain.Entities.TenantDb.Order.CustomerName",
        "Wasla.Domain.Entities.TenantDb.Order.CustomerNote",
        "Wasla.Domain.Entities.TenantDb.Order.CustomerPhone",
        "Wasla.Infrastructure.Persistence.Tenant.Migrations.AddOrderCustomerNote",
        "Wasla.Web.Models.Dashboard.DashboardViewModel+RecentOrderRow.CustomerName",
        "Wasla.Web.Models.Orders.OrderDetailViewModel.CustomerAddress",
        "Wasla.Web.Models.Orders.OrderDetailViewModel.CustomerName",
        "Wasla.Web.Models.Orders.OrderDetailViewModel.CustomerNote",
        "Wasla.Web.Models.Orders.OrderDetailViewModel.CustomerPhone",
        "Wasla.Web.Models.Orders.OrderListViewModel+Row.CustomerName",
        "Wasla.Web.Models.PrintBridge.PrintJobHistoryRowViewModel.OrderCustomerName",
    ];

    [Fact]
    public void EveryCustomerNameInThePublicSurface_IsAnOrderCustomerConcept()
    {
        var names = AssemblyNames
            .Select(Assembly.Load)
            .SelectMany(assembly => assembly.GetExportedTypes())
            .SelectMany(PublicNames)
            .Where(name => name.Own.Contains("customer", StringComparison.OrdinalIgnoreCase))
            .Select(name => name.Qualified)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);

        Assert.Equal(OrderCustomerNames.Order(StringComparer.Ordinal), names);
    }

    /// <summary>Each public name with its qualified form: the type, its members, and their parameters.</summary>
    private static IEnumerable<(string Own, string Qualified)> PublicNames(Type type)
    {
        var typeName = type.FullName!;
        yield return (type.Name, typeName);

        foreach (var member in type.GetMembers(Declared))
        {
            if (member is Type || member.IsDefined(typeof(CompilerGeneratedAttribute)) || !IsVisible(member))
                continue;
            if (member is MethodBase { IsSpecialName: true } and not ConstructorInfo)
                continue;

            var memberName = member is ConstructorInfo ? "ctor" : member.Name;
            if (member is not ConstructorInfo)
                yield return (member.Name, $"{typeName}.{memberName}");

            if (member is MethodBase method)
                foreach (var parameter in method.GetParameters())
                    yield return (parameter.Name ?? string.Empty, $"{typeName}.{memberName}({parameter.Name})");
        }
    }

    private static bool IsVisible(MemberInfo member) => member switch
    {
        MethodBase method => method.IsPublic || method.IsFamily || method.IsFamilyOrAssembly,
        FieldInfo field => field.IsPublic || field.IsFamily || field.IsFamilyOrAssembly,
        PropertyInfo property => (property.GetMethod ?? property.SetMethod) is { } accessor && IsVisible(accessor),
        EventInfo @event => @event.AddMethod is { } add && IsVisible(add),
        _ => false,
    };
}
