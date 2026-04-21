using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace KnowledgeEngine.Api.Tests;

/// <summary>
/// Base class for integration tests. Creates a fresh WebApplicationFactory per test class.
/// Each derived class gets its own InMemory DB and temp library directory.
/// </summary>
public abstract class IntegrationTestBase : IDisposable
{
    protected CustomWebApplicationFactory Factory { get; }
    protected HttpClient Client { get; }
    protected MockAgentService MockAgent => Factory.MockAgent;

    protected IntegrationTestBase()
    {
        Factory = new CustomWebApplicationFactory();
        Client = Factory.CreateClient();
    }

    protected async Task<string> CreateBookAsync(string title)
    {
        var response = await Client.PostAsJsonAsync("/api/books", new { title });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<BookResponse>();
        return body!.Slug;
    }

    protected void CreateLibraryFile(string slug, string relativePath, string content)
    {
        var path = Path.Combine(Factory.TempLibraryPath, slug, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    protected async Task SetBookStatusAsync(string slug, string status, string? errorMessage = null)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Data.AppDbContext>();
        var book = await db.Books.FirstOrDefaultAsync(b => b.Slug == slug);
        if (book != null)
        {
            book.Status = status;
            book.ErrorMessage = errorMessage;
            book.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }
    }

    public void Dispose()
    {
        Client.Dispose();
        Factory.Dispose();
        GC.SuppressFinalize(this);
    }
}

public record BookResponse(int Id, string Slug, string Title, string Status);
