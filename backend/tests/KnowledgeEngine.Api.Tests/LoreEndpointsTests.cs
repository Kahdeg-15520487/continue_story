using System.Net;
using System.Net.Http.Json;
using KnowledgeEngine.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace KnowledgeEngine.Api.Tests;

public class LoreEndpointsTests : IntegrationTestBase
{
    private async Task<string> CreateBookWithWikiAsync(string title)
    {
        var slug = await CreateBookAsync(title);
        CreateLibraryFile(slug, "wiki/characters.md", "# Characters\n- Alice");
        CreateLibraryFile(slug, "wiki/locations.md", "# Locations\n- The Castle");
        CreateLibraryFile(slug, "wiki/themes.md", "# Themes\n- Courage");
        CreateLibraryFile(slug, "wiki/summary.md", "# Summary\nA great story");
        CreateLibraryFile(slug, "wiki/chapter-summaries.md", "# Chapters\nCh 1 summary");
        return slug;
    }

    [Fact]
    public async Task ListLoreFiles_ReturnsAllMdFiles()
    {
        var slug = await CreateBookWithWikiAsync("Lore List");
        var response = await Client.GetAsync($"/api/books/{slug}/lore");
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("characters.md", body);
        Assert.Contains("chapter-summaries.md", body);
    }

    [Fact]
    public async Task GetLoreFile_Exists_ReturnsContent()
    {
        var slug = await CreateBookWithWikiAsync("Lore Get");
        var response = await Client.GetAsync($"/api/books/{slug}/lore/characters.md");
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Alice", body);
    }

    [Fact]
    public async Task GetLoreFile_NotFound_Returns404()
    {
        var slug = await CreateBookWithWikiAsync("Lore Missing");
        var response = await Client.GetAsync($"/api/books/{slug}/lore/nonexistent.md");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task TriggerLore_EnqueuesJob()
    {
        var slug = await CreateBookAsync("Lore Trigger");
        var response = await Client.PostAsync($"/api/books/{slug}/lore", null);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("queued", body);
    }

    [Fact]
    public async Task RetryLore_ErrorStatus_EnqueuesJob()
    {
        var slug = await CreateBookAsync("Retry Test");
        await SetBookStatusAsync(slug, "error", "Something failed");

        var response = await Client.PostAsync($"/api/books/{slug}/lore/retry", null);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("re-queued", body);
    }

    [Fact]
    public async Task RetryLore_NotErrorStatus_Returns400()
    {
        var slug = await CreateBookAsync("No Retry");
        await SetBookStatusAsync(slug, "lore-ready");

        var response = await Client.PostAsync($"/api/books/{slug}/lore/retry", null);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
