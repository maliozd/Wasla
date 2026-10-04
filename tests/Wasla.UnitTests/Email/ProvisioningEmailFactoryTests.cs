using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using Wasla.Domain.Entities.Central;
using Wasla.Infrastructure.Email;
using Wasla.Infrastructure.Options;
using Wasla.Web.Routing;

namespace Wasla.UnitTests.Email;

public sealed class ProvisioningEmailFactoryTests
{
    [Fact]
    public void BuildPanelReadyEmail_UsesProvidedPanelLoginUrl_WithDevelopmentPort()
    {
        var request = new DefaultHttpContext().Request;
        request.Scheme = "http";
        request.Host = new HostString("admin.wasla.local", 5200);
        var environment = new TestWebHostEnvironment { EnvironmentName = "Development" };
        var loginUrl = TenantWelcomeUrlBuilder.BuildLoginUrl(request, environment, "tenant.wasla.local");

        var factory = new ProvisioningEmailFactory(
            new EmailTemplateRenderer(),
            Options.Create(new EmailOptions { FromName = "Wasla Test" }));

        var message = factory.BuildPanelReadyEmail(new PendingRegistration
        {
            OwnerFullName = "Owner",
            OwnerEmail = "owner@example.test",
            PrimaryDomain = "tenant.wasla.local"
        }, loginUrl);

        Assert.Equal("http://tenant.wasla.local:5200/auth/login", loginUrl);
        Assert.Contains(loginUrl, message.TextBody);
        Assert.Contains("http://tenant.wasla.local:5200/auth/login", message.HtmlBody);
        Assert.DoesNotContain("https://tenant.wasla.local/auth/login", message.TextBody);
    }

    private sealed class TestWebHostEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "Wasla.UnitTests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = string.Empty;
        public string EnvironmentName { get; set; } = "Development";
        public string ContentRootPath { get; set; } = Directory.GetCurrentDirectory();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
