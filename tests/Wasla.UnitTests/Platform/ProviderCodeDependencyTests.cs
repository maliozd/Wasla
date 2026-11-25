using System.Reflection;
using System.Reflection.Emit;
using Wasla.Application.Abstractions.Platform;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Platform.TrendyolGo;
using Wasla.Infrastructure.Services;

namespace Wasla.UnitTests.Platform;

/// <summary>
/// Provider code depends on provider facts and pure helpers, never on Orders (WAS-232). Provider code is every type in
/// the <c>Wasla.Infrastructure.Platform</c> namespaces, with the nested types the compiler generates for its lambdas,
/// iterators and async methods, except the Orders code that still lives there (<see cref="OrdersCodeInProviderNamespaces"/>).
/// What a type uses is read from its compiled metadata and method bodies, not from its source text, so a reference in
/// a comment does not count and a fully qualified call does.
/// </summary>
public sealed class ProviderCodeDependencyTests
{
    private const string ProviderNamespace = "Wasla.Infrastructure.Platform";

    private static readonly Assembly Infrastructure = typeof(TrendyolGoFoodPlatformClient).Assembly;

    /// <summary>
    /// Orders code in the provider namespaces. It is not provider code and may use Orders; A3 (WAS-233) leaves it outside
    /// the provider library.
    /// </summary>
    private static readonly HashSet<string> OrdersCodeInProviderNamespaces =
    [
        // Orders' status mapping policy: provider status text to OrderStatus.
        "Wasla.Infrastructure.Platform.Mapping.DefaultOrderStatusMapper",
        // The webhook pilot's ingestion side: it stores events in the tenant database, applies them through Orders
        // synchronization, and shows the operator each event with its order's Wasla status.
        "Wasla.Infrastructure.Platform.TrendyolGo.Webhooks.TrendyolGoMealWebhookInbox",
        "Wasla.Infrastructure.Platform.TrendyolGo.Webhooks.TrendyolGoMealWebhookProcessor",
        "Wasla.Infrastructure.Platform.TrendyolGo.Webhooks.TrendyolGoMealWebhookInspection",
        "Wasla.Infrastructure.Platform.TrendyolGo.Webhooks.TrendyolGoMealWebhookEventView",
    ];

    [Fact]
    public void ProviderCode_UsesNoOrdersType()
    {
        var offending = ProviderTypes()
            .SelectMany(type => UsedTypes(type).Where(IsOrdersType).Select(used => $"{type.FullName} uses {used.FullName}"))
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(offending.Count == 0, "Provider code uses Orders types:\n" + string.Join("\n", offending));
    }

    [Fact]
    public void ProviderCode_IsFound_AndTheExceptionsExist()
    {
        var names = ProviderTypes().Select(t => t.FullName).ToHashSet();
        Assert.Contains(typeof(TrendyolGoFoodPlatformClient).FullName, names);
        Assert.Contains("Wasla.Infrastructure.Platform.Mock.MockProviderOrderStatusStore", names);
        Assert.Contains("Wasla.Infrastructure.Platform.TrendyolGo.Webhooks.TrendyolGoMealWebhookPilotOptions", names);

        // A renamed or moved exception would otherwise widen nothing and hide nothing: it must still be there.
        Assert.All(OrdersCodeInProviderNamespaces, name => Assert.NotNull(Infrastructure.GetType(name)));
    }

    /// <summary>
    /// The scan reads method bodies, including the state machines of async methods: <c>OrderActionService</c> checks a
    /// reason with <c>OrderRejectReasons.Refusal</c> inside an async method and has no field or signature of that type.
    /// </summary>
    [Fact]
    public void TheScan_SeesACallInsideAnAsyncMethodBody()
    {
        Assert.Contains(UsedTypes(typeof(OrderActionService)), t => t.FullName == "Wasla.Application.Orders.OrderRejectReasons");
    }

    private static IEnumerable<Type> ProviderTypes() =>
        Infrastructure.GetTypes()
            .Where(t => !t.IsNested
                        && t.Namespace is { } ns
                        && (ns == ProviderNamespace || ns.StartsWith(ProviderNamespace + ".", StringComparison.Ordinal))
                        && !OrdersCodeInProviderNamespaces.Contains(t.FullName!));

    /// <summary>
    /// Orders types: the status mapping contract and the Orders status, everything in the Orders namespaces of
    /// Application, and Orders ingestion and synchronization.
    /// </summary>
    private static bool IsOrdersType(Type type) =>
        type == typeof(IOrderStatusMapper)
        || type == typeof(OrderStatus)
        || type.Namespace is { } ns
           && (InNamespace(ns, "Wasla.Application.Orders")
               || InNamespace(ns, "Wasla.Application.Abstractions.Orders")
               || InNamespace(ns, "Wasla.Infrastructure.Sync"));

    private static bool InNamespace(string ns, string root) =>
        ns == root || ns.StartsWith(root + ".", StringComparison.Ordinal);

    /// <summary>Every type <paramref name="type"/> and its nested types use in signatures, fields, locals and IL.</summary>
    private static IEnumerable<Type> UsedTypes(Type type)
    {
        var used = new HashSet<Type>();
        Collect(type, used);
        return used;
    }

    private const BindingFlags Declared =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    private static void Collect(Type type, HashSet<Type> used)
    {
        Add(type.BaseType, used);
        foreach (var i in type.GetInterfaces()) Add(i, used);
        foreach (var a in type.CustomAttributes) Add(a.AttributeType, used);
        foreach (var f in type.GetFields(Declared)) Add(f.FieldType, used);
        foreach (var p in type.GetProperties(Declared)) Add(p.PropertyType, used);
        foreach (var e in type.GetEvents(Declared)) Add(e.EventHandlerType, used);
        foreach (var method in type.GetMethods(Declared).Cast<MethodBase>().Concat(type.GetConstructors(Declared)))
            CollectMethod(method, used);
        foreach (var nested in type.GetNestedTypes(Declared))
            Collect(nested, used);
    }

    private static void CollectMethod(MethodBase method, HashSet<Type> used)
    {
        if (method is MethodInfo info) Add(info.ReturnType, used);
        foreach (var p in method.GetParameters()) Add(p.ParameterType, used);
        if (method.IsGenericMethodDefinition)
            foreach (var g in method.GetGenericArguments()) Add(g, used);

        var body = method.GetMethodBody();
        if (body is null) return;
        foreach (var local in body.LocalVariables) Add(local.LocalType, used);

        var il = body.GetILAsByteArray() ?? [];
        var typeArguments = method.DeclaringType is { IsGenericType: true } declaring ? declaring.GetGenericArguments() : null;
        var methodArguments = method.IsGenericMethod ? method.GetGenericArguments() : null;
        foreach (var token in MemberTokens(il))
        {
            switch (method.Module.ResolveMember(token, typeArguments, methodArguments))
            {
                case Type t:
                    Add(t, used);
                    break;
                case FieldInfo f:
                    Add(f.DeclaringType, used);
                    Add(f.FieldType, used);
                    break;
                case MethodBase m:
                    Add(m.DeclaringType, used);
                    if (m is MethodInfo mi) Add(mi.ReturnType, used);
                    foreach (var p in m.GetParameters()) Add(p.ParameterType, used);
                    if (m.IsGenericMethod)
                        foreach (var g in m.GetGenericArguments()) Add(g, used);
                    break;
            }
        }
    }

    private static void Add(Type? type, HashSet<Type> used)
    {
        if (type is null) return;
        if (type.HasElementType)
        {
            Add(type.GetElementType(), used);
            return;
        }

        if (type.IsGenericType && !type.IsGenericTypeDefinition)
        {
            foreach (var argument in type.GetGenericArguments()) Add(argument, used);
            Add(type.GetGenericTypeDefinition(), used);
            return;
        }

        used.Add(type);
    }

    // IL reading: the metadata tokens of the field, method, type and ldtoken operands of a method body. ------------

    private static readonly OpCode[] OneByteOpCodes = new OpCode[0x100];
    private static readonly OpCode[] TwoByteOpCodes = new OpCode[0x100];

    static ProviderCodeDependencyTests()
    {
        foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            var opCode = (OpCode)field.GetValue(null)!;
            var value = (ushort)opCode.Value;
            if (opCode.Size == 1)
                OneByteOpCodes[value] = opCode;
            else
                TwoByteOpCodes[value & 0xff] = opCode;
        }
    }

    private static IEnumerable<int> MemberTokens(byte[] il)
    {
        var position = 0;
        while (position < il.Length)
        {
            var opCode = il[position] == 0xfe ? TwoByteOpCodes[il[position + 1]] : OneByteOpCodes[il[position]];
            position += opCode.Size;
            switch (opCode.OperandType)
            {
                case OperandType.InlineField:
                case OperandType.InlineMethod:
                case OperandType.InlineTok:
                case OperandType.InlineType:
                    yield return BitConverter.ToInt32(il, position);
                    position += 4;
                    break;
                case OperandType.InlineNone:
                    break;
                case OperandType.ShortInlineBrTarget:
                case OperandType.ShortInlineI:
                case OperandType.ShortInlineVar:
                    position += 1;
                    break;
                case OperandType.InlineVar:
                    position += 2;
                    break;
                case OperandType.InlineI8:
                case OperandType.InlineR:
                    position += 8;
                    break;
                case OperandType.InlineSwitch:
                    position += 4 + 4 * BitConverter.ToInt32(il, position);
                    break;
                default:
                    // InlineBrTarget, InlineI, InlineSig, InlineString, ShortInlineR.
                    position += 4;
                    break;
            }
        }
    }
}
