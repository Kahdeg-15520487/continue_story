using Microsoft.EntityFrameworkCore;
using StoryEngine.Api.Models;

namespace StoryEngine.Api.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Book> Books => Set<Book>();
    public DbSet<ChatMessage> ChatMessages => Set<ChatMessage>();
    public DbSet<LoreCheckpoint> LoreCheckpoints => Set<LoreCheckpoint>();
    public DbSet<AgentTask> AgentTasks => Set<AgentTask>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Book>(b =>
        {
            b.HasIndex(x => x.Slug).IsUnique();
        });
    }
}
