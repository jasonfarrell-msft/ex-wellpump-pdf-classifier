using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Azure.Core;
using WellpumpClassifier.Data;
using WellpumpClassifier.Models;

namespace WellpumpClassifier;

public sealed class ClassifierException(string message, bool transient, int? status = null, string? errorCode = null,
    string? deniedAction = null) : Exception(message)
{
    public bool IsTransient { get; } = transient;
    public int? Status { get; } = status;
    public string? ErrorCode { get; } = errorCode is { Length: > 0 and <= 64 }
        && errorCode.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.') ? errorCode : null;
    public string? DeniedAction { get; } = ExtractDeniedAction(deniedAction);

    internal static string? ExtractDeniedAction(string? message)
    {
        if (message is null) return null;
        var match = Regex.Match(message, @"Microsoft\.CognitiveServices/[A-Za-z0-9_/:.\-]+",
            RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
        // Never truncate an oversized identifier into a seemingly valid permission.
        return match.Success && match.Length <= 256 ? match.Value : null;
    }
}

public sealed record ClassificationPoll(bool Completed, string? Classification = null, double? Confidence = null);

public interface IClassifier
{
    Task<string> SubmitAsync(Stream page, CancellationToken ct);
    Task<ClassificationPoll> PollAsync(string operationId, CancellationToken ct);
}

public sealed class Classifier(CloudSettings settings, TokenCredential credential, HttpClient http,
    TokenDiagnostics? tokenDiagnostics = null) : IClassifier
{
    private string ClassifierPath => $"{settings.DocumentIntelligenceEndpoint.TrimEnd('/')}/documentintelligence/documentClassifiers/{Uri.EscapeDataString(settings.ClassifierId)}";
    private const string ApiVersion = "api-version=2024-11-30";
    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using (request)
        {
            var token = await credential.GetTokenAsync(new TokenRequestContext(["https://cognitiveservices.azure.com/.default"]), ct);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
            var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                using (response)
                {
                    var status = (int)response.StatusCode;
                    if (response.StatusCode == HttpStatusCode.Unauthorized && credential is Azure.Identity.ManagedIdentityCredential)
                        tokenDiagnostics?.ObserveUnauthorized(token, response.Headers);
                    var diagnostic = await ReadErrorDiagnosticAsync(response.Content, ct);
                    // Only validated code/action identifiers cross this boundary; never retain
                    // the body, original message, identity, inner error, URLs or headers.
                    throw new ClassifierException($"Document Intelligence returned HTTP {status}.",
                        ProcessingRules.IsTransient(status), status, diagnostic.Code, diagnostic.DeniedAction);
                }
            }
            return response;
        }
    }

    private static async Task<(string? Code, string? DeniedAction)> ReadErrorDiagnosticAsync(HttpContent content, CancellationToken ct)
    {
        const int maximumBytes = 4096;
        try
        {
            if (content.Headers.ContentLength > maximumBytes) return (null, null);
            await using var stream = await content.ReadAsStreamAsync(ct);
            var bytes = new byte[maximumBytes + 1];
            var length = 0;
            while (length < bytes.Length)
            {
                var read = await stream.ReadAsync(bytes.AsMemory(length), ct);
                if (read == 0) break;
                length += read;
            }
            if (length > maximumBytes) return (null, null);
            using var json = JsonDocument.Parse(bytes.AsMemory(0, length), new JsonDocumentOptions { MaxDepth = 16 });
            if (!json.RootElement.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object)
                return (null, null);
            var value = error.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String ? code.GetString() : null;
            if (value is not { Length: > 0 and <= 64 }
                || !value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.'))
                value = null;
            var action = error.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String
                ? ClassifierException.ExtractDeniedAction(message.GetString()) : null;
            return (value, action);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or IOException or HttpRequestException)
        {
            // Failed diagnostic extraction must not replace the original HTTP failure.
            return (null, null);
        }
    }

    public async Task<string> SubmitAsync(Stream page, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"{ClassifierPath}:analyze?{ApiVersion}&split=none");
        request.Content = new StreamContent(page);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var response = await SendAsync(request, ct);
        if (response.StatusCode != HttpStatusCode.Accepted
            || !response.Headers.TryGetValues("Operation-Location", out var locations)
            || !Uri.TryCreate(locations.SingleOrDefault(), UriKind.Absolute, out var operation)
            || operation.GetLeftPart(UriPartial.Authority) != new Uri(settings.DocumentIntelligenceEndpoint).GetLeftPart(UriPartial.Authority)
            || !operation.AbsolutePath.StartsWith(new Uri(ClassifierPath).AbsolutePath + "/analyzeResults/", StringComparison.Ordinal)
            || !Guid.TryParse(operation.Segments.Last(), out var id))
            throw new ClassifierException("Classification submission returned no valid operation ID; its outcome is uncertain.", true);
        return id.ToString();
    }

    public async Task<ClassificationPoll> PollAsync(string operationId, CancellationToken ct)
    {
        if (!Guid.TryParse(operationId, out var id)) throw new ClassifierException("Invalid stored operation ID.", false);
        using var response = await SendAsync(new(HttpMethod.Get, $"{ClassifierPath}/analyzeResults/{id}?{ApiVersion}"), ct);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
        return ParsePoll(json.RootElement);
    }

    public static ClassificationPoll ParsePoll(JsonElement root)
    {
        try
        {
            var status = root.GetProperty("status").GetString();
            if (status is "running" or "notStarted") return new(false);
            if (status != "succeeded") throw new ClassifierException("Document Intelligence operation failed or returned an unknown status.", false);
            var documents = root.GetProperty("analyzeResult").GetProperty("documents");
            if (documents.GetArrayLength() != 1)
                throw new ClassifierException("Expected exactly one classification for the single-page PDF.", false);
            var result = documents[0];
            var type = result.GetProperty("docType").GetString();
            var confidence = result.GetProperty("confidence").GetDouble();
            if (type is null || !ProcessingRules.Classes.Contains(type) || !double.IsFinite(confidence) || confidence is < 0 or > 1)
                throw new ClassifierException("Classification contained an invalid class or confidence.", false);
            return new(true, type, confidence);
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new ClassifierException("Document Intelligence returned an invalid classification response.", false);
        }
    }

    public async Task CheckAsync(CancellationToken ct)
    {
        using var response = await SendAsync(new(HttpMethod.Get, $"{ClassifierPath}?{ApiVersion}"), ct);
    }
}

public interface IPageSource
{
    Task<Stream> OpenAsync(WorkItem work, int page, CancellationToken ct);
}

public sealed class BlobPageSource(Storage storage) : IPageSource
{
    public async Task<Stream> OpenAsync(WorkItem work, int page, CancellationToken ct) =>
        await storage.Page(work, page).OpenReadAsync(cancellationToken: ct);
}

public sealed class PageProcessor(IClassifier classifier, IPageSource source, TimeProvider clock)
{
    public async Task StepAsync(IJobCheckpoint checkpoint, WorkItem work, CancellationToken ct)
    {
        var job = checkpoint.Job;
        var page = ProcessingRules.NextPage(job);
        if (page is null)
        {
            job.Status = job.Pages.Any(p => p.Status == "failed") ? "completed_with_errors" : "completed";
            await checkpoint.SaveAsync();
            return;
        }
        job.Status = "processing";
        job.Error = null;
        try
        {
            if (page.OperationId is not null)
            {
                if (page.OperationStartedAt is null || clock.GetUtcNow() - page.OperationStartedAt > TimeSpan.FromHours(24))
                    throw new ClassifierException("Saved classification operation expired; not resubmitted.", false);
                var result = await classifier.PollAsync(page.OperationId, ct);
                page.PollFailures = 0;
                page.Status = result.Completed ? "completed" : "classifying";
                page.Classification = result.Classification;
                page.Confidence = result.Confidence;
                page.Error = null;
            }
            else
            {
                if (page.Submissions >= 5)
                    throw new ClassifierException("Submission retry limit reached; an earlier uncertain submission may have incurred cost.", false);
                page.Submissions++;
                page.Status = "submitting";
                page.Error = null;
                // Persist the attempt before sending so recovery cannot bypass the retry bound.
                await checkpoint.SaveAsync();
                await using var stream = await source.OpenAsync(work, page.PageNumber, ct);
                page.OperationId = await classifier.SubmitAsync(stream, ct);
                page.OperationStartedAt = clock.GetUtcNow();
                page.Status = "classifying";
            }
            job.NextAttemptAt = clock.GetUtcNow().AddSeconds(page.IsTerminal ? 0 : 3);
        }
        catch (Exception ex) when (ex is ClassifierException or HttpRequestException or JsonException
            || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            var transient = ex is not ClassifierException ce || ce.IsTransient;
            var attempts = page.OperationId is null ? page.Submissions : ++page.PollFailures;
            page.Error = ex is ClassifierException ? ex.Message : "Classification transport or response failure.";
            page.Status = transient && attempts < (page.OperationId is null ? 5 : 6) ? "retrying" : "failed";
            job.NextAttemptAt = clock.GetUtcNow() + ProcessingRules.RetryDelay(attempts);
        }
        await checkpoint.SaveAsync();
    }
}
