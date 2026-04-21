using Hangfire;
using Hangfire.SQLite;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using KnowledgeEngine.Api.Data;
using KnowledgeEngine.Api.Services;

namespace KnowledgeEngine.Api.Tests;

public class CustomWebApplicationFactory : WebApplicationFactory<Program>
{
    public MockAgentService MockAgent { get; private set; } = null!;
    public string TempLibraryPath { get; private set; } = null!;
    public MockBackgroundJobClient MockJobs { get; private set; } = null!;

    private string _dbName = $"ke-test-{Guid.NewGuid():N}";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        TempLibraryPath = Path.Combine(Path.GetTempPath(), $"ke-test-lib-{Guid.NewGuid():N}");
        Directory.CreateDirectory(TempLibraryPath);

        builder.UseEnvironment("Testing");
        builder.UseSetting("Library:Path", TempLibraryPath);
        builder.UseSetting("ConnectionStrings:Default", "Data Source=:memory:");
        builder.UseSetting("ConnectionStrings:Hangfire", "Data Source=:memory:");
        builder.UseSetting("AgentHost", "localhost");
        builder.UseSetting("AgentPort", "3001");

        builder.ConfigureServices(services =>
        {
            // Remove all DbContext registrations
            var dbContextDescriptors = services
                .Where(d => d.ServiceType == typeof(DbContextOptions<AppDbContext>)
                         || d.ServiceType == typeof(DbContextOptions)
                         || d.ServiceType == typeof(AppDbContext))
                .ToList();
            foreach (var d in dbContextDescriptors)
                services.Remove(d);

            // Add InMemory DB
            services.AddDbContext<AppDbContext>(options =>
                options.UseInMemoryDatabase(_dbName));

            // Replace agent service with mock
            services.RemoveAll<IAgentService>();
            MockAgent = new MockAgentService();
            services.AddSingleton<IAgentService>(MockAgent);

            // Background job client recorder
            MockJobs = new MockBackgroundJobClient();

            // Initialize Hangfire with SQLite (required for endpoints using IBackgroundJobClient)
            var hangfireDb = Path.Combine(TempLibraryPath, "hangfire-test.db");
            services.AddHangfire(config =>
                config.UseSQLiteStorage($"Data Source={hangfireDb};"));
        });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && TempLibraryPath != null && Directory.Exists(TempLibraryPath))
        {
            try { Directory.Delete(TempLibraryPath, recursive: true); } catch { }
        }
        base.Dispose(disposing);
    }
}
