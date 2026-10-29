using System.Text.Json;
using Azure;
using Azure.Storage.Queues.Models;
using WellpumpClassifier.Data;
using WellpumpClassifier.Models;

namespace WellpumpClassifier;

public sealed class JobWorker(Storage storage, PageProcessor processor, PdfSubprocess splitter, Admission admission,
    Retention retention, RuntimeState runtime, ILogger<JobWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!runtime.ConfigurationValid)
        {
            logger.LogError("Worker is disabled because Cloud configuration is invalid.");
            return;
        }
        var reconcileAt = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (DateTimeOffset.UtcNow >= reconcileAt)
                {
                    reconcileAt = DateTimeOffset.UtcNow.AddMinutes(1);
                    try { await retention.SweepAsync(stoppingToken); await ReconcileAsync(stoppingToken); }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    { logger.LogError("Manifest reconciliation failed ({ErrorType}). Will retry.", ex.GetType().Name); }
                }
                var messages = (await storage.Queue.ReceiveMessagesAsync(1, TimeSpan.FromSeconds(120), stoppingToken)).Value;
                if (messages.Length == 0) await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                else await HandleAsync(messages[0], stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError("Durable worker failed ({ErrorType}); queued work remains recoverable.", ex.GetType().Name);
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }
    }

    private async Task HandleAsync(QueueMessage message, CancellationToken ct)
    {
        WorkItem? work = null;
        try { work = JsonSerializer.Deserialize<WorkItem>(message.Body, Storage.Json); }
        catch (JsonException) { }
        if (work is null || !work.IsValid)
        {
            await storage.Poison.SendMessageAsync("Invalid work-item format.", cancellationToken: ct);
            await storage.Queue.DeleteMessageAsync(message.MessageId, message.PopReceipt, ct);
            logger.LogError("Invalid queue message moved to poison.");
            return;
        }
        var blob = storage.Manifest(work);
        BlobLease? acquired;
        try { acquired = await BlobLease.TryAcquireAsync(blob, ct); }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            await storage.Poison.SendMessageAsync(JsonSerializer.Serialize(new { work, error = "Missing manifest" }), cancellationToken: ct);
            await storage.Queue.DeleteMessageAsync(message.MessageId, message.PopReceipt, ct);
            return;
        }
        await using var lease = acquired;
        if (lease is null)
        {
            await storage.Queue.UpdateMessageAsync(message.MessageId, message.PopReceipt, visibilityTimeout: TimeSpan.FromSeconds(60), cancellationToken: ct);
            return;
        }
        var checkpoint = await JobCheckpoint.LoadAsync(blob, lease);
        var job = checkpoint.Job;
        if (job.OwnerHash != work.OwnerHash || job.Id != work.Id) throw new InvalidDataException("Manifest identity mismatch.");
        if (!job.IsTerminal && job.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            job.Status = "failed";
            job.Error = "Document access expired before processing completed.";
            foreach (var page in job.Pages.Where(p => !p.IsTerminal)) { page.Status = "failed"; page.Error = job.Error; }
            await checkpoint.SaveAsync();
        }
        using var renewalStop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var workStop = CancellationTokenSource.CreateLinkedTokenSource(lease.Token);
        var receipt = message.PopReceipt;
        var renewal = RenewVisibilityAsync();
        try
        {
            if (!job.IsTerminal && job.Status != "uploading"
                && (job.Status == "quota_blocked" || job.NextAttemptAt <= DateTimeOffset.UtcNow))
            {
                try
                {
                    if (!job.SplitComplete) await splitter.SplitAsync(checkpoint, work, workStop.Token);
                    else await processor.StepAsync(checkpoint, work, workStop.Token);
                    await admission.UpdateAsync(work, job.IsTerminal ? "terminal" : "pending", job.RetainedBytes, workStop.Token);
                    job.InfrastructureFailures = 0;
                }
                catch (Exception ex) when (!workStop.IsCancellationRequested)
                {
                    job.InfrastructureFailures++;
                    job.Error = "Storage or processing dependency failure; retrying.";
                    job.NextAttemptAt = DateTimeOffset.UtcNow + ProcessingRules.RetryDelay(job.InfrastructureFailures);
                    logger.LogError("Job {JobId} dependency failure ({ErrorType}), attempt {Attempt}.",
                        work.Id, ex.GetType().Name, job.InfrastructureFailures);
                    if (job.InfrastructureFailures >= 8)
                    {
                        job.Status = "failed";
                        job.Error = "Dependency retry limit exceeded. Job moved to poison queue.";
                        foreach (var page in job.Pages.Where(p => !p.IsTerminal))
                        { page.Status = "failed"; page.Error = job.Error; }
                    }
                }
                await checkpoint.SaveAsync();
            }
        }
        finally
        {
            await renewalStop.CancelAsync();
            await renewal;
        }
        workStop.Token.ThrowIfCancellationRequested();
        if (job.IsTerminal)
        {
            await admission.UpdateAsync(work, "terminal", job.RetainedBytes, ct);
            if (job.Status == "failed")
                await storage.Poison.SendMessageAsync(JsonSerializer.Serialize(new { work, job.Error }), cancellationToken: ct);
            await storage.Queue.DeleteMessageAsync(message.MessageId, receipt, ct);
        }
        else
        {
            var delay = job.NextAttemptAt - DateTimeOffset.UtcNow;
            await storage.Queue.UpdateMessageAsync(message.MessageId, receipt,
                visibilityTimeout: delay > TimeSpan.Zero ? delay : TimeSpan.FromSeconds(1), cancellationToken: ct);
        }

        async Task RenewVisibilityAsync()
        {
            try
            {
                while (true)
                {
                    await Task.Delay(TimeSpan.FromSeconds(40), renewalStop.Token);
                    receipt = (await storage.Queue.UpdateMessageAsync(message.MessageId, receipt,
                        visibilityTimeout: TimeSpan.FromSeconds(120), cancellationToken: renewalStop.Token)).Value.PopReceipt;
                }
            }
            catch (OperationCanceledException) when (renewalStop.IsCancellationRequested) { }
            catch (Exception ex)
            {
                logger.LogError("Queue visibility renewal failed ({ErrorType}); cancelling job step.", ex.GetType().Name);
                await workStop.CancelAsync();
            }
        }
    }

    private async Task ReconcileAsync(CancellationToken ct)
    {
        // Server-only scan: no anonymous endpoint ever lists all owners.
        await foreach (var candidate in storage.ListAsync(null, ct))
        {
            if (candidate.IsTerminal || candidate.LastEnqueuedAt > DateTimeOffset.UtcNow.AddMinutes(-5)
                || (candidate.Status != "quota_blocked" && candidate.NextAttemptAt > DateTimeOffset.UtcNow)) continue;
            var work = new WorkItem(candidate.OwnerHash, candidate.Id);
            if (!work.IsValid) continue;
            await using var lease = await BlobLease.TryAcquireAsync(storage.Manifest(work), ct);
            if (lease is null) continue;
            var checkpoint = await JobCheckpoint.LoadAsync(storage.Manifest(work), lease);
            var job = checkpoint.Job;
            if (job.IsTerminal) continue;
            if (job.Status == "uploading")
            {
                if (job.CreatedAt > DateTimeOffset.UtcNow.AddMinutes(-15)) continue;
                job.Status = "failed";
                job.Error = "Upload was interrupted before all files were stored. Please upload again.";
                await checkpoint.SaveAsync();
                await admission.UpdateAsync(work, "terminal", job.RetainedBytes, ct);
                await storage.Poison.SendMessageAsync(JsonSerializer.Serialize(new { work, job.Error }), cancellationToken: ct);
                continue;
            }
            await storage.EnqueueAsync(work, TimeSpan.Zero, ct);
            job.LastEnqueuedAt = DateTimeOffset.UtcNow;
            await checkpoint.SaveAsync();
        }
    }
}
