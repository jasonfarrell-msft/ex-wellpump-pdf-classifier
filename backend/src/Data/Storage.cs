using System.Runtime.CompilerServices;
using System.Text.Json;
using Azure;
using Azure.Core;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using Azure.Storage.Queues;
using Azure.Storage.Sas;
using WellpumpClassifier.Models;

namespace WellpumpClassifier.Data;

public sealed class CloudSettings
{
    public string BlobEndpoint { get; set; } = "https://stwellpumpdocsswmx01.blob.core.windows.net/";
    public string QueueEndpoint { get; set; } = "https://stwellpumpdocsswmx01.queue.core.windows.net/";
    public string DocumentIntelligenceEndpoint { get; set; } = "https://doc-wellpump-swc-mx01.cognitiveservices.azure.com/";
    public string ClassifierId { get; set; } = "wellpump-page-classifier-v1.1";
    public string PublicOrigin { get; set; } = "";
    public string Credential { get; set; } = "ManagedIdentity";

    public void Validate(bool development)
    {
        foreach (var endpoint in new[] { BlobEndpoint, QueueEndpoint, DocumentIntelligenceEndpoint })
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != "https"
                || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || uri.AbsolutePath != "/")
                throw new InvalidOperationException("Azure endpoints must be HTTPS service origins, without credentials or queries.");
        if (!Uri.TryCreate(PublicOrigin, UriKind.Absolute, out var origin)
            || (!development && origin.Scheme != "https")
            || origin.GetLeftPart(UriPartial.Authority) != PublicOrigin)
            throw new InvalidOperationException("Cloud:PublicOrigin must be the exact public HTTPS origin (no trailing slash).");
        if (Credential != "ManagedIdentity" && !(development && Credential == "AzureCli"))
            throw new InvalidOperationException("Only system-assigned ManagedIdentity, or explicit Development AzureCli, is allowed.");
    }
}

public sealed class Storage
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public BlobServiceClient Service { get; }
    public BlobContainerClient Container { get; }
    public QueueClient Queue { get; }
    public QueueClient Poison { get; }

    public Storage(CloudSettings settings, TokenCredential credential)
    {
        Service = new BlobServiceClient(new Uri(settings.BlobEndpoint), credential);
        Container = Service.GetBlobContainerClient("uploads");
        var options = new QueueClientOptions { MessageEncoding = QueueMessageEncoding.Base64 };
        Queue = new QueueClient(new Uri($"{settings.QueueEndpoint.TrimEnd('/')}/wellpump-jobs"), credential, options);
        Poison = new QueueClient(new Uri($"{settings.QueueEndpoint.TrimEnd('/')}/wellpump-poison"), credential, options);
    }

    public static string Prefix(WorkItem work) => $"owners/{work.OwnerHash}/jobs/{work.Id}/";
    public BlobClient Manifest(WorkItem work) => Container.GetBlobClient(Prefix(work) + "manifest.json");
    public BlobClient Page(WorkItem work, int number) => Container.GetBlobClient(Prefix(work) + $"pages/{number}.pdf");

    public async Task<Job?> ReadAsync(WorkItem work, CancellationToken ct)
    {
        try { return (await Manifest(work).DownloadContentAsync(ct)).Value.Content.ToObjectFromJson<Job>(Json); }
        catch (RequestFailedException ex) when (ex.Status == 404) { return null; }
    }

    public async Task CreateAsync(Job job, CancellationToken ct) =>
        await Manifest(new(job.OwnerHash, job.Id)).UploadAsync(BinaryData.FromObjectAsJson(job, Json),
            new BlobUploadOptions { Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All } }, ct);

    public async IAsyncEnumerable<Job> ListAsync(string? owner, [EnumeratorCancellation] CancellationToken ct)
    {
        var prefix = owner is null ? "owners/" : $"owners/{owner}/jobs/";
        await foreach (var item in Container.GetBlobsAsync(BlobTraits.None, BlobStates.None, prefix, ct))
        {
            if (!item.Name.EndsWith("/manifest.json", StringComparison.Ordinal)) continue;
            Job? job;
            try { job = (await Container.GetBlobClient(item.Name).DownloadContentAsync(ct)).Value.Content.ToObjectFromJson<Job>(Json); }
            catch (RequestFailedException ex) when (ex.Status == 404) { continue; }
            if (job is not null) yield return job;
        }
    }

    public async Task UploadPdfAsync(BlobClient blob, string path, string name, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        await UploadPdfStreamAsync(blob, stream, name, ct);
    }

    public async Task UploadPdfStreamAsync(BlobClient blob, Stream stream, string name, CancellationToken ct)
    {
        await blob.UploadAsync(stream, new BlobUploadOptions
        {
            Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All },
            HttpHeaders = new BlobHttpHeaders
            {
                ContentType = "application/pdf", ContentDisposition = $"inline; filename=\"{name}\"",
                CacheControl = "private, no-store"
            }
        }, ct);
    }

    public async Task EnqueueAsync(WorkItem work, TimeSpan delay, CancellationToken ct) =>
        await Queue.SendMessageAsync(JsonSerializer.Serialize(work, Json), delay, TimeSpan.FromSeconds(-1), ct);

    public static BlobSasBuilder PageSas(string container, string blob, DateTimeOffset now)
    {
        var sas = new BlobSasBuilder
        {
            BlobContainerName = container, BlobName = blob, Resource = "b", Protocol = SasProtocol.Https,
            StartsOn = now.AddMinutes(-1), ExpiresOn = now.AddMinutes(5),
            ContentType = "application/pdf", ContentDisposition = "inline; filename=\"page.pdf\"",
            CacheControl = "private, no-store"
        };
        sas.SetPermissions(BlobSasPermissions.Read);
        return sas;
    }

    public async Task<Uri> PageLinkAsync(WorkItem work, int page, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var key = (await Service.GetUserDelegationKeyAsync(now.AddMinutes(-5), now.AddMinutes(10), ct)).Value;
        var blob = Page(work, page);
        var query = PageSas(Container.Name, blob.Name, now).ToSasQueryParameters(key, Service.AccountName);
        return new UriBuilder(blob.Uri) { Query = query.ToString() }.Uri;
    }

}

// The lease renewer cancels dependent work on loss. All manifest writes also carry the
// lease ID and ETag, so a stale owner cannot checkpoint over a replacement worker.
public sealed class BlobLease : IAsyncDisposable
{
    private readonly BlobLeaseClient lease;
    private readonly CancellationTokenSource stop = new();
    private readonly CancellationTokenSource lost;
    private readonly Task renewer;
    public string Id => lease.LeaseId;
    public CancellationToken Token => lost.Token;

    private BlobLease(BlobLeaseClient lease, CancellationToken ct)
    {
        this.lease = lease;
        lost = CancellationTokenSource.CreateLinkedTokenSource(ct);
        renewer = RenewAsync();
    }

    public static async Task<BlobLease?> TryAcquireAsync(BlobClient blob, CancellationToken ct)
    {
        var client = blob.GetBlobLeaseClient();
        try
        {
            await client.AcquireAsync(TimeSpan.FromSeconds(60), cancellationToken: ct);
            return new BlobLease(client, ct);
        }
        catch (RequestFailedException ex) when (ex.Status == 409) { return null; }
    }

    private async Task RenewAsync()
    {
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(20), stop.Token);
                await lease.RenewAsync(cancellationToken: stop.Token);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch { await lost.CancelAsync(); }
    }

    public async ValueTask DisposeAsync()
    {
        await stop.CancelAsync();
        await renewer;
        try { await lease.ReleaseAsync(cancellationToken: CancellationToken.None); }
        catch (RequestFailedException) { /* An expired lease cannot be released by its former owner. */ }
        lost.Dispose();
        stop.Dispose();
    }
}

public interface IJobCheckpoint
{
    Job Job { get; }
    Task SaveAsync();
}

public sealed class JobCheckpoint(BlobClient blob, BlobLease lease, ETag etag, Job job) : IJobCheckpoint
{
    public Job Job { get; } = job;
    private ETag version = etag;
    public async Task SaveAsync()
    {
        var result = await blob.UploadAsync(BinaryData.FromObjectAsJson(Job, Storage.Json), new BlobUploadOptions
        {
            Conditions = new() { LeaseId = lease.Id, IfMatch = version }
        }, lease.Token);
        version = result.Value.ETag;
    }

    public static async Task<JobCheckpoint> LoadAsync(BlobClient blob, BlobLease lease)
    {
        var response = (await blob.DownloadContentAsync(new BlobDownloadOptions
        {
            Conditions = new() { LeaseId = lease.Id }
        }, lease.Token)).Value;
        var job = response.Content.ToObjectFromJson<Job>(Storage.Json) ?? throw new InvalidDataException("Invalid job manifest.");
        return new(blob, lease, response.Details.ETag, job);
    }
}
