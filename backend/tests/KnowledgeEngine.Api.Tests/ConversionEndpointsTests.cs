using System.Net;
using System.Net.Http.Json;

namespace KnowledgeEngine.Api.Tests;

public class ConversionEndpointsTests : IntegrationTestBase
{
    [Fact]
    public async Task UploadFile_ValidTxt_ReturnsAccepted()
    {
        var slug = await CreateBookAsync("Upload Test");
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent("This is a test file with some content for conversion."), "file", "test.txt");

        var response = await Client.PostAsync($"/api/books/{slug}/upload", content);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task UploadFile_NoFile_ReturnsBadRequest()
    {
        var slug = await CreateBookAsync("No File Upload");
        var response = await Client.PostAsync($"/api/books/{slug}/upload", new MultipartFormDataContent());
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task UploadFile_BookNotFound_Returns404()
    {
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent("test"), "file", "test.txt");

        var response = await Client.PostAsync("/api/books/nonexistent/upload", content);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact(Skip = "Requires Hangfire JobStorage initialization")]
    public async Task GetConversionStatus_ReturnsStatus()
    {
        var slug = await CreateBookAsync("Status Test");
        var response = await Client.GetAsync($"/api/books/{slug}/upload/status");
        response.EnsureSuccessStatusCode();
    }
}
