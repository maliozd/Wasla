using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;

namespace Wasla.Web.Middleware;

public static class WaslaExceptionHandlingExtensions
{
    public const string UnexpectedErrorPath = "/error";

    public static void UseWaslaExceptionHandling(this IApplicationBuilder app, IHostEnvironment environment)
    {
        if (environment.IsDevelopment())
            app.UseDeveloperExceptionPage();
        else
            app.UseExceptionHandler(UnexpectedErrorPath);
    }
}
