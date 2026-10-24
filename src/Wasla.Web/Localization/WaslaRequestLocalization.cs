using Microsoft.AspNetCore.Localization;

namespace Wasla.Web.Localization;

/// <summary>The request localization of the Web application, shared by Program.cs and the in-process test hosts.</summary>
public static class WaslaRequestLocalization
{
    public static void Configure(RequestLocalizationOptions options)
    {
        var supported = SupportedCultures.AllCultureInfos;

        options.DefaultRequestCulture = new RequestCulture(SupportedCultures.Default);
        options.SupportedCultures = supported.ToList();
        options.SupportedUICultures = supported.ToList();

        // Provider priority:
        // 1) Cookie
        // 2) Accept-Language
        // 3) Default fallback

        options.RequestCultureProviders = new List<IRequestCultureProvider>
        {
            new CookieRequestCultureProvider(),
            new AcceptLanguageHeaderRequestCultureProvider()
        };
    }
}
