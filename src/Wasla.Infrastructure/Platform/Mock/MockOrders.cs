using System.Collections.Concurrent;
using System.Text.Json;
using Wasla.Application.Platform.Dtos;
using Wasla.Domain.Entities.Customer;
using Wasla.Domain.Enums;

namespace Wasla.Infrastructure.Platform.Mock;

internal static class MockOrders
{
    private static readonly ConcurrentDictionary<FoodPlatform, ConcurrentQueue<string>> _knownExternalOrderIds = new();

    /// <summary>
    /// Realistic Turkish first+last names for mock/demo orders (not "Customer NNNN").
    /// </summary>
    internal static readonly string[] CustomerNames =
    [
        "Ayşe Yılmaz",
        "Mehmet Demir",
        "Fatma Kaya",
        "Ali Çelik",
        "Zeynep Şahin",
        "Mustafa Arslan",
        "Elif Doğan",
        "Hüseyin Öztürk",
        "Emine Aydın",
        "Ahmet Yıldız",
        "Hatice Özdemir",
        "İbrahim Koç",
        "Merve Aksoy",
        "Yusuf Erdoğan",
        "Seda Çetin",
        "Burak Aslan",
        "Gamze Polat",
        "Canan Kurt",
        "Emre Güneş",
        "Selin Taş",
        "Oğuz Kara",
        "Deniz Bulut",
        "Ceren Akın",
        "Serkan Yavuz",
        "Pınar Tekin",
        "Barış Uçar",
        "Gülşen Avcı",
        "Tolga Şimşek",
        "Derya Bozkurt",
        "Hakan Mutlu",
        "Esra Karaca",
        "Volkan Şen",
        "Nihan Ergin",
        "Kemal Acar",
        "Burcu Işık",
        "Onur Bayram",
        "Aylin Koç",
        "Murat Yalçın",
        "Seher Aslan",
        "Cemre Uysal",
        "Halil İnan",
        "Büşra Demirtaş",
        "Kadir Özkan",
        "Yasemin Acar",
        "Tuncay Güler",
        "Melis Karataş",
        "Orhan Yücel",
        "Şeyma Doğan",
        "Ferhat Kılıç",
        "Aslı Erdem",
        "Gizem Aktaş",
        "Serhat Yıldırım",
        "Nazan Çetin",
        "Uğur Korkmaz",
        "İrem Aydın",
        "Levent Öz",
        "Sibel Karaman",
        "Hakan Demirci",
        "Pelin Usta",
        "Ramazan Şen",
        "Dilek Yurt",
        "Emrah Çakır",
        "Funda Akgün",
        "Salih Ersoy",
        "Müge Tan",
    ];

    /// <summary>
    /// Realistic Turkish order-level instructions. Independent of item notes.
    /// </summary>
    internal static readonly string[] OrderNotes =
    [
        "Zili çalmayın, arayın.",
        "Kapıya bırakabilirsiniz.",
        "Güvenliğe teslim edebilirsiniz.",
        "Bebek uyuyor, zile basmayın.",
        "Sosları ayrı gönderin.",
        "Siparişi mümkünse sıcak gönderin.",
        "2. kata bırakabilirsiniz.",
        "Geldiğinizde telefonla arayın.",
        "Apartman girişinde bekleyin.",
        "Lütfen hızlı hazırlansın.",
        "Temassız teslimat rica ediyorum.",
        "Kapının önüne bırakabilirsiniz.",
        "Kapıyı çalmadan arayın, bebek uyuyor.",
        "Siparişi kapıya bırakın ve zili çalmayın. Apartman girişindeki güvenliğe de teslim edebilirsiniz.",
        "Lütfen sosları ayrı gönderin ve siparişi mümkün olduğunca sıcak tutun. Geldiğinizde telefonla arayın, zile basmayın.",
        "Zile basmayın, telefonla arayın.",
        "Bebek uyuyor, lütfen kapıyı çalmayın.",
        "Güvenliğe bırakmayın, 3. kata çıkarabilir misiniz?",
        "Çatal kaşık göndermeyin.",
        "5 kişiyiz, ekmeği biraz fazla gönderebilir misiniz?",
        "Tatlıları ayrı poşete koyun.",
        "Soğuk içecekleri sıcak yemeklerden ayrı koyabilir misiniz?",
        "Mümkünse sıcak teslim edin.",
        "Ofis girişinde resepsiyona ismimi söyleyin.",
        "Sipariş iki ayrı poşet olabilir mi?",
        "İçecekleri unutmayın lütfen.",
        "Kalabalık sipariş, ekmeği fazla gönderebilir misiniz?",
        "Asansör bozuk, merdivenle çıkmanızı rica ederim.",
        "Öğle arasında ofisteyiz, geldiğinizde arayın.",
    ];

    public static IReadOnlyCollection<ExternalOrderDto> CreateOrders(
        FoodPlatform platform,
        PlatformConnection connection,
        int count,
        Random? random = null,
        IReadOnlyList<string>? subtypeCodes = null,
        string? culture = null)
    {
        if (count <= 0) return Array.Empty<ExternalOrderDto>();

        var rng = random ?? Random.Shared;
        var contextActive = MockOrderGenerationContext.IsActive;
        var useSubtypeScenarios = subtypeCodes is not null || contextActive;
        var codes = subtypeCodes ?? (contextActive ? MockOrderGenerationContext.Codes : null);
        var resolvedCulture = culture
            ?? (contextActive ? MockOrderGenerationContext.Culture : "tr");
        var list = new List<ExternalOrderDto>(count);
        for (var i = 0; i < count; i++)
        {
            // Mock behavior for MVP:
            // - New orders use provider-like initial status strings (see GetInitialExternalStatus) that map to OrderStatus.New.
            // - Reused order ids (upsert) may only jump to final outcomes (Delivered/Cancelled), not random intermediates
            //   (intermediate echo strings come from IFoodPlatformClient + MockProviderOrderStatusStore after manual actions).
            var (externalOrderId, isExisting) = GetExternalOrderId(platform, rng);
            IReadOnlyCollection<ExternalOrderItemDto> items;
            string? customerNote;
            string rawPayload;
            if (useSubtypeScenarios)
            {
                var built = MockSubtypeOrders.Build(codes, resolvedCulture, externalOrderId, rng);
                items = built.Items;
                customerNote = built.CustomerNote;
                var subtotal = items.Sum(x => x.TotalPrice);
                var deliveryFee = rng.Next(0, 2) == 0 ? 0m : 24.90m;
                var serviceFee = 5.00m;
                var total = subtotal + deliveryFee + serviceFee;
                var externalStatus = isExisting
                    ? GetMockUpdateExternalStatus(platform, rng)
                    : GetInitialExternalStatus(platform);
                rawPayload = CreateSubtypeRawPayload(
                    platform, connection, externalOrderId, externalStatus, items,
                    subtotal, deliveryFee, serviceFee, total, customerNote, built.ScenarioName);
                list.Add(CreateDto(
                    platform, rng, externalOrderId, externalStatus,
                    items, subtotal, deliveryFee, serviceFee, total, customerNote, rawPayload));
                continue;
            }

            var basket = MockLokantaBasket.Build(externalOrderId, rng);
            items = basket.Items;

            var lokantaSubtotal = items.Sum(x => x.TotalPrice);
            var lokantaDeliveryFee = rng.Next(0, 2) == 0 ? 0m : 24.90m;
            var lokantaServiceFee = 5.00m;
            var lokantaTotal = lokantaSubtotal + lokantaDeliveryFee + lokantaServiceFee;
            var lokantaStatus = isExisting
                ? GetMockUpdateExternalStatus(platform, rng)
                : GetInitialExternalStatus(platform);
            customerNote = CreateScenarioOrderNote(basket, rng);
            rawPayload = CreateRawPayload(
                platform, connection, externalOrderId, lokantaStatus, items,
                lokantaSubtotal, lokantaDeliveryFee, lokantaServiceFee, lokantaTotal, customerNote, basket.Scenario);
            list.Add(CreateDto(
                platform, rng, externalOrderId, lokantaStatus,
                items, lokantaSubtotal, lokantaDeliveryFee, lokantaServiceFee, lokantaTotal, customerNote, rawPayload));
        }

        return list;
    }

    private static ExternalOrderDto CreateDto(
        FoodPlatform platform,
        Random rng,
        string externalOrderId,
        string externalStatus,
        IReadOnlyCollection<ExternalOrderItemDto> items,
        decimal subtotal,
        decimal deliveryFee,
        decimal serviceFee,
        decimal total,
        string? customerNote,
        string rawPayload) =>
        new(
            Platform: platform,
            ExternalOrderId: externalOrderId,
            ExternalOrderCode: CreateOrderCode(platform, rng),
            OrderedAtUtc: DateTime.UtcNow.AddMinutes(-rng.Next(1, 90)),
            CustomerName: CreateCustomerName(rng),
            CustomerPhone: CreatePhone(rng),
            CustomerAddress: CreateAddress(rng),
            Subtotal: subtotal,
            DeliveryFee: deliveryFee,
            ServiceFee: serviceFee,
            Total: total,
            PaymentMethod: GetPaymentMethod(rng),
            PaymentStatus: GetPaymentStatus(rng),
            ExternalStatus: externalStatus,
            RawPayloadJson: rawPayload,
            Items: items,
            CustomerNote: customerNote);

    internal static string CreateCustomerName(Random rng) =>
        CustomerNames[rng.Next(CustomerNames.Length)];

    /// <summary>
    /// About 65% of generated orders get an order-level note. The rest stay null.
    /// </summary>
    internal static string? CreateOrderNote(Random rng)
    {
        if (rng.Next(100) >= 65)
            return null;

        return OrderNotes[rng.Next(OrderNotes.Length)];
    }

    /// <summary>
    /// Scenario-aware order note. Many orders stay without one.
    /// Item instructions are chosen separately and never copied from this list.
    /// </summary>
    private static string? CreateScenarioOrderNote(MockBuiltOrder basket, Random rng)
    {
        var scenario = basket.Scenario;
        if (rng.NextDouble() >= scenario.OrderNoteChance)
            return null;

        var hasDessert = false;
        var hasDrink = false;
        foreach (var item in basket.Items)
        {
            var product = MockLokantaMenu.FindByName(item.ProductName);
            if (product is null)
                continue;
            if (product.Course == MockCourse.Dessert)
                hasDessert = true;
            if (product.Course == MockCourse.Drink)
                hasDrink = true;
        }

        var matches = new List<string>();
        foreach (var note in OrderNotes)
        {
            if (OrderNoteFits(note, scenario, hasDessert, hasDrink))
                matches.Add(note);
        }

        if (matches.Count == 0)
            return OrderNotes[rng.Next(OrderNotes.Length)];

        return matches[rng.Next(matches.Count)];
    }

    private static bool OrderNoteFits(string note, MockScenario scenario, bool hasDessert, bool hasDrink)
    {
        if (note.Contains("5 kişiyiz", StringComparison.Ordinal)
            && (scenario.PartyMin < 5 || scenario.PartyMax > 6))
            return false;

        if (note.Contains("Kalabalık sipariş", StringComparison.Ordinal) && scenario.PartyMin < 6)
            return false;

        var office = scenario.Style.HasFlag(MockScenarioStyle.Office);
        if (!office && note.Contains("resepsiyon", StringComparison.OrdinalIgnoreCase))
            return false;

        if (!office && note.Contains("ofisteyiz", StringComparison.OrdinalIgnoreCase))
            return false;

        if (office && note.Contains("Bebek", StringComparison.Ordinal))
            return false;

        if (!hasDessert && note.Contains("Tatlı", StringComparison.Ordinal))
            return false;

        if (!hasDrink
            && (note.Contains("İçecek", StringComparison.Ordinal) || note.Contains("içecek", StringComparison.Ordinal)))
            return false;

        return true;
    }

    private static (string ExternalOrderId, bool IsExisting) GetExternalOrderId(FoodPlatform platform, Random rng)
    {
        var q = _knownExternalOrderIds.GetOrAdd(platform, _ => new ConcurrentQueue<string>());

        // ~30% reuse an existing ExternalOrderId to exercise idempotent upsert + updates
        if (rng.NextDouble() < 0.30 && q.TryPeek(out var existing))
        {
            return (existing, true);
        }

        var fresh = $"{platform}-{Guid.NewGuid():N}";
        q.Enqueue(fresh);

        // keep memory bounded
        while (q.Count > 200 && q.TryDequeue(out _)) { }

        return (fresh, false);
    }

    private static string GetInitialExternalStatus(FoodPlatform platform) =>
        platform switch
        {
            // Partner / provider-shaped strings mapped to New (see DefaultOrderStatusMapper).
            FoodPlatform.Yemeksepeti => "RECEIVED",
            FoodPlatform.GetirYemek => "CREATED",
            FoodPlatform.TrendyolYemek => "Created",
            _ => "unknown"
        };

    private static string GetMockUpdateExternalStatus(FoodPlatform platform, Random rng)
    {
        // Simple, predictable demo behavior:
        // 70% stays New, 20% becomes Delivered, 10% becomes Cancelled.
        var roll = rng.NextDouble();
        if (roll < 0.70) return GetInitialExternalStatus(platform);

        var toDelivered = roll < 0.90;

        return platform switch
        {
            FoodPlatform.Yemeksepeti => toDelivered ? "DELIVERED" : "CANCELLED",
            FoodPlatform.GetirYemek => toDelivered ? "DELIVERED" : "CANCELLED",
            FoodPlatform.TrendyolYemek => toDelivered ? "Delivered" : "Cancelled",
            _ => GetInitialExternalStatus(platform)
        };
    }

    private static PaymentMethod GetPaymentMethod(Random rng) =>
        Pick(rng, PaymentMethod.Cash, PaymentMethod.CreditCard, PaymentMethod.OnlinePayment);

    private static PaymentStatus GetPaymentStatus(Random rng) =>
        Pick(rng, PaymentStatus.Pending, PaymentStatus.Paid, PaymentStatus.Failed);

    private static string CreateRawPayload(
        FoodPlatform platform,
        PlatformConnection connection,
        string externalOrderId,
        string externalStatus,
        IReadOnlyCollection<ExternalOrderItemDto> items,
        decimal subtotal,
        decimal deliveryFee,
        decimal serviceFee,
        decimal total,
        string? customerNote,
        MockScenario scenario)
    {
        var payload = new
        {
            platform = platform.ToString(),
            restaurant = "Mengen Lokantası",
            scenario = scenario.Name,
            partyMin = scenario.PartyMin,
            partyMax = scenario.PartyMax,
            focus = scenario.Focus.ToString(),
            storeId = connection.StoreId,
            externalOrderId,
            externalStatus,
            customerNote,
            orderedAtUtc = DateTime.UtcNow,
            totals = new { subtotal, deliveryFee, serviceFee, total },
            items = items.Select(i => new
            {
                i.ExternalItemId,
                i.ProductName,
                i.Quantity,
                i.UnitPrice,
                i.TotalPrice,
                i.Notes,
                options = i.Options.Select(o => new { o.Name, o.Price })
            })
        };

        return JsonSerializer.Serialize(payload);
    }

    private static string CreateSubtypeRawPayload(
        FoodPlatform platform,
        PlatformConnection connection,
        string externalOrderId,
        string externalStatus,
        IReadOnlyCollection<ExternalOrderItemDto> items,
        decimal subtotal,
        decimal deliveryFee,
        decimal serviceFee,
        decimal total,
        string? customerNote,
        string scenarioName)
    {
        var payload = new
        {
            platform = platform.ToString(),
            scenario = scenarioName,
            storeId = connection.StoreId,
            externalOrderId,
            externalStatus,
            customerNote,
            orderedAtUtc = DateTime.UtcNow,
            totals = new { subtotal, deliveryFee, serviceFee, total },
            items = items.Select(i => new
            {
                i.ExternalItemId,
                i.ProductName,
                i.Quantity,
                i.UnitPrice,
                i.TotalPrice,
                i.Notes,
                options = i.Options.Select(o => new { o.Name, o.Price })
            })
        };

        return JsonSerializer.Serialize(payload);
    }

    private static string CreateOrderCode(FoodPlatform platform, Random rng)
    {
        var prefix = platform switch
        {
            FoodPlatform.Yemeksepeti => "YS",
            FoodPlatform.GetirYemek => "GY",
            FoodPlatform.TrendyolYemek => "TY",
            _ => "OH"
        };
        return $"{prefix}-ORDER-{rng.Next(10000, 99999)}";
    }

    private static string CreatePhone(Random rng) => $"+9055{rng.Next(10000000, 99999999)}";

    private static string CreateAddress(Random rng) =>
        Pick(rng,
            "Kadıköy, İstanbul",
            "Beşiktaş, İstanbul",
            "Üsküdar, İstanbul",
            "Şişli, İstanbul",
            "Ataşehir, İstanbul",
            "Mengen, Bolu",
            "Tabaklar, Bolu",
            "İzzet Baysal, Bolu",
            "Akpınar, Mengen",
            "Karacaağaç, Bolu");

    private static T Pick<T>(Random rng, params T[] values) => values[rng.Next(values.Length)];
}
