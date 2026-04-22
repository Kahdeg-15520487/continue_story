using System.ComponentModel.DataAnnotations;
using System.Text.Json;

namespace KnowledgeEngine.Api.Models;

public class ChatMessage
{
    public int Id { get; set; }
    public int BookId { get; set; }
    public string Role { get; set; } = ""; // "user" or "assistant"
    public string Content { get; set; } = "";
    public string? Thinking { get; set; }
    public string? SessionId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public Book Book { get; set; } = null!;
}
