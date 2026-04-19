using System.ComponentModel.DataAnnotations;

namespace KnowledgeEngine.Api.Models;

public class AgentTask
{
    public int Id { get; set; }
    public int BookId { get; set; }
    public string Slug { get; set; } = "";
    public string TaskType { get; set; } = ""; // "lore", "edit", "chapter"
    public string Description { get; set; } = ""; // what the task is doing
    public string Status { get; set; } = ""; // "pending", "running", "done", "failed", "interrupted"
    public string? SessionId { get; set; } // agent session handling this
    public string? Result { get; set; } // summary of what was done
    public string? ErrorMessage { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public Book Book { get; set; } = null!;
}
