using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace KnowledgeEngine.Api.Tests;

public class ChatEndpointsTests : IntegrationTestBase
{
    private async Task<string> CreateBookWithContentAsync(string title)
    {
        var slug = await CreateBookAsync(title);
        CreateLibraryFile(slug, "book.md", "# Test Book\n\nThis is test content for the book.");
        await SetBookStatusAsync(slug, "lore-ready");
        return slug;
    }

    [Fact]
    public async Task SendMessage_FreshSession_InjectsContext()
    {
        var slug = await CreateBookWithContentAsync("Chat Fresh");
        MockAgent.MessageCount = 0;

        var response = await Client.PostAsJsonAsync("/api/chat", new { bookSlug = slug, message = "Hello?" });
        response.EnsureSuccessStatusCode();
        Assert.NotEmpty(MockAgent.SentPrompts);
    }

    [Fact]
    public async Task SendMessage_ExistingSession_SendsUserMessage()
    {
        var slug = await CreateBookWithContentAsync("Chat Existing");
        MockAgent.MessageCount = 5;

        var response = await Client.PostAsJsonAsync("/api/chat", new { bookSlug = slug, message = "Tell me more" });
        response.EnsureSuccessStatusCode();
        Assert.NotEmpty(MockAgent.SentPrompts);
    }

    [Fact]
    public async Task SendMessage_SSE_StreamsResponse()
    {
        var slug = await CreateBookWithContentAsync("Chat SSE");
        MockAgent.MessageCount = 0;

        var response = await Client.PostAsJsonAsync("/api/chat", new { bookSlug = slug, message = "Hello" });
        response.EnsureSuccessStatusCode();
        var contentType = response.Content.Headers.ContentType?.MediaType;
        Assert.Equal("text/event-stream", contentType);
    }
}
