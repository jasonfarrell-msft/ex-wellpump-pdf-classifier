using System.Diagnostics;
using Azure.Storage.Blobs;
using WellpumpClassifier.Data;
using WellpumpClassifier.Models;

namespace WellpumpClassifier;

public sealed class PdfSubprocess(Storage storage, DemoLimits limits, ILogger<PdfSubprocess> logger)
{
    public async Task SplitAsync(JobCheckpoint checkpoint, WorkItem work, CancellationToken ct)
    {
        // A storage lease bounds parser concurrency across replicas, not just this process.
        var parserLock = storage.Container.GetBlobClient("control/parser.lock");
        try { await parserLock.UploadAsync(BinaryData.FromString(""), new Azure.Storage.Blobs.Models.BlobUploadOptions
            { Conditions = new() { IfNoneMatch = Azure.ETag.All } }, ct); }
        catch (Azure.RequestFailedException ex) when (ex.Status is 409 or 412) { }
        await using var parserLease = await BlobLease.TryAcquireAsync(parserLock, ct);
        if (parserLease is null) { checkpoint.Job.NextAttemptAt = DateTimeOffset.UtcNow.AddSeconds(10); return; }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, parserLease.Token);
        var root = Path.Combine(Path.GetTempPath(), $"wellpump-worker-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var output = Path.Combine(root, "pages");
        Directory.CreateDirectory(output);
        try
        {
            CleanupStaleTemp();
            checkpoint.Job.Status = "splitting";
            await checkpoint.SaveAsync();
            var input = Path.Combine(root, "input.pdf");
            await using (var original = await storage.Container.GetBlobClient(Storage.Prefix(work) + "original.pdf")
                .OpenReadAsync(cancellationToken: linked.Token))
            await using (var destination = File.Create(input))
                await original.CopyToAsync(destination, linked.Token);

            var paths = await ExecuteAsync(input, output, UploadPolicy.DocumentIntelligenceMaxPageBytes,
                TimeSpan.FromSeconds(limits.ParserTimeoutSeconds), linked.Token);
            var total = new FileInfo(input).Length;
            checkpoint.Job.Pages = paths.Select((_, index) => new PageState { PageNumber = index + 1 }).ToList();
            for (var index = 0; index < paths.Count; index++)
            {
                var blob = storage.Page(work, index + 1);
                // Idempotent restart after an interrupted split/upload: generated page bytes may
                // have a different PDF creation timestamp, so an existing page is reused.
                if (!await blob.ExistsAsync(linked.Token))
                    await storage.UploadPdfAsync(blob, paths[index], $"page-{index + 1}.pdf", linked.Token);
                total += new FileInfo(paths[index]).Length;
                checkpoint.Job.Pages[index].BlobReady = true;
            }
            checkpoint.Job.SplitComplete = true;
            checkpoint.Job.RetainedBytes = total;
            checkpoint.Job.Status = "queued";
            checkpoint.Job.NextAttemptAt = DateTimeOffset.UtcNow;
            await checkpoint.SaveAsync();
            logger.LogInformation("Job {JobId} split into {Pages} pages ({Bytes} retained bytes).", work.Id, paths.Count, total);
        }
        catch (InvalidDataException ex)
        {
            checkpoint.Job.Status = "failed";
            checkpoint.Job.Error = ex.Message;
            foreach (var page in checkpoint.Job.Pages) { page.Status = "failed"; page.Error = ex.Message; }
            await checkpoint.SaveAsync();
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    public static async Task<List<string>> ExecuteAsync(string input, string output, long maxPageBytes, TimeSpan timeout, CancellationToken ct)
    {
        // Launch the same application, bypassing host construction and all cloud clients.
        var start = new ProcessStartInfo
        {
            FileName = "dotnet", UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = output
        };
        start.ArgumentList.Add(typeof(PdfSubprocess).Assembly.Location);
        start.ArgumentList.Add("--split-pdf");
        start.ArgumentList.Add(input);
        start.ArgumentList.Add(output);
        start.ArgumentList.Add(maxPageBytes.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var process = Process.Start(start) ?? throw new IOException("Unable to start PDF parser.");
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        try
        {
            while (!process.HasExited)
            {
                deadline.Token.ThrowIfCancellationRequested();
                var files = new DirectoryInfo(output).EnumerateFiles().ToArray();
                if (files.Any(f => f.Length > maxPageBytes))
                    throw new InvalidDataException("A page exceeds the Document Intelligence service size limit.");
                await Task.Delay(100, deadline.Token);
            }
            await process.WaitForExitAsync(ct);
            await Task.WhenAll(stdout, stderr);
            if (process.ExitCode != 0)
                throw new InvalidDataException("PDF is malformed, encrypted, exceeds the Document Intelligence per-page size limit, or is unsupported.");
            return ValidateOutputs(output, maxPageBytes);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new InvalidDataException("PDF parsing exceeded the 60-second safety timeout.");
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); }
            // Observe redirected-reader tasks even on cancellation/kill.
            try { await Task.WhenAll(stdout, stderr); } catch (OperationCanceledException) { }
        }
    }

    public static List<string> ValidateOutputs(string output, long maxPageBytes)
    {
        var files = new DirectoryInfo(output).EnumerateFiles().ToArray();
        if (files.Length < 1
            || files.Any(f => f.Length is < 5 || f.Length > maxPageBytes || f.LinkTarget is not null))
            throw new InvalidDataException("PDF parser produced invalid or oversized outputs.");
        var pages = Enumerable.Range(1, files.Length).Select(i => Path.Combine(output, $"{i}.pdf")).ToList();
        foreach (var path in pages)
        {
            if (!File.Exists(path)) throw new InvalidDataException("PDF parser page numbering was invalid.");
            using var stream = File.OpenRead(path);
            var header = new byte[5];
            _ = stream.Read(header);
            if (!header.AsSpan().SequenceEqual("%PDF-"u8)) throw new InvalidDataException("PDF parser output was invalid.");
        }
        return pages;
    }

    public static int RunChild(string[] args)
    {
        if (args.Length != 4 || !long.TryParse(args[3], out var maximum) || maximum is < 1 or > UploadPolicy.DocumentIntelligenceMaxPageBytes) return 2;
        try
        {
            var splitter = new PdfSplitter(args[2]);
            splitter.SplitFile(args[1], maximum, CancellationToken.None);
            return 0; // Parent owns and removes the isolated directory.
        }
        catch { return 2; } // No PDF content, parser exceptions or file paths reach platform logs.
    }

    public static void CleanupStaleTemp()
    {
        foreach (var directory in new DirectoryInfo(Path.GetTempPath()).EnumerateDirectories("wellpump-worker-*"))
            if (directory.LastWriteTimeUtc < DateTime.UtcNow.AddHours(-1)) directory.Delete(true);
    }
}
