using GoldSrcOps.AlertReceiver.Configuration;
using GoldSrcOps.AlertReceiver.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace GoldSrcOps.AlertReceiver.Tests.Integration;

internal sealed class AlertReceiverFactory(
    string connectionString,
    ReceiverMode receiverMode)
    : WebApplicationFactory<Program>
{
    public const string Authorization = "Bearer alert-receiver-test";

    public static async Task<AlertReceiverFactory> CreateAsync(
        string connectionString,
        ReceiverMode receiverMode)
    {
        var factory = new AlertReceiverFactory(connectionString, receiverMode);

        try
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AlertReceiverDbContext>();
            await dbContext.Database.MigrateAsync();
            return factory;
        }
        catch
        {
            await factory.DisposeAsync();
            throw;
        }
    }

    public async Task<T> ExecuteDbContextAsync<T>(
        Func<AlertReceiverDbContext, Task<T>> action)
    {
        await using var scope = Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AlertReceiverDbContext>();
        return await action(dbContext);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:AlertReceiver", connectionString);
        builder.UseSetting("Receiver:Authorization", Authorization);
        builder.UseSetting("Receiver:Mode", receiverMode.ToString());
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(
                new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["ConnectionStrings:AlertReceiver"] = connectionString,
                    ["Receiver:Authorization"] = Authorization,
                    ["Receiver:Mode"] = receiverMode.ToString(),
                });
        });
    }
}
