namespace Wasla.Domain.Enums;

/// <summary>
/// What a stored message is about. The contact form lets the sender choose one of the first five; a pilot restaurant
/// application (WAS-217) comes only from the pilot form at <c>/pilot</c>, never from the contact form's topic list.
/// </summary>
public enum ContactTopic
{
    WaslaOrders = 0,
    WaslaPos = 1,
    PrintBridgeSetup = 2,
    Pricing = 3,
    Other = 4,

    /// <summary>A restaurant applying to the Wasla pilot through <c>/pilot</c>; it carries a city and a required phone.</summary>
    PilotApplication = 5
}
