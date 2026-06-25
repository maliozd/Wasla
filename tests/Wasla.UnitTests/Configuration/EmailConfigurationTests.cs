using System.Xml.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wasla.Application.Abstractions.Email;
using Wasla.Infrastructure.DependencyInjection;
using Wasla.Infrastructure.Email;
using Wasla.Infrastructure.Options;

namespace Wasla.UnitTests.Configuration;

public sealed class EmailConfigurationTests
{
    private const string SharedUserSecretsId = "c8cc92c8-4c99-488d-848c-b14914a9c722";

    [Fact]
    public void WebAndCliProjects_UseSameUserSecretsId()
    {
        var root = FindSolutionRoot();
        var web = ReadUserSecretsId(Path.Combine(root, "src", "Wasla.Web", "Wasla.Web.csproj"));
        var cli = ReadUserSecretsId(Path.Combine(root, "src", "Wasla.Cli", "Wasla.Cli.csproj"));

        Assert.Equal(SharedUserSecretsId, web);
        Assert.Equal(web, cli);
    }

    [Fact]
    public void EmailOptions_BindsExpectedKeys()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Email:Provider"] = "Smtp",
            ["Email:FromEmail"] = "sender@example.test",
            ["Email:FromName"] = "Wasla Test",
            ["Email:SmtpHost"] = "smtp.example.test",
            ["Email:SmtpPort"] = "2525",
            ["Email:SmtpUsername"] = "smtp-user",
            ["Email:SmtpPassword"] = "smtp-password",
            ["Email:EnableSsl"] = "false"
        });

        var options = new EmailOptions();
        configuration.GetSection(EmailOptions.SectionName).Bind(options);

        Assert.Equal("Smtp", options.Provider);
        Assert.Equal("sender@example.test", options.FromEmail);
        Assert.Equal("Wasla Test", options.FromName);
        Assert.Equal("smtp.example.test", options.SmtpHost);
        Assert.Equal(2525, options.SmtpPort);
        Assert.Equal("smtp-user", options.SmtpUsername);
        Assert.Equal("smtp-password", options.SmtpPassword);
        Assert.False(options.EnableSsl);
    }

    [Fact]
    public void AddWaslaEmail_SmtpProvider_ResolvesSmtpEmailSender()
    {
        using var provider = BuildEmailProvider(new Dictionary<string, string?>
        {
            ["Email:Provider"] = "Smtp",
            ["Email:FromEmail"] = "sender@example.test",
            ["Email:FromName"] = "Wasla Test",
            ["Email:SmtpHost"] = "smtp.example.test",
            ["Email:SmtpPort"] = "587",
            ["Email:SmtpUsername"] = "smtp-user",
            ["Email:SmtpPassword"] = "smtp-password",
            ["Email:EnableSsl"] = "true"
        });

        Assert.IsType<SmtpEmailSender>(provider.GetRequiredService<IEmailSender>());
    }

    [Fact]
    public void AddWaslaEmail_LogProvider_ResolvesLogEmailSender_WithBlankSmtpValues()
    {
        using var provider = BuildEmailProvider(new Dictionary<string, string?>
        {
            ["Email:Provider"] = "Log",
            ["Email:FromEmail"] = "noreply@wasla.local",
            ["Email:FromName"] = "Wasla",
            ["Email:SmtpHost"] = "",
            ["Email:SmtpPort"] = "587",
            ["Email:SmtpUsername"] = "",
            ["Email:SmtpPassword"] = "",
            ["Email:EnableSsl"] = "true"
        });

        Assert.IsType<LogEmailSender>(provider.GetRequiredService<IEmailSender>());
        _ = provider.GetRequiredService<IOptions<EmailOptions>>().Value;
    }

    [Fact]
    public void SmtpValidation_AcceptsCompleteConfiguration()
    {
        using var provider = BuildEmailProvider(new Dictionary<string, string?>
        {
            ["Email:Provider"] = "Smtp",
            ["Email:FromEmail"] = "sender@example.test",
            ["Email:FromName"] = "Wasla Test",
            ["Email:SmtpHost"] = "smtp.example.test",
            ["Email:SmtpPort"] = "587",
            ["Email:SmtpUsername"] = "smtp-user",
            ["Email:SmtpPassword"] = "smtp-password",
            ["Email:EnableSsl"] = "true"
        });

        var options = provider.GetRequiredService<IOptions<EmailOptions>>().Value;

        Assert.Equal("Smtp", options.Provider);
    }

    [Fact]
    public void SmtpValidation_RejectsMissingRequiredConfiguration_WithoutPasswordValue()
    {
        const string password = "smtp-password-that-must-not-appear";
        using var provider = BuildEmailProvider(new Dictionary<string, string?>
        {
            ["Email:Provider"] = "Smtp",
            ["Email:FromEmail"] = "sender@example.test",
            ["Email:FromName"] = "Wasla Test",
            ["Email:SmtpHost"] = "",
            ["Email:SmtpPort"] = "587",
            ["Email:SmtpUsername"] = "smtp-user",
            ["Email:SmtpPassword"] = password,
            ["Email:EnableSsl"] = "true"
        });

        var ex = Assert.Throws<OptionsValidationException>(() =>
            _ = provider.GetRequiredService<IOptions<EmailOptions>>().Value);

        Assert.Contains("Email:SmtpHost", ex.Message);
        Assert.DoesNotContain(password, ex.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void SmtpValidation_RequiresPasswordForSmtpProvider()
    {
        using var provider = BuildEmailProvider(new Dictionary<string, string?>
        {
            ["Email:Provider"] = "Smtp",
            ["Email:FromEmail"] = "sender@example.test",
            ["Email:FromName"] = "Wasla Test",
            ["Email:SmtpHost"] = "smtp.example.test",
            ["Email:SmtpPort"] = "587",
            ["Email:SmtpUsername"] = "smtp-user",
            ["Email:SmtpPassword"] = "",
            ["Email:EnableSsl"] = "true"
        });

        var ex = Assert.Throws<OptionsValidationException>(() =>
            _ = provider.GetRequiredService<IOptions<EmailOptions>>().Value);

        Assert.Contains("Email:SmtpPassword", ex.Message);
    }

    private static ServiceProvider BuildEmailProvider(Dictionary<string, string?> values)
    {
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddProvider(NullLoggerProvider.Instance));
        services.AddWaslaEmail(BuildConfiguration(values));
        return services.BuildServiceProvider();
    }

    private static IConfiguration BuildConfiguration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

    private static string? ReadUserSecretsId(string projectPath)
    {
        var document = XDocument.Load(projectPath);
        return document
            .Descendants("UserSecretsId")
            .SingleOrDefault()
            ?.Value;
    }

    private static string FindSolutionRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !directory.GetFiles("Wasla.sln").Any())
            directory = directory.Parent;

        return directory?.FullName
            ?? throw new InvalidOperationException("Could not locate Wasla.sln.");
    }

    private sealed class NullLoggerProvider : ILoggerProvider
    {
        public static readonly NullLoggerProvider Instance = new();

        public ILogger CreateLogger(string categoryName) => Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

        public void Dispose()
        {
        }
    }
}
