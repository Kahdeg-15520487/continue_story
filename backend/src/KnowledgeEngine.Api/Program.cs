using Hangfire;
using Hangfire.SQLite;
using Microsoft.EntityFrameworkCore;
using KnowledgeEngine.Api.Data;
using KnowledgeEngine.Api.Endpoints;
using KnowledgeEngine.Api.Services;

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
    config
        .UseSimpleAssemblyNameTypeSerializer()
        .UseRecommendedSerializerSettings()
        .UseSQLiteStorage(hangfireConnectionString);
    GlobalJobFilters.Filters.Add(new AutomaticRetryAttribute { Attempts = 0 });
});
builder.Services.AddHangfireServer(options => options.WorkerCount = 1);

// Conversion service
builder.Services.AddSingleton<IConversionService, ConversionService>();
builder.Services.AddTransient<ConversionJobService>();

// Agent service
builder.Services.AddHttpClient<IAgentService, AgentService>();
builder.Services.AddTransient<LoreJobService>();
builder.Services.AddScoped<AgentTaskService>();

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

if (app.Environment.IsDevelopment())
    app.UseHangfireDashboard("/hangfire", new DashboardOptions
    {
        Authorization = [] // No auth in development
    });

    // Periodic session cleanup (daily at 3 AM)
    RecurringJob.AddOrUpdate<SessionCleanupService>(
        "session-cleanup",
        x => x.CleanupAsync(),
        "0 3 * * *");

app.Run();
