using System.Text.Json;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using WellpumpClassifier.Models;

namespace WellpumpClassifier.Data;

public sealed class DemoLimits
{
    public int RetentionHours { get; set; } = 24;
    public int ParserTimeoutSeconds { get; set; } = 60;
    public void Validate()
    {
        if (RetentionHours is < 1 or > 24 || ParserTimeoutSeconds is < 1 or > 60)
            throw new InvalidOperationException("DemoLimits values are outside supported safety bounds.");
    }
}

public sealed class AdmissionEntry
{
    public WorkItem Work { get; set; } = new("", "");
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string Phase { get; set; } = "uploading";
    public long ReservedBytes { get; set; }
}

public sealed class AdmissionLedger
{
    public List<AdmissionEntry> Entries { get; set; } = [];
}

// Keep durable job tracking for reconciliation and retention without imposing admission caps.
public sealed class Admission(Storage storage)
{
    private BlobClient Ledger => storage.Container.GetBlobClient("control/admission.json");

    public async Task ReserveAsync(WorkItem work, CancellationToken ct)
    {
        await MutateAsync<object?>(ledger =>
        {
            ledger.Entries.Add(new() { Work = work });
            return null;
        }, ct);
    }

    public async Task UpdateAsync(WorkItem work, string phase, long? actualBytes, CancellationToken ct) =>
        await MutateAsync<object?>(ledger =>
        {
            var entry = ledger.Entries.SingleOrDefault(e => e.Work == work)
                ?? throw new InvalidDataException("Job admission reservation is missing.");
            entry.Phase = phase;
            if (actualBytes is not null)
            {
                if (actualBytes < 0) throw new InvalidDataException("Invalid retained byte count.");
                entry.ReservedBytes = actualBytes.Value;
            }
            return null;
        }, ct);

    public async Task RemoveAsync(WorkItem work, CancellationToken ct) =>
        await MutateAsync<object?>(ledger => { ledger.Entries.RemoveAll(e => e.Work == work); return null; }, ct);

    public async Task<List<AdmissionEntry>> EntriesAsync(CancellationToken ct) =>
        (await Ledger.DownloadContentAsync(ct)).Value.Content.ToObjectFromJson<AdmissionLedger>(Storage.Json)?.Entries
            ?? throw new InvalidDataException("Invalid admission ledger.");

    private async Task<T> MutateAsync<T>(Func<AdmissionLedger, T> action, CancellationToken ct)
    {
        try { await Ledger.UploadAsync(BinaryData.FromObjectAsJson(new AdmissionLedger(), Storage.Json),
            new BlobUploadOptions { Conditions = new() { IfNoneMatch = ETag.All } }, ct); }
        catch (RequestFailedException ex) when (ex.Status is 409 or 412) { }
        BlobLease? lease = null;
        for (var attempt = 0; attempt < 30 && lease is null; attempt++)
        {
            lease = await BlobLease.TryAcquireAsync(Ledger, ct);
            if (lease is null) await Task.Delay(TimeSpan.FromMilliseconds(100), ct);
        }
        if (lease is null) throw new IOException("Admission ledger is busy.");
        await using (lease)
        {
            var response = (await Ledger.DownloadContentAsync(lease.Token)).Value;
            var ledger = response.Content.ToObjectFromJson<AdmissionLedger>(Storage.Json)
                ?? throw new InvalidDataException("Invalid admission ledger.");
            var result = action(ledger);
            await Ledger.UploadAsync(BinaryData.FromObjectAsJson(ledger, Storage.Json), new BlobUploadOptions
            { Conditions = new() { LeaseId = lease.Id, IfMatch = response.Details.ETag } }, lease.Token);
            return result;
        }
    }
}
