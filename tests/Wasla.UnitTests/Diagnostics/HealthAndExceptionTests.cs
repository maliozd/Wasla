using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wasla.Infrastructure.Diagnostics;
using Wasla.Infrastructure.Persistence.Central;
using Wasla.Web.Controllers;
using Wasla.Web.Middleware;
using Wasla.Web.Models;

namespace Wasla.UnitTests.Diagnostics;

public sealed class HealthAndExceptionTests
{
    [Fact]
    public async Task Live_IsHealthy_AndReady_FailsWhenCentralDatabaseCannotBeOpened()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Production
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddScoped<CentralDbContext>(_ =>
            throw new InvalidOperationException("Server=secret;Password=secret-db"));
        builder.Services.AddWaslaHealthChecks();

        var app = builder.Build();
        app.UseMiddleware<RequestDiagnosticsMiddleware>();
        app.MapWaslaHealthChecks();
        var ct = TestContext.Current.CancellationToken;
        await app.StartAsync(ct);
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        try
        {
            var live = await client.GetAsync("/health/live", ct);
            var liveBody = await live.Content.ReadAsStringAsync(ct);
            Assert.Equal(System.Net.HttpStatusCode.OK, live.StatusCode);
            Assert.Equal("Healthy", liveBody);
            Assert.True(live.Headers.TryGetValues(RequestDiagnosticsMiddleware.TraceHeaderName, out var traceValues));
            Assert.False(string.IsNullOrWhiteSpace(Assert.Single(traceValues)));
            Assert.DoesNotContain("secret", liveBody, StringComparison.OrdinalIgnoreCase);

            var ready = await client.GetAsync("/health/ready", ct);
            var readyBody = await ready.Content.ReadAsStringAsync(ct);
            Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, ready.StatusCode);
            Assert.Equal("Unhealthy", readyBody);
            Assert.DoesNotContain("Password", readyBody, StringComparison.Ordinal);
            Assert.DoesNotContain("secret-db", readyBody, StringComparison.Ordinal);
        }
        finally
        {
            await app.StopAsync(ct);
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task ProductionFailures_DoNotRedirectToLogin_OrReturnExceptionText()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Production
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.UseWaslaExceptionHandling(app.Environment);
        app.MapGet("/boom", (HttpContext _) =>
        {
            throw new InvalidOperationException("SECRET_CUSTOMER_Ali_5551112233");
        });
        app.MapGet("/error", async (HttpContext http) =>
        {
            http.Response.StatusCode = StatusCodes.Status500InternalServerError;
            http.Response.ContentType = "text/plain";
            await http.Response.WriteAsync("safe-error", TestContext.Current.CancellationToken);
        });
        var ct = TestContext.Current.CancellationToken;
        await app.StartAsync(ct);
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            var response = await client.GetAsync("/boom", ct);
            var body = await response.Content.ReadAsStringAsync(ct);

            Assert.Equal(System.Net.HttpStatusCode.InternalServerError, response.StatusCode);
            Assert.Equal("safe-error", body);
            Assert.DoesNotContain("SECRET_CUSTOMER", body, StringComparison.Ordinal);
            Assert.DoesNotContain("InvalidOperationException", body, StringComparison.Ordinal);
            Assert.Null(response.Headers.Location);
        }
        finally
        {
            await app.StopAsync(ct);
            await app.DisposeAsync();
        }
    }

    [Fact]
    public void ErrorPage_Sets500_AndOnlyExposesTraceId()
    {
        var http = new DefaultHttpContext();
        http.TraceIdentifier = "trace-from-request";
        var controller = new ErrorController
        {
            ControllerContext = new ControllerContext { HttpContext = http }
        };

        var result = controller.Index();

        Assert.Equal(StatusCodes.Status500InternalServerError, http.Response.StatusCode);
        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<ErrorPageViewModel>(view.Model);
        Assert.Equal("trace-from-request", model.TraceId);
        Assert.Equal("no-store", http.Response.Headers["Cache-Control"].ToString());
    }
}
