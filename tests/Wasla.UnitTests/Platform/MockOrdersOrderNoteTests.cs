using System.Text.Json;
using Wasla.Domain.Entities.Customer;
using Wasla.Domain.Enums;
using Wasla.Infrastructure.Platform.Mock;

namespace Wasla.UnitTests.Platform;

public sealed class MockOrdersOrderNoteTests
{
    [Fact]
    public void CreateOrderNote_IncludesNotedAndUnnotedResults_FromTheOrderNoteCatalog()
    {
        var rng = new Random(99);
        var noted = 0;
        var absent = 0;

        for (var i = 0; i < 200; i++)
        {
            var note = MockOrders.CreateOrderNote(rng);
            if (note is null)
            {
                absent++;
                continue;
            }

            noted++;
            Assert.False(string.IsNullOrWhiteSpace(note));
            Assert.Contains(note, MockOrders.OrderNotes);
        }

        Assert.True(noted > 0);
        Assert.True(absent > 0);
        Assert.Contains(MockOrders.OrderNotes, note => note.Length > 40);
    }

    [Fact]
    public void CreateOrderNote_IsDeterministicForTheSameSeed()
    {
        Assert.Equal(SampleNotes(new Random(2026)), SampleNotes(new Random(2026)));
    }

    [Fact]
    public void CreateOrders_KeepsOrderNotesIndependentFromItemNotes_WithScenarioVariety()
    {
        var orders = MockOrders.CreateOrders(
            FoodPlatform.TrendyolYemek,
            new PlatformConnection
            {
                Platform = FoodPlatform.TrendyolYemek,
                StoreId = "mock-store",
            },
            count: 80,
            random: new Random(2026));

        var withOrderNote = 0;
        var withoutOrderNote = 0;
        var withItemNote = 0;
        var withoutItemNote = 0;
        var scenarios = new HashSet<string>(StringComparer.Ordinal);

        foreach (var order in orders)
        {
            Assert.False(string.IsNullOrWhiteSpace(order.ExternalOrderId));
            Assert.False(string.IsNullOrWhiteSpace(order.CustomerName));
            Assert.NotEmpty(order.Items);
            Assert.True(order.Total > 0);

            using var document = JsonDocument.Parse(order.RawPayloadJson);
            Assert.Equal("Mengen Lokantası", document.RootElement.GetProperty("restaurant").GetString());
            var scenario = document.RootElement.GetProperty("scenario").GetString();
            Assert.False(string.IsNullOrWhiteSpace(scenario));
            scenarios.Add(scenario!);

            var hasOrderNote = !string.IsNullOrWhiteSpace(order.CustomerNote);
            var hasItemNote = order.Items.Any(item => !string.IsNullOrWhiteSpace(item.Notes));
            if (hasOrderNote)
            {
                withOrderNote++;
                Assert.Contains(order.CustomerNote, MockOrders.OrderNotes);
                Assert.DoesNotContain(order.Items, item => item.Notes == order.CustomerNote);
            }
            else
            {
                withoutOrderNote++;
            }

            if (hasItemNote)
                withItemNote++;
            else
                withoutItemNote++;
        }

        Assert.True(withOrderNote > 0);
        Assert.True(withoutOrderNote > 0);
        Assert.True(withItemNote > 0);
        Assert.True(withoutItemNote > 0);
        Assert.True(scenarios.Count > 1);
    }

    private static string?[] SampleNotes(Random rng)
    {
        var notes = new string?[40];
        for (var i = 0; i < notes.Length; i++)
            notes[i] = MockOrders.CreateOrderNote(rng);
        return notes;
    }
}
