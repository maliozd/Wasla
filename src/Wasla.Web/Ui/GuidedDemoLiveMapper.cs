using Microsoft.Extensions.Localization;
using Wasla.Application.Abstractions.Orders;
using Wasla.Application.Demos;
using Wasla.Domain.Enums;

namespace Wasla.Web.Ui;

public static class GuidedDemoLiveMapper
{
    public static LiveScreenOrderDto ToLiveOrder(IStringLocalizer localizer, GuidedDemoSessionState session)
    {
        var items = session.Lines
            .Select(line => new LiveScreenLineItemDto(localizer[line.NameKey].Value, line.Quantity, null))
            .ToArray();
        var total = session.Lines.Sum(line => line.UnitPrice * line.Quantity);
        return new LiveScreenOrderDto(
            session.Id,
            "DEMO",
            FoodPlatform.Yemeksepeti,
            session.Status,
            session.ReceivedAtUtc,
            session.DeliveredAtUtc,
            localizer[session.CustomerNameKey].Value,
            string.Empty,
            session.NoteKey is null ? null : localizer[session.NoteKey].Value,
            total,
            items,
            IsDemo: true)
        {
            DemoAutomation = session.Automatic is { } step
                ? new LiveScreenDemoAutomation(step.Action, step.DueAtUtc, (int)GuidedDemoTiming.StageDuration.TotalSeconds)
                : null
        };
    }
}
