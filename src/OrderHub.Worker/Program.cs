using Microsoft.Extensions.Hosting;
using OrderHub.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using OrderHub.Application.Abstractions.Security;
using OrderHub.Infrastructure.Persistence.Central;

AesSecretManager.ValidateMasterKeyOrThrow();

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddDbContext<CentralDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("CentralDb")));

builder.Services.AddSingleton<ISecretManager, AesSecretManager>();

var host = builder.Build();

await host.RunAsync();

