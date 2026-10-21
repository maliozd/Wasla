using System.Reflection;
using System.Text.RegularExpressions;
using Wasla.Application.Abstractions.Billing;
using Wasla.Domain.Billing;
using Wasla.Domain.Entities.Central;

namespace Wasla.UnitTests.Billing;

/// <summary>
/// Architecture and data-minimization boundaries of the billing model (WAS-147): the domain and application contracts
/// are built from Wasla and BCL types only, so no provider transport type can leak into them, and the billing records
/// have no field for card data, contact data, secrets or provider payloads.
/// </summary>
public sealed class BillingBoundaryTests
{
    private static readonly Assembly Domain = typeof(BillingSubscription).Assembly;
    private static readonly Assembly Application = typeof(IBillingGateway).Assembly;

    private static IEnumerable<Type> BillingContractTypes() =>
        Application.GetTypes()
            .Where(t => t.Namespace == typeof(IBillingGateway).Namespace)
            .Concat(Domain.GetTypes().Where(t => t.Namespace == typeof(BillingPrice).Namespace))
            .Concat([typeof(BillingSubscription), typeof(BillingPayment)])
            .Where(t => t.IsPublic);

    [Fact]
    public void DomainAndApplication_ReferenceNoInfrastructureEfOrProviderAssembly()
    {
        foreach (var assembly in new[] { Domain, Application })
        {
            var references = assembly.GetReferencedAssemblies().Select(a => a.Name ?? string.Empty).ToArray();
            Assert.DoesNotContain(references, name => name.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal));
            Assert.DoesNotContain(references, name => name.Contains("Iyzi", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(references, name => name is "Wasla.Infrastructure" or "Wasla.Web" or "Wasla.Api" or "Wasla.Contracts");
        }

        Assert.All(Domain.GetReferencedAssemblies(), a => Assert.True(
            a.Name!.StartsWith("System", StringComparison.Ordinal) || a.Name == "netstandard",
            $"Wasla.Domain references {a.Name}"));
    }

    [Fact]
    public void NoProviderSpecificType_LivesInDomainOrApplication()
    {
        var providerTypes = Domain.GetTypes().Concat(Application.GetTypes())
            .Where(t => t.FullName!.Contains("Iyzico", StringComparison.OrdinalIgnoreCase))
            .Select(t => t.FullName);

        Assert.Empty(providerTypes);
    }

    [Fact]
    public void BillingContracts_UseOnlyWaslaAndBclTypes()
    {
        var offending = new List<string>();
        foreach (var type in BillingContractTypes())
        {
            var members = type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
            foreach (var member in members)
            {
                IEnumerable<Type> used = member switch
                {
                    PropertyInfo p => [p.PropertyType],
                    FieldInfo f => [f.FieldType],
                    MethodInfo m => m.GetParameters().Select(p => p.ParameterType).Append(m.ReturnType),
                    ConstructorInfo c => c.GetParameters().Select(p => p.ParameterType),
                    _ => []
                };
                foreach (var t in used.SelectMany(Flatten))
                {
                    if (!IsAllowed(t))
                        offending.Add($"{type.Name}.{member.Name}: {t.FullName}");
                }
            }
        }

        Assert.Empty(offending);
    }

    [Fact]
    public void BillingRecords_HaveNoFieldForCardContactSecretOrPayloadData()
    {
        var forbidden = new Regex(
            "card|pan$|cvv|cvc|expir|holder|iban|email|phone|address|name$|token|secret|password|apikey|payload|raw|response|request|signature",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        var fields = new[] { typeof(BillingSubscription), typeof(BillingPayment) }
            .SelectMany(t => t.GetProperties().Select(p => $"{t.Name}.{p.Name}"))
            .Where(name => forbidden.IsMatch(name.Split('.')[1]));

        Assert.Empty(fields);
    }

    private static IEnumerable<Type> Flatten(Type type)
    {
        if (type.IsByRef || type.IsArray)
            return Flatten(type.GetElementType()!);
        if (type.IsGenericType)
            return type.GetGenericArguments().SelectMany(Flatten).Append(type.GetGenericTypeDefinition());
        return [type];
    }

    private static bool IsAllowed(Type type) =>
        type.IsGenericParameter
        || type.Namespace is null
        || type.Namespace == "System"
        || type.Namespace.StartsWith("System.Collections", StringComparison.Ordinal)
        || type.Namespace == "System.Threading" || type.Namespace == "System.Threading.Tasks"
        || type.Namespace == "System.Runtime.CompilerServices"
        || type.Namespace.StartsWith("Wasla.Domain", StringComparison.Ordinal)
        || type.Namespace.StartsWith("Wasla.Application.Abstractions.Billing", StringComparison.Ordinal);
}
