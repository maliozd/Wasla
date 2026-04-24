using OrderHub.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using OrderHub.Api.Middleware;
using OrderHub.Api.Tenant;
using OrderHub.Application.Abstractions.Security;
using OrderHub.Application.Abstractions.Tenant;
using OrderHub.Infrastructure.Persistence.Central;

AesSecretManager.ValidateMasterKeyOrThrow();

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpContextAccessor();
builder.Services.AddMemoryCache();

builder.Services.AddDbContext<CentralDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("CentralDb")));

builder.Services.AddSingleton<ISecretManager, AesSecretManager>();
builder.Services.AddScoped<ICurrentCustomerService, CurrentCustomerService>();

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

app.MapControllers();
app.MapGet("/", () => Results.Ok("OrderHub API"));

app.Run();

