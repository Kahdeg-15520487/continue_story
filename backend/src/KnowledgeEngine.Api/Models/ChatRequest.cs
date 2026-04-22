namespace KnowledgeEngine.Api.Models;

public class ChatRequest
{
    public string BookSlug { get; set; } = "";
    public string Message { get; set; } = "";
    public string? ActiveChapterId { get; set; }
    public string? SessionId { get; set; }
}
