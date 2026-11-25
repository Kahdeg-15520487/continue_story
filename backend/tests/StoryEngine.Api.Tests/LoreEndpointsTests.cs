using System.Net;
using System.Net.Http.Json;
using StoryEngine.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace StoryEngine.Api.Tests;

public class LoreEndpointsTests : IntegrationTestBase
{
    private async Task<string> CreateBookWithWikiAsync(string title)
    {
        var slug = await CreateBookAsync(title);
        CreateLibraryFile(slug, "wiki/characters/alice.md", "# Alice\n- Protagonist");
        CreateLibraryFile(slug, "wiki/locations/castle.md", "# The Castle\n- Setting");
        CreateLibraryFile(slug, "wiki/summary.md", "# Summary\nA great story");
        return slug;
    }

    [Fact]
    public async Task ListLoreFiles_ReturnsAllMdFiles()
    {
        var slug = await CreateBookWithWikiAsync("Lore List");
        var response = await Client.GetAsync($"/api/books/{slug}/lore");
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("characters", body);
        Assert.Contains("alice", body);
    }

    [Fact]
    public async Task GetLoreFile_Exists_ReturnsContent()
    {
        var slug = await CreateBookWithWikiAsync("Lore Get");
        var response = await Client.GetAsync($"/api/books/{slug}/lore/characters/alice.md");
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Alice", body);
    }

    [Fact]
    public async Task GetLoreFile_NotFound_Returns404()
    {
        var slug = await CreateBookWithWikiAsync("Lore Missing");
        var response = await Client.GetAsync($"/api/books/{slug}/lore/characters/missing.md");
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
