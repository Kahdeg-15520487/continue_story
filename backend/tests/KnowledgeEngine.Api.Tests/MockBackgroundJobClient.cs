using System.Collections.Concurrent;

namespace KnowledgeEngine.Api.Tests;

/// <summary>
/// Records jobs that were enqueued without actually processing them.
/// Does NOT implement Hangfire's IBackgroundJobClient (too many internal types).
/// Tests check EnqueuedJobs directly.
/// </summary>
public class MockBackgroundJobClient
{
    public ConcurrentBag<(string Method, string JobId)> EnqueuedJobs { get; } = [];
    private int _counter = 0;

    public string Enqueue<TJob>(System.Linq.Expressions.Expression<Action<TJob>> method)
    {
        var jobId = $"test-job-{Interlocked.Increment(ref _counter)}";
        var call = (method.Body as System.Linq.Expressions.MethodCallExpression);
        var methodStr = call != null ? $"{call.Method.DeclaringType?.Name}.{call.Method.Name}" : "unknown";
        EnqueuedJobs.Add((methodStr, jobId));
        return jobId;
    }
}
