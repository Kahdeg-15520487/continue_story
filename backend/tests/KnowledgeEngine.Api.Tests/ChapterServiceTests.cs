using KnowledgeEngine.Api.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace KnowledgeEngine.Api.Tests;

public class ChapterServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ChapterService _service;

    public ChapterServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ch-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Library:Path"] = _tempDir })
            .Build();

        _service = new ChapterService(
            LoggerFactory.Create(b => { }).CreateLogger<ChapterService>(),
            config);
    }

    private void CreateChapterFile(string slug, string fileName, string content)
    {
        var dir = Path.Combine(_tempDir, slug, "chapters");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, fileName), content);
    }

    [Fact]
    public async Task ListChapters_NoChapters_ReturnsEmpty()
    {
        var chapters = await _service.ListChaptersAsync("no-book");
        Assert.Empty(chapters);
    }

    [Fact]
    public async Task ListChapters_ThreeChapters_ReturnsOrdered()
    {
        CreateChapterFile("mybook", "ch-001-alpha.md", "# Alpha\nContent A");
        CreateChapterFile("mybook", "ch-002-beta.md", "# Beta\nContent B");
        CreateChapterFile("mybook", "ch-003-gamma.md", "# Gamma\nContent C");

        var chapters = await _service.ListChaptersAsync("mybook");
        Assert.Equal(3, chapters.Count);
        Assert.Equal("Alpha", chapters[0].Title);
        Assert.Equal("Beta", chapters[1].Title);
        Assert.Equal("Gamma", chapters[2].Title);
        Assert.Equal(1, chapters[0].Number);
        Assert.Equal(2, chapters[1].Number);
        Assert.Equal(3, chapters[2].Number);
    }

    [Fact]
    public async Task GetChapter_Exists_ReturnsContent()
    {
        CreateChapterFile("mybook", "ch-001-hello.md", "# Hello World\nSome content here");

        var chapter = await _service.GetChapterAsync("mybook", "ch-001-hello");
        Assert.NotNull(chapter);
        Assert.Equal("Hello World", chapter!.Title);
        Assert.Contains("Some content here", chapter.Content);
    }

    [Fact]
    public async Task GetChapter_NotFound_ReturnsNull()
    {
        var chapter = await _service.GetChapterAsync("mybook", "ch-999-nonexistent");
        Assert.Null(chapter);
    }

    [Fact]
    public async Task SaveChapter_Exists_OverwritesContent()
    {
        CreateChapterFile("mybook", "ch-001-test.md", "# Test\nOld content");
        await _service.SaveChapterAsync("mybook", "ch-001-test", "# Updated\nNew content");

        var chapter = await _service.GetChapterAsync("mybook", "ch-001-test");
        Assert.NotNull(chapter);
        Assert.Contains("New content", chapter!.Content);
    }

    [Fact]
    public async Task SaveChapter_NotFound_Throws()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            _service.SaveChapterAsync("mybook", "ch-999-nope", "content"));
    }

    [Fact]
    public async Task InsertChapter_Append_CreatesAtEnd()
    {
        CreateChapterFile("mybook", "ch-001-first.md", "# First\nContent");

        var result = await _service.InsertChapterAsync("mybook", "Chapter Two", null);
        Assert.Equal("Chapter Two", result.Title);
        Assert.Equal(2, result.Number);

        var chapters = await _service.ListChaptersAsync("mybook");
        Assert.Equal(2, chapters.Count);
        Assert.Equal("Chapter Two", chapters[1].Title);
    }

    [Fact]
    public async Task InsertChapter_InMiddle_Renumbers()
    {
        CreateChapterFile("mybook", "ch-001-alpha.md", "# Alpha\nContent");
        CreateChapterFile("mybook", "ch-002-gamma.md", "# Gamma\nContent");

        var result = await _service.InsertChapterAsync("mybook", "Beta", "ch-001-alpha");
        Assert.Equal("Beta", result.Title);

        var chapters = await _service.ListChaptersAsync("mybook");
        Assert.Equal(3, chapters.Count);
        // Order should be: Alpha, Beta, Gamma
        Assert.Equal("Alpha", chapters[0].Title);
        Assert.Equal("Beta", chapters[1].Title);
        Assert.Equal("Gamma", chapters[2].Title);
    }

    [Fact]
    public async Task DeleteChapter_Middle_Renumbers()
    {
        CreateChapterFile("mybook", "ch-001-alpha.md", "# Alpha\nContent");
        CreateChapterFile("mybook", "ch-002-beta.md", "# Beta\nContent");
        CreateChapterFile("mybook", "ch-003-gamma.md", "# Gamma\nContent");

        await _service.DeleteChapterAsync("mybook", "ch-002-beta");

        var chapters = await _service.ListChaptersAsync("mybook");
        Assert.Equal(2, chapters.Count);
        Assert.Equal("Alpha", chapters[0].Title);
        Assert.Equal("Gamma", chapters[1].Title);
        Assert.Equal(1, chapters[0].Number);
        Assert.Equal(2, chapters[1].Number);
    }

    [Fact]
    public async Task DeleteChapter_Last_NoRenumber()
    {
        CreateChapterFile("mybook", "ch-001-alpha.md", "# Alpha\nContent");
        CreateChapterFile("mybook", "ch-002-beta.md", "# Beta\nContent");

        await _service.DeleteChapterAsync("mybook", "ch-002-beta");

        var chapters = await _service.ListChaptersAsync("mybook");
        Assert.Single(chapters);
        Assert.Equal("Alpha", chapters[0].Title);
    }

    [Fact]
    public async Task ReorderChapters_ValidOrder_RenamesFiles()
    {
        CreateChapterFile("mybook", "ch-001-alpha.md", "# Alpha\nContent A");
        CreateChapterFile("mybook", "ch-002-beta.md", "# Beta\nContent B");
        CreateChapterFile("mybook", "ch-003-gamma.md", "# Gamma\nContent C");

        // Reverse order
        var chapters = await _service.ListChaptersAsync("mybook");
        var reversedIds = chapters.OrderByDescending(c => c.Number).Select(c => c.Id).ToArray();
        await _service.ReorderChaptersAsync("mybook", reversedIds);

        var reordered = await _service.ListChaptersAsync("mybook");
        Assert.Equal(3, reordered.Count);
        Assert.Equal("Gamma", reordered[0].Title);
        Assert.Equal("Beta", reordered[1].Title);
        Assert.Equal("Alpha", reordered[2].Title);
    }

    [Fact]
    public async Task ReorderChapters_MissingId_Throws()
    {
        CreateChapterFile("mybook", "ch-001-alpha.md", "# Alpha\nContent");

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            _service.ReorderChaptersAsync("mybook", new[] { "ch-999-nonexistent" }));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            try { Directory.Delete(_tempDir, true); } catch { }
    }
}
