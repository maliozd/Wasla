using System.Globalization;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.Extensions.Localization;
using Wasla.Application.Abstractions.Admin;
using Wasla.Application.Abstractions.Printing;
using Wasla.Application.Abstractions.Setup;
using Wasla.Application.Admin;
using Wasla.Domain.Enums;

namespace Wasla.Web.Areas.Admin.Models.TenantOperations;

public enum AdminStatusTone
{
    Success,
    Warning,
    Danger,
    Info,
    Neutral
}

/// <summary>A status shown as an icon plus localized text; the tone only adds color.</summary>
public sealed record AdminStatus(AdminStatusTone Tone, string LabelKey, string Icon)
{
    public string CssModifier => Tone.ToString().ToLowerInvariant();
}

/// <summary>Maps operational states to resource keys and badges. Presentation only; no rule lives here.</summary>
public static class AdminOperationsPresenter
{
    private static AdminStatus Ok(string key) => new(AdminStatusTone.Success, key, "bi-check-circle");
    private static AdminStatus Warn(string key) => new(AdminStatusTone.Warning, key, "bi-exclamation-triangle");
    private static AdminStatus Bad(string key) => new(AdminStatusTone.Danger, key, "bi-x-octagon");
    private static AdminStatus Note(string key) => new(AdminStatusTone.Info, key, "bi-info-circle");
    private static AdminStatus Muted(string key) => new(AdminStatusTone.Neutral, key, "bi-dash-circle");
    private static AdminStatus Unknown(string key) => new(AdminStatusTone.Neutral, key, "bi-question-circle");

    public static AdminStatus TenantActive(bool isActive) =>
        isActive ? Ok("Admin.StatusActive") : Muted("Admin.StatusInactive");

    public static AdminStatus MigrationRecord(TenantMigrationRecord record) => record switch
    {
        TenantMigrationRecord.Succeeded => Ok("Admin.Ops.MigrationRecord.Succeeded"),
        TenantMigrationRecord.Failed => Bad("Admin.Ops.MigrationRecord.Failed"),
        TenantMigrationRecord.NotRecorded => Muted("Admin.Ops.MigrationRecord.NotRecorded"),
        _ => Unknown("Admin.Ops.State.Unknown")
    };

    public static AdminStatus Database(TenantDatabaseState state) => state switch
    {
        TenantDatabaseState.Reachable => Ok("Admin.Ops.Database.Reachable"),
        TenantDatabaseState.NotConfigured => Muted("Admin.Ops.Database.NotConfigured"),
        TenantDatabaseState.Unreachable => Bad("Admin.Ops.Database.Unreachable"),
        TenantDatabaseState.TimedOut => Bad("Admin.Ops.Database.TimedOut"),
        TenantDatabaseState.ConfigurationUnreadable => Bad("Admin.Ops.Database.ConfigurationUnreadable"),
        _ => Unknown("Admin.Ops.State.Unknown")
    };

    public static AdminStatus Migrations(TenantMigrationState? state) => state switch
    {
        TenantMigrationState.Current => Ok("Admin.Ops.Migrations.Current"),
        TenantMigrationState.Pending => Bad("Admin.Ops.Migrations.Pending"),
        TenantMigrationState.DatabaseAhead => Warn("Admin.Ops.Migrations.DatabaseAhead"),
        _ => Unknown("Admin.Ops.State.Unknown")
    };

    public static AdminStatus OperationalMode(TenantOperationalMode mode) =>
        mode == TenantOperationalMode.Live
            ? Ok("Admin.Ops.Mode.Live")
            : Note("Admin.Ops.Mode.Setup");

    public static AdminStatus Automation(AutomationState state) => state switch
    {
        AutomationState.Active => Ok("Admin.Ops.Automation.Active"),
        AutomationState.PendingSetup => Note("Admin.Ops.Automation.PendingSetup"),
        _ => Muted("Admin.Ops.Automation.Off")
    };

    public static AdminStatus Connection(ProviderConnectionHealthState state) => state switch
    {
        ProviderConnectionHealthState.Healthy => Ok("Admin.Ops.Connection.Healthy"),
        ProviderConnectionHealthState.Disabled => Muted("Admin.Ops.Connection.Disabled"),
        ProviderConnectionHealthState.SyncOff => Muted("Admin.Ops.Connection.SyncOff"),
        ProviderConnectionHealthState.CircuitOpen => Bad("Admin.Ops.Connection.CircuitOpen"),
        ProviderConnectionHealthState.Failing => Warn("Admin.Ops.Connection.Failing"),
        ProviderConnectionHealthState.NeverSynced => Note("Admin.Ops.Connection.NeverSynced"),
        ProviderConnectionHealthState.Stale => Warn("Admin.Ops.Connection.Stale"),
        _ => Unknown("Admin.Ops.State.Unknown")
    };

    /// <summary>Online / stale / offline from the canonical Print Bridge thresholds (60 seconds, 5 minutes).</summary>
    public static AdminStatus PrintBridge(PrintBridgeConnectionStatus status) => status switch
    {
        PrintBridgeConnectionStatus.Connected => new(AdminStatusTone.Success, "Admin.Ops.PrintBridge.Online", "bi-wifi"),
        PrintBridgeConnectionStatus.RecentlySeen => new(AdminStatusTone.Warning, "Admin.Ops.PrintBridge.Stale", "bi-hourglass-split"),
        PrintBridgeConnectionStatus.Disconnected => new(AdminStatusTone.Danger, "Admin.Ops.PrintBridge.Offline", "bi-wifi-off"),
        PrintBridgeConnectionStatus.NeverConnected => Muted("Admin.Ops.PrintBridge.NeverConnected"),
        _ => Muted("Admin.Ops.PrintBridge.Disabled")
    };

    public static AdminStatus Registration(PendingRegistrationStatus status) => status switch
    {
        PendingRegistrationStatus.Provisioned => Ok("Admin.PendingReg.Status.Provisioned"),
        PendingRegistrationStatus.PaymentSucceeded => Warn("Admin.PendingReg.Status.PaymentSucceeded"),
        PendingRegistrationStatus.AwaitingPayment => Note("Admin.PendingReg.Status.AwaitingPayment"),
        PendingRegistrationStatus.PaymentFailed => Bad("Admin.PendingReg.Status.PaymentFailed"),
        PendingRegistrationStatus.Cancelled => Muted("Admin.PendingReg.Status.Cancelled"),
        PendingRegistrationStatus.Expired => Muted("Admin.PendingReg.Status.Expired"),
        _ => Muted("Admin.PendingReg.Status.Draft")
    };

    public static string MembershipStatusKey(MembershipStatus status) => status switch
    {
        MembershipStatus.Trial => "Admin.Ops.Membership.Trial",
        MembershipStatus.Active => "Admin.Ops.Membership.Active",
        MembershipStatus.PendingPayment => "Admin.Ops.Membership.PendingPayment",
        _ => "Admin.Ops.Membership.Cancelled"
    };

    public static string PlatformKey(FoodPlatform platform) => platform switch
    {
        FoodPlatform.Yemeksepeti => "Orders.PlatformYemeksepeti",
        FoodPlatform.GetirYemek => "Orders.PlatformGetirYemek",
        _ => "Orders.PlatformTrendyolYemek"
    };

    public static string ProviderModeKey(TenantProviderMode mode) => mode switch
    {
        TenantProviderMode.Mock => "Admin.Ops.ProviderMode.Mock",
        TenantProviderMode.Real => "Admin.Ops.ProviderMode.Real",
        _ => "Admin.Ops.State.Unknown"
    };

    public static string ClientKindKey(ProviderClientKind kind) => kind switch
    {
        ProviderClientKind.Mock => "Admin.Ops.ProviderMode.Mock",
        ProviderClientKind.Real => "Admin.Ops.ProviderMode.Real",
        _ => "Admin.Ops.ProviderMode.NotRegistered"
    };

    public static AdminStatus Guidance(TenantOperationsGuidanceSeverity severity, TenantOperationsGuidanceCode code) =>
        code == TenantOperationsGuidanceCode.AllClear
            ? Ok("Admin.Ops.Guidance.Severity.AllClear")
            : severity switch
            {
                TenantOperationsGuidanceSeverity.Critical => Bad("Admin.Ops.Guidance.Severity.Critical"),
                TenantOperationsGuidanceSeverity.Warning => Warn("Admin.Ops.Guidance.Severity.Warning"),
                _ => Note("Admin.Ops.Guidance.Severity.Info")
            };

    public static string GuidanceKey(TenantOperationsGuidanceCode code) => $"Admin.Ops.Guidance.{code}";

    /// <summary>A UTC time as text with an explicit zone, or an em dash when absent.</summary>
    public static IHtmlContent Time(DateTime? utc)
    {
        if (utc is null)
            return new HtmlString("<span class=\"text-body-secondary\">—</span>");

        var value = DateTime.SpecifyKind(utc.Value, DateTimeKind.Utc);
        var tag = new TagBuilder("time");
        tag.Attributes["datetime"] = value.ToString("yyyy-MM-ddTHH:mm:ss'Z'", CultureInfo.InvariantCulture);
        tag.InnerHtml.Append(value.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC");
        return tag;
    }

    /// <summary>A short localized age such as "5 min ago", or null when there is no time.</summary>
    public static string? Age(DateTime? utc, DateTime nowUtc, IStringLocalizer localizer)
    {
        if (utc is null)
            return null;

        var age = nowUtc - utc.Value;
        if (age < TimeSpan.FromMinutes(1))
            return localizer["Admin.Ops.Age.JustNow"].Value;
        if (age < TimeSpan.FromHours(1))
            return localizer["Admin.Ops.Age.Minutes", (int)age.TotalMinutes].Value;
        if (age < TimeSpan.FromDays(1))
            return localizer["Admin.Ops.Age.Hours", (int)age.TotalHours].Value;
        return localizer["Admin.Ops.Age.Days", (int)age.TotalDays].Value;
    }
}
