using Azure;
using Azure.Storage.Blobs.Models;
using WellpumpClassifier.Data;
using WellpumpClassifier.Models;
using BlobLease = WellpumpClassifier.Data.BlobLease;

namespace WellpumpClassifier;

public sealed class Retention(Storage storage, Admission admission, DemoLimits limits, ILogger<Retention> logger)
{
    public async Task SweepAsync(CancellationToken ct)
    {
        List<AdmissionEntry> entries;
        try { entries = await admission.EntriesAsync(ct); }
        catch (RequestFailedException ex) when (ex.Status == 404) { entries = []; }
        foreach (var entry in entries)
        {
            if (!entry.Work.IsValid) throw new InvalidDataException("Invalid retention ledger entry.");
            if (entry.Phase == "uploading" && entry.CreatedAt < DateTimeOffset.UtcNow.AddMinutes(-15))
            {
                try
                {
                    await using var abandoned = await BlobLease.TryAcquireAsync(storage.Manifest(entry.Work), ct);
                    if (abandoned is null) continue;
                    var checkpoint = await JobCheckpoint.LoadAsync(storage.Manifest(entry.Work), abandoned);
                    if (checkpoint.Job.Status == "uploading")
                    {
                        checkpoint.Job.Status = "failed";
                        checkpoint.Job.Error = "Upload was interrupted. Please upload again.";
                        await checkpoint.SaveAsync();
                    }
                    await admission.UpdateAsync(entry.Work, checkpoint.Job.IsTerminal ? "terminal" : "pending", checkpoint.Job.RetainedBytes, ct);
                }
                catch (RequestFailedException ex) when (ex.Status == 404)
                { await admission.UpdateAsync(entry.Work, "terminal", null, ct); }
            }
            if (entry.CreatedAt.AddHours(limits.RetentionHours) > DateTimeOffset.UtcNow) continue;
            var manifest = storage.Manifest(entry.Work);
            BlobLease? lease = null;
            try { lease = await BlobLease.TryAcquireAsync(manifest, ct); if (lease is null) continue; }
            catch (RequestFailedException ex) when (ex.Status == 404) { }
            await using (lease)
            {
                var prefix = Storage.Prefix(entry.Work);
                await foreach (var blob in storage.Container.GetBlobsAsync(BlobTraits.None, BlobStates.None, prefix, ct))
                {
                    if (blob.Name == manifest.Name) continue;
                    await storage.Container.GetBlobClient(blob.Name).DeleteIfExistsAsync(cancellationToken: ct);
                }
                if (lease is not null)
                    await manifest.DeleteIfExistsAsync(conditions: new BlobRequestConditions { LeaseId = lease.Id }, cancellationToken: ct);
                await admission.RemoveAsync(entry.Work, ct);
                logger.LogInformation("Expired job {JobId} removed.", entry.Work.Id);
            }
        }
        // Sweep old unregistered/orphaned uploads, but only names in this application's
        // exact opaque owner/job namespace. Never touch unrelated container contents.
        var known = entries.Where(e => e.Work.IsValid).Select(e => Storage.Prefix(e.Work)).ToHashSet();
        await foreach (var blob in storage.Container.GetBlobsAsync(BlobTraits.None, BlobStates.None, "owners/", ct))
        {
            var segments = blob.Name.Split('/');
            if (segments.Length < 5 || segments[0] != "owners" || segments[2] != "jobs") continue;
            var work = new WorkItem(segments[1], segments[3]);
            if (!work.IsValid || known.Contains(Storage.Prefix(work))
                || blob.Properties.LastModified is null
                || blob.Properties.LastModified > DateTimeOffset.UtcNow.AddHours(-limits.RetentionHours)) continue;
            await storage.Container.GetBlobClient(blob.Name).DeleteIfExistsAsync(cancellationToken: ct);
        }
        // Age out counters left by earlier releases; no new usage counters are written.
        foreach (var prefix in new[] { "control/rates/", "quota/" })
            await foreach (var blob in storage.Container.GetBlobsAsync(BlobTraits.None, BlobStates.None, prefix, ct))
                if (blob.Properties.LastModified < DateTimeOffset.UtcNow.AddDays(-2))
                    await storage.Container.GetBlobClient(blob.Name).DeleteIfExistsAsync(cancellationToken: ct);
        PdfSubprocess.CleanupStaleTemp();
    }
}
