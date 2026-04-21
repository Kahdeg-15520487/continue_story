using System.Runtime.CompilerServices;
using Hangfire;
using Hangfire.SQLite;
using Microsoft.EntityFrameworkCore;
using KnowledgeEngine.Api.Data;
using KnowledgeEngine.Api.Endpoints;
using KnowledgeEngine.Api.Services;

[assembly: InternalsVisibleTo("KnowledgeEngine.Api.Tests")]

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = 100 * 1024 * 1024; // 100MB
});

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("Default")));

// Hangfire.Sqlite v1.4.2's IsConnectionString() checks for ";" in the string.
// SQLite connection strings with ";" are recognized as raw connection strings.
var hangfireConnectionString = builder.Configuration.GetConnectionString("Hangfire")
    ?? builder.Configuration.GetConnectionString("Default")
    ?? "Data Source=/data/hangfire.db";

builder.Services.AddHangfire(config =>
{
    if (builder.Environment.IsEnvironment("Testing")) return;
    config
        .UseSimpleAssemblyNameTypeSerializer()
        .UseRecommendedSerializerSettings()
        .UseSQLiteStorage(hangfireConnectionString);
    GlobalJobFilters.Filters.Add(new AutomaticRetryAttribute { Attempts = 0 });
});
// Only run Hangfire if not in test mode
if (!builder.Environment.IsEnvironment("Testing"))
{
    builder.Services.AddHangfireServer(options => options.WorkerCount = 1);
}

// Conversion service
builder.Services.AddSingleton<IConversionService, ConversionService>();
builder.Services.AddTransient<ConversionJobService>();

// Agent service
builder.Services.AddHttpClient<IAgentService, AgentService>();
builder.Services.AddTransient<LoreJobService>();
builder.Services.AddTransient<LoreAutoRetryService>();
builder.Services.AddTransient<ChapterSplitService>();
builder.Services.AddScoped<AgentTaskService>();
builder.Services.AddScoped<ChapterService>();

builder.Services.AddCors(options =>
{
    options.AddPolicy("Frontend", policy =>
        policy.WithOrigins("http://localhost:5173", "http://localhost:5000")
              .AllowAnyMethod()
              .AllowAnyHeader());
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    if (db.Database.IsRelational())
        db.Database.Migrate();
}

app.UseCors("Frontend");

app.MapGet("/api/health", () => Results.Ok(new { status = "healthy", timestamp = DateTime.UtcNow }));

LibraryEndpoints.Map(app);
EditorEndpoints.Map(app);
ConversionEndpoints.Map(app);
UploadEndpoints.Map(app);
ChatEndpoints.Map(app);
ChatHistoryEndpoints.Map(app);
LoreEndpoints.Map(app);
AgentEndpoints.Map(app);
ChapterEndpoints.Map(app);

// Hangfire recurring jobs — only in non-test environments
if (!app.Environment.IsEnvironment("Testing"))
{
    using (var scope = app.Services.CreateScope())
    {
        var recurring = scope.ServiceProvider.GetRequiredService<IRecurringJobManager>();
        recurring.AddOrUpdate<LoreAutoRetryService>(
            "lore-auto-retry",
            x => x.RecoverStuckAsync(),
            "*/10 * * * *"); // every 10 minutes

        recurring.AddOrUpdate<SessionCleanupService>(
            "session-cleanup",
            x => x.CleanupAsync(),
            "0 3 * * *");
    }

    if (app.Environment.IsDevelopment())
        app.UseHangfireDashboard("/hangfire", new DashboardOptions
        {
            Authorization = [] // No auth in development
        });

    // Startup: recover any books stuck mid-generation
    using (var scope = app.Services.CreateScope())
    {
        var autoRetry = scope.ServiceProvider.GetRequiredService<LoreAutoRetryService>();
        await autoRetry.RecoverStuckAsync();
    }
}

app.Run();

// Make Program accessible for WebApplicationFactory
public partial class Program { }
