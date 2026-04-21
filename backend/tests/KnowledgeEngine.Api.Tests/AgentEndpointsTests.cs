using System.Net;
using System.Net.Http.Json;
using KnowledgeEngine.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace KnowledgeEngine.Api.Tests;

public class AgentEndpointsTests : IntegrationTestBase
{
    [Fact]
    public async Task CreateSession_ValidRequest_ReturnsSessionId()
    {
        var response = await Client.PostAsJsonAsync("/api/agent/session",
            new { bookSlug = "test-book", mode = "read" });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("sessionId", body);
    }

    [Fact]
    public async Task CreateSession_InvalidSlug_Returns400()
    {
        var response = await Client.PostAsJsonAsync("/api/agent/session",
            new { bookSlug = "../etc/passwd", mode = "read" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetAgentTasks_NoTasks_ReturnsEmpty()
    {
        var slug = await CreateBookAsync("Task Test");
        var response = await Client.GetAsync($"/api/agent/tasks/{slug}");
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal("[]", body);
    }

    [Fact]
    public async Task GetAgentTasks_WithInterrupted_ReturnsList()
    {
        var slug = await CreateBookAsync("Interrupted Book");
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Data.AppDbContext>();
        var book = await db.Books.FirstAsync(b => b.Slug == slug);

        db.AgentTasks.Add(new AgentTask
        {
            BookId = book.Id,
            Slug = slug,
            TaskType = "lore",
            Description = "Test task",
            Status = "interrupted",
        });
        await db.SaveChangesAsync();

        var response = await Client.GetAsync($"/api/agent/tasks/{slug}");
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("interrupted", body);
    }
}
