namespace KnowledgeEngine.Api.Models;

public class Book
{
    public int Id { get; set; }
    public string Slug { get; set; } = "";        // URL-safe folder name
    public string Title { get; set; } = "";
    public string? Author { get; set; }
    public int? Year { get; set; }
    public string? SourceFile { get; set; }        // Original uploaded filename
    public string Status { get; set; } = "pending"; // pending, converting, ready, error
    public string? ErrorMessage { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
