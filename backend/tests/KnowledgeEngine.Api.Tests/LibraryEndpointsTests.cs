using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace KnowledgeEngine.Api.Tests;

public class LibraryEndpointsTests : IntegrationTestBase
{
    [Fact]
    public async Task CreateBook_ValidRequest_ReturnsCreated()
    {
        var response = await Client.PostAsJsonAsync("/api/books", new { title = "My Test Book" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<BookResponse>();
        Assert.Equal("my-test-book", body!.Slug);
    }

    [Fact]
    public async Task CreateBook_MissingTitle_ReturnsBadRequest()
    {
        var response = await Client.PostAsJsonAsync("/api/books", new { });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetAllBooks_ReturnsList()
    {
        await CreateBookAsync("Book A");
        await CreateBookAsync("Book B");

        var response = await Client.GetAsync("/api/books");
        response.EnsureSuccessStatusCode();
        var books = await response.Content.ReadFromJsonAsync<List<BookResponse>>();
        Assert.Equal(2, books!.Count);
    }

    [Fact]
    public async Task GetBookBySlug_Exists_ReturnsBook()
    {
        await CreateBookAsync("Find Me");
        var response = await Client.GetAsync("/api/books/find-me");
        response.EnsureSuccessStatusCode();
        var book = await response.Content.ReadFromJsonAsync<BookResponse>();
        Assert.Equal("Find Me", book!.Title);
    }

    [Fact]
    public async Task GetBookBySlug_NotFound_Returns404()
    {
        var response = await Client.GetAsync("/api/books/nonexistent");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DeleteBook_Exists_ReturnsOk()
    {
        var slug = await CreateBookAsync("Delete Me");
        var response = await Client.DeleteAsync($"/api/books/{slug}");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var getResponse = await Client.GetAsync($"/api/books/{slug}");
        Assert.Equal(HttpStatusCode.NotFound, getResponse.StatusCode);
    }

    [Fact]
    public async Task DeleteBook_NotFound_Returns404()
    {
        var response = await Client.DeleteAsync("/api/books/nonexistent");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task CreateBook_DuplicateTitle_ReturnsOk()
    {
        await CreateBookAsync("Duplicate");
        var response = await Client.PostAsJsonAsync("/api/books", new { title = "Duplicate" });
        Assert.True(response.StatusCode == HttpStatusCode.Created || response.StatusCode == HttpStatusCode.Conflict);
    }
}
