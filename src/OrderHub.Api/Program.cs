using OrderHub.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using OrderHub.Api.Middleware;
using OrderHub.Api.Tenant;
using OrderHub.Application.Abstractions.Security;
using OrderHub.Application.Abstractions.Tenant;
using OrderHub.Application.Auth.Services;
using OrderHub.Infrastructure.Persistence.Central;
using OrderHub.Infrastructure.Services;
using Microsoft.AspNetCore.Authentication.Cookies;

AesSecretManager.ValidateMasterKeyOrThrow();

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpContextAccessor();
builder.Services.AddMemoryCache();

builder.Services.AddDbContext<CentralDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("CentralDb")));

builder.Services.AddSingleton<ISecretManager, AesSecretManager>();
builder.Services.AddScoped<ICurrentCustomerService, CurrentCustomerService>();
builder.Services.AddScoped<IAuthService, AuthService>();

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie();
builder.Services.AddAuthorization();

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseMiddleware<CustomerResolutionMiddleware>();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapGet("/", () => Results.Ok("OrderHub API"));

app.Run();

