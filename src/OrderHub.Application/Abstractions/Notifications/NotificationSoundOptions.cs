namespace OrderHub.Application.Abstractions.Notifications;

public static class NotificationSoundOptions
{
    public static readonly IReadOnlyList<string> AllowedNames =
        ["bell1", "bell2", "bell3", "bell4", "bell5", "bell6"];

    public static string GetLabel(string name) => name switch
    {
        "bell1" => "Bell 1",
        "bell2" => "Bell 2",
        "bell3" => "Bell 3",
        "bell4" => "Bell 4",
        "bell5" => "Bell 5",
        "bell6" => "Bell 6",
        _ => "Bell"
    };
}

