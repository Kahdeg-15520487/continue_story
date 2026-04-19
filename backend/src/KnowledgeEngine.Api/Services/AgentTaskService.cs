using KnowledgeEngine.Api.Data;
using KnowledgeEngine.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace KnowledgeEngine.Api.Services;

public class AgentTaskService
{
    private readonly ILogger<AgentTaskService> _logger;
    private readonly IServiceScopeFactory _scopeFactory;

    public AgentTaskService(ILogger<AgentTaskService> logger, IServiceScopeFactory scopeFactory)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
    }

    public async Task<AgentTask> CreateTaskAsync(int bookId, string slug, string taskType, string description)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var task = new AgentTask
        {
            BookId = bookId,
            Slug = slug,
            TaskType = taskType,
            Description = description,
            Status = "pending",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        db.AgentTasks.Add(task);
        await db.SaveChangesAsync();
        _logger.LogInformation("Agent task created: {Id} ({Type}) for {Slug}", task.Id, taskType, slug);
        return task;
    }

    public async Task MarkRunningAsync(int taskId, string? sessionId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var task = await db.AgentTasks.FindAsync(taskId);
        if (task is null) return;

        task.Status = "running";
        task.SessionId = sessionId;
        task.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }

    public async Task CompleteTaskAsync(int taskId, string? result = null)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var task = await db.AgentTasks.FindAsync(taskId);
        if (task is null) return;

        task.Status = "done";
        task.Result = result;
        task.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        _logger.LogInformation("Agent task completed: {Id}", taskId);
    }

    public async Task FailTaskAsync(int taskId, string error)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var task = await db.AgentTasks.FindAsync(taskId);
        if (task is null) return;

        task.Status = "failed";
        task.ErrorMessage = error;
        task.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        _logger.LogInformation("Agent task failed: {Id}: {Error}", taskId, error);
    }

    public async Task<List<AgentTask>> GetActiveTasksAsync(int bookId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        return await db.AgentTasks
            .Where(t => t.BookId == bookId && (t.Status == "running" || t.Status == "interrupted"))
            .OrderByDescending(t => t.UpdatedAt)
            .ToListAsync();
    }

    public async Task MarkInterruptedAsync(string sessionId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var tasks = await db.AgentTasks
            .Where(t => t.SessionId == sessionId && t.Status == "running")
            .ToListAsync();

        foreach (var task in tasks)
        {
            task.Status = "interrupted";
            task.UpdatedAt = DateTime.UtcNow;
            _logger.LogInformation("Agent task interrupted: {Id} (session: {Session})", task.Id, sessionId);
        }

        await db.SaveChangesAsync();
    }
}
