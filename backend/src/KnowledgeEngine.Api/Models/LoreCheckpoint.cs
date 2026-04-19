using System.ComponentModel.DataAnnotations;
using System.Text.Json;

namespace KnowledgeEngine.Api.Models;

public class LoreCheckpoint
{
    public int Id { get; set; }
    public int BookId { get; set; }
    public string Slug { get; set; } = "";
    public string TargetFile { get; set; } = ""; // e.g., "characters.md"
    public string Status { get; set; } = ""; // "pending", "done", "failed"
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public Book Book { get; set; } = null!;
}
