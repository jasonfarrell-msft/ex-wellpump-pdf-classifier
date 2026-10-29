namespace WellpumpClassifier.Models;

public sealed class Job
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string OwnerHash { get; set; } = "";
    public string FileName { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; set; } = DateTimeOffset.UtcNow.AddHours(24);
    public bool SplitComplete { get; set; }
    public long? RetainedBytes { get; set; }
    public string Status { get; set; } = "uploading";
    public string? Error { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastEnqueuedAt { get; set; }
    public int InfrastructureFailures { get; set; }
    public List<PageState> Pages { get; set; } = [];
    public bool IsTerminal => Status is "completed" or "completed_with_errors" or "failed";
}

public sealed class PageState
{
    public int PageNumber { get; set; }
    public bool BlobReady { get; set; }
    public string Status { get; set; } = "queued";
    public string? Classification { get; set; }
    public double? Confidence { get; set; }
    public string? Error { get; set; }
    public string? OperationId { get; set; }
    public int Submissions { get; set; }
    public int PollFailures { get; set; }
    public DateTimeOffset? OperationStartedAt { get; set; }
    public bool IsTerminal => Status is "completed" or "failed";
}

public sealed record WorkItem(string OwnerHash, string Id)
{
    public bool IsValid => OwnerHash.Length == 64 && OwnerHash.All(Uri.IsHexDigit)
        && Id.Length == 32 && Id.All(Uri.IsHexDigit);
}

public static class ProcessingRules
{
    public static readonly HashSet<string> Classes =
    [
        "document_control", "engineering_diagram", "notes", "operating_maintenance",
        "parts_list", "pump_curve", "technical_datasheet"
    ];
    public static TimeSpan RetryDelay(int attempt) =>
        TimeSpan.FromSeconds(Math.Min(300, Math.Pow(2, Math.Clamp(attempt, 1, 9))));
    public static bool IsTransient(int status) => status is 408 or 429 || status >= 500;
    public static PageState? NextPage(Job job) => job.Pages.FirstOrDefault(p => !p.IsTerminal);
}
