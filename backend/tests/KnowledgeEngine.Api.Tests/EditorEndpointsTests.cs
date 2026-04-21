using System.Net;
using System.Net.Http.Json;

namespace KnowledgeEngine.Api.Tests;

public class EditorEndpointsTests : IntegrationTestBase
{
    [Fact]
    public async Task GetContent_BookExists_ReturnsMarkdown()
    {
        var slug = await CreateBookAsync("Content Book");
        CreateLibraryFile(slug, "book.md", "# My Book\nHello world");

        var response = await Client.GetAsync($"/api/books/{slug}/content");
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Hello world", body);
    }

    [Fact]
    public async Task GetContent_NoContent_Returns404()
    {
        var slug = await CreateBookAsync("Empty Book");
        var response = await Client.GetAsync($"/api/books/{slug}/content");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task SaveContent_Valid_OverwritesFile()
    {
        var slug = await CreateBookAsync("Save Book");
        CreateLibraryFile(slug, "book.md", "# Old");

        var response = await Client.PutAsJsonAsync($"/api/books/{slug}/content",
            new { content = "# Updated\nNew content" });
        response.EnsureSuccessStatusCode();

        var file = Path.Combine(Factory.TempLibraryPath, slug, "book.md");
        var saved = await File.ReadAllTextAsync(file);
        Assert.Contains("New content", saved);
    }

    [Fact]
    public async Task GetMetadata_ReturnsMetadata()
    {
        var slug = await CreateBookAsync("Meta Book");
        CreateLibraryFile(slug, "metadata.json", "{\"wordCount\": 3, \"title\": \"Meta Book\"}");

        var response = await Client.GetAsync($"/api/books/{slug}/content/metadata");
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("wordCount", body);
    }
}
