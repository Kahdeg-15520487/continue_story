namespace KnowledgeEngine.Api.Models;

public class ConversionJob
{
    public int Id { get; set; }
    public int BookId { get; set; }
    public string InputPath { get; set; } = "";
    public string OutputPath { get; set; } = "";
    public string Status { get; set; } = "queued"; // queued, processing, completed, failed
    public string? ErrorMessage { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
}
