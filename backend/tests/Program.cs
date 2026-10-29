using System.Net;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Azure.Storage.Sas;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using WellpumpClassifier;
using WellpumpClassifier.Data;
using WellpumpClassifier.Models;

var checks = 0;
void Assert(bool condition, string message)
{
    checks++;
    if (!condition) throw new InvalidOperationException(message);
}
async Task Reject(Func<Task> action, string message)
{
    try { await action(); }
    catch (InvalidDataException) { checks++; return; }
    throw new InvalidOperationException(message);
}

Assert(UploadPolicy.ValidateMetadata("well.pdf", 1) is null, "Small PDF metadata.");
Assert(UploadPolicy.ValidateMetadata("WELL.PDF", 26L * 1024 * 1024) is null, "Files over the former 25 MiB limit are accepted.");
Assert(UploadPolicy.ValidateMetadata("well.pdf", long.MaxValue) is null, "No application file-size cap.");
Assert(UploadPolicy.ValidateMetadata("well.pdf", 0) is not null, "Reject empty metadata.");
Assert(UploadPolicy.ValidateMetadata("well.txt", 5) is not null, "Reject non-PDF extension.");
Assert(PdfSplitter.SafeName("../../evil\r\n\"name.pdf") == "evilname.pdf", "Safe filename strips traversal/header injection.");
Assert(ProcessingRules.RetryDelay(30) == TimeSpan.FromMinutes(5), "Bound exponential retry.");
Assert(ProcessingRules.RetryDelay(1) < ProcessingRules.RetryDelay(2), "Exponential delay grows.");
Assert(ProcessingRules.IsTransient(429) && ProcessingRules.IsTransient(503) && !ProcessingRules.IsTransient(400), "Transient status classification.");
var now = new DateTimeOffset(2026, 10, 28, 23, 59, 59, TimeSpan.Zero);

Assert(Capability.SameOrigin("https://demo.example", "https://demo.example"), "Same origin.");
Assert(!Capability.SameOrigin("https://demo.example.attacker.test", "https://demo.example"), "Reject suffix attack.");
Assert(!Capability.SameOrigin("http://demo.example", "https://demo.example"), "Reject insecure origin.");
Assert(!Capability.SameOrigin(null, "https://demo.example"), "Reject missing origin.");
Assert(!Capability.SameOrigin("null", "https://demo.example"), "Reject opaque origin.");
var owner = Capability.Hash("one");
var other = Capability.Hash("two");
Assert(owner.Length == 64 && owner != other, "Only a SHA256 owner hash is stored.");
var context = new DefaultHttpContext();
var granted = Capability.Grant(context, false);
var cookie = context.Response.Headers.SetCookie.ToString();
Assert(cookie.Contains("secure", StringComparison.OrdinalIgnoreCase) && cookie.Contains("httponly", StringComparison.OrdinalIgnoreCase)
    && cookie.Contains("samesite=strict", StringComparison.OrdinalIgnoreCase) && !cookie.Contains("domain=", StringComparison.OrdinalIgnoreCase), "Secure host-only HttpOnly Strict cookie.");
context.Request.Headers.Cookie = cookie.Split(';')[0];
Assert(Capability.Owner(context) == granted, "Capability cookie round-trips as hash.");
var authorizedJob = new Job { OwnerHash = owner, ExpiresAt = now.AddHours(1) };
Assert(Capability.Owns(authorizedJob, owner, authorizedJob.Id, now), "Owner authorized.");
Assert(!Capability.Owns(authorizedJob, other, authorizedJob.Id, now), "Different browser denied even with known job ID.");
Assert(!Capability.Owns(authorizedJob, null, authorizedJob.Id, now), "Missing capability denied.");
Assert(!Capability.Owns(authorizedJob, owner, authorizedJob.Id, now.AddHours(2)), "Expired job denied.");
Assert(!new WorkItem(owner, "../escape").IsValid, "Invalid blob namespace rejected.");

var sas = Storage.PageSas("uploads", $"owners/{owner}/jobs/{authorizedJob.Id}/pages/1.pdf", now);
Assert(sas.Permissions == "r" && sas.Protocol == SasProtocol.Https && sas.Resource == "b", "Read-only HTTPS exact-blob SAS.");
Assert(sas.ExpiresOn == now.AddMinutes(5), "Five-minute SAS expiration.");
Assert(!JsonSerializer.Serialize(authorizedJob).Contains("sig=", StringComparison.Ordinal), "No SAS stored in manifest.");

var limits = new DemoLimits();
limits.Validate();
Assert(new AdmissionEntry().ReservedBytes == 0, "No bounded storage reservation is required.");
Assert(!typeof(PageProcessor).Assembly.GetTypes().Any(type => type.Name is
    "DailyQuota" or "BlobQuotaStore" or "IQuotaStore" or "CounterSnapshot" or "QuotaReservation"),
    "The backend assembly contains no aggregate submission-counter implementation.");

var clock = new TestClock(now);
var work = new WorkItem(owner, authorizedJob.Id);
var classifier = new FakeClassifier();
var pages = new FakePages();
var processor = new PageProcessor(classifier, pages, clock);
var checkpoint = new FakeCheckpoint(new Job { OwnerHash = owner, Pages = [new() { PageNumber = 1 }] });
await processor.StepAsync(checkpoint, work, default);
Assert(classifier.Submissions == 1 && checkpoint.Job.Pages[0].OperationId is not null, "Submit and checkpoint operation ID.");
Assert(checkpoint.Snapshots.Any(j => j.Pages[0].Status == "submitting" && j.Pages[0].OperationId is null && j.Pages[0].Submissions == 1),
    "Uncertain submission checkpoint exists before POST.");
// Simulate process restart by loading only persisted manifest state into a fresh processor.
checkpoint = new FakeCheckpoint(checkpoint.Snapshots.Last());
processor = new PageProcessor(classifier, pages, clock);
await processor.StepAsync(checkpoint, work, default);
Assert(classifier.Submissions == 1, "Recovery polls saved operation without resubmission.");
Assert(checkpoint.Job.Pages[0].Classification == "notes" && checkpoint.Job.Pages[0].Confidence == .9, "Real classifier output persisted.");
await processor.StepAsync(checkpoint, work, default);
Assert(checkpoint.Job.Status == "completed", "Job completes from saved page checkpoints.");
var previouslyBlocked = new FakeCheckpoint(new Job { Status = "quota_blocked", Pages = [new() { PageNumber = 1, Status = "quota_blocked" }] });
await processor.StepAsync(previouslyBlocked, work, default);
Assert(previouslyBlocked.Job.Status == "processing" && previouslyBlocked.Job.Pages[0].Status == "classifying",
    "Previously quota-blocked pages can resume without a usage counter.");

var uncertainClassifier = new FakeClassifier { FailSubmission = true };
var uncertainProcessor = new PageProcessor(uncertainClassifier, pages, clock);
var uncertain = new FakeCheckpoint(new Job { Pages = [new() { PageNumber = 1 }] });
await uncertainProcessor.StepAsync(uncertain, work, default);
await uncertainProcessor.StepAsync(uncertain, work, default);
Assert(uncertain.Job.Pages[0].Submissions == 2, "Uncertain retries retain only the internal retry budget.");
for (var index = 0; index < 4; index++) await uncertainProcessor.StepAsync(uncertain, work, default);
Assert(uncertain.Job.Pages[0].Status == "failed" && uncertainClassifier.Submissions == 5, "Submission retries bounded at five.");

using (var json = JsonDocument.Parse("""{"status":"succeeded","analyzeResult":{"documents":[]}}"""))
{
    try { Classifier.ParsePoll(json.RootElement); throw new Exception("Empty response must fail."); }
    catch (ClassifierException ex) { Assert(!ex.IsTransient, "Empty classifications never fabricated."); }
}
using (var json = JsonDocument.Parse("""{"status":"succeeded","analyzeResult":{"documents":[{"docType":"invented","confidence":0.8}]}}"""))
{
    try { Classifier.ParsePoll(json.RootElement); throw new Exception("Unknown class must fail."); }
    catch (ClassifierException) { checks++; }
}
using (var json = JsonDocument.Parse("""{"status":"succeeded","analyzeResult":{"documents":[{"docType":"notes","confidence":1.1}]}}"""))
{
    try { Classifier.ParsePoll(json.RootElement); throw new Exception("Invalid confidence must fail."); }
    catch (ClassifierException) { checks++; }
}
using (var http = new HttpClient(new BinaryHandler()))
{
    var client = new Classifier(new CloudSettings(), new FakeCredential(), http);
    await using var binary = new MemoryStream("%PDF-single-page"u8.ToArray());
    Assert(Guid.TryParse(await client.SubmitAsync(binary, default), out _), "Binary bearer-authenticated classify request, explicit split=none.");
}

var readinessLogger = new CapturingLogger();
Assert(await Readiness.CheckAsync("uploads_container", _ => Task.CompletedTask, readinessLogger, default)
    && readinessLogger.Entries.Count == 0, "Healthy readiness dependency does not emit a failure.");
var sensitiveReason = "DO_NOT_LOG_RESPONSE https://service.example/?sig=SECRET Authorization: Bearer SECRET";
var failedDependencies = await Task.WhenAll(
    Readiness.CheckAsync("jobs_queue", _ => Task.FromException(
        new Azure.RequestFailedException(403, sensitiveReason, "AuthorizationPermissionMismatch", new Exception(sensitiveReason))), readinessLogger, default),
    Readiness.CheckAsync("poison_queue", _ => Task.FromException(
        new Azure.RequestFailedException(404, sensitiveReason, "QueueNotFound", null)), readinessLogger, default),
    Readiness.CheckAsync("blob_delegation", _ => Task.FromException(
        new OperationCanceledException(sensitiveReason)), readinessLogger, default));
Assert(failedDependencies.All(ready => !ready) && readinessLogger.Entries.Count == 3,
    "Concurrent readiness failures are each named and logged, not just the first exception.");
var queueFailure = readinessLogger.Entries.Single(entry => Equals(entry.Fields["Dependency"], "jobs_queue"));
Assert(Equals(queueFailure.Fields["Status"], 403) && Equals(queueFailure.Fields["ErrorCode"], "AuthorizationPermissionMismatch")
    && Equals(queueFailure.Fields["ErrorType"], "RequestFailedException"), "Storage readiness logs safe structured status/code/type.");
var poisonFailure = readinessLogger.Entries.Single(entry => Equals(entry.Fields["Dependency"], "poison_queue"));
Assert(Equals(poisonFailure.Fields["Status"], 404) && Equals(poisonFailure.Fields["ErrorCode"], "QueueNotFound"),
    "Poison queue failure is distinguishable from jobs queue.");
var timeoutFailure = readinessLogger.Entries.Single(entry => Equals(entry.Fields["Dependency"], "blob_delegation"));
Assert(timeoutFailure.Fields["Status"] is null && timeoutFailure.Fields["ErrorCode"] is null
    && Equals(timeoutFailure.Fields["ErrorType"], "OperationCanceledException"), "Readiness timeout logs no invented status.");
using (var http = new HttpClient(new FailureHandler(sensitiveReason)))
{
    var client = new Classifier(new CloudSettings(), new FakeCredential(), http);
    Assert(!await Readiness.CheckAsync("classifier_metadata", client.CheckAsync, readinessLogger, default),
        "Failed DI metadata readiness remains unhealthy.");
}
var classifierFailure = readinessLogger.Entries.Single(entry => Equals(entry.Fields["Dependency"], "classifier_metadata"));
Assert(Equals(classifierFailure.Fields["Status"], 403) && classifierFailure.Fields["ErrorCode"] is null
    && Equals(classifierFailure.Fields["ErrorType"], "ClassifierException"), "DI status survives safe exception wrapping without exposing response body.");
await Readiness.CheckAsync("uploads_container", _ => Task.FromException(
    new Azure.RequestFailedException(403, sensitiveReason, sensitiveReason, null)), readinessLogger, default);
Assert(readinessLogger.Entries.Last().Fields["ErrorCode"] is null, "Non-identifier error codes are redacted.");
Assert(readinessLogger.Entries.All(entry => entry.Exception is null
    && !entry.Message.Contains("SECRET") && !entry.Message.Contains("https://")
    && !entry.Message.Contains("DO_NOT_LOG_RESPONSE")
    && entry.Level == LogLevel.Warning && entry.EventId.Id == 4101),
    "Readiness logs never carry exception objects, messages, URLs, response content, tokens or SAS.");

string ErrorBody(object? code) => JsonSerializer.Serialize(new
{
    error = new
    {
        code,
        message = sensitiveReason,
        innererror = new { code = "INNER_ONLY_SENTINEL", message = sensitiveReason }
    }
});
foreach (var (body, expectedCode) in new (string Body, string? Code)[]
{
    (ErrorBody("PermissionDenied"), "PermissionDenied"),
    (ErrorBody("InvalidAuthenticationToken"), "InvalidAuthenticationToken"),
    (ErrorBody("401"), "401"),
    (ErrorBody("Auth.Error_Code-1"), "Auth.Error_Code-1"),
    (ErrorBody(new string('A', 64)), new string('A', 64)),
    (ErrorBody(new string('A', 65)), null),
    (ErrorBody(""), null),
    (ErrorBody(null), null),
    (ErrorBody(401), null),
    (ErrorBody("PermissionDenied\nINJECTED"), null),
    (ErrorBody("PérmissionDenied"), null),
    (ErrorBody("https://service.example/?sig=SECRET"), null),
    (ErrorBody("Authorization: Bearer SECRET"), null),
    ("""{"error":{"message":"INNER_ONLY_SENTINEL","innererror":{"code":"PermissionDenied"}}}""", null),
    ("""{"code":"PermissionDenied","error":{"message":"DO_NOT_LOG_RESPONSE"}}""", null),
    ("not-json " + sensitiveReason, null),
    (ErrorBody("PermissionDenied") + new string(' ', 4096), null)
})
{
    var codeLogger = new CapturingLogger();
    using var http = new HttpClient(new FailureHandler(body, HttpStatusCode.Unauthorized));
    var client = new Classifier(new CloudSettings(), new FakeCredential(), http);
    var ready = await Readiness.CheckAsync("classifier_metadata", client.CheckAsync, codeLogger, default);
    var entry = codeLogger.Entries.Single();
    Assert(!ready && Equals(entry.Fields["Status"], 401) && Equals(entry.Fields["ErrorCode"], expectedCode)
        && entry.Fields["DeniedAction"] is null
        && Equals(entry.Fields["ErrorType"], "ClassifierException") && entry.Exception is null
        && !entry.Message.Contains("SECRET") && !entry.Message.Contains("https://")
        && !entry.Message.Contains("INNER_ONLY_SENTINEL") && !entry.Message.Contains("DO_NOT_LOG_RESPONSE"),
        "DI diagnostics allow only bounded ASCII error.code, never message/body/inner errors/headers.");
}
using (var http = new HttpClient(new FailureHandler(ErrorBody("PermissionDenied"), HttpStatusCode.Unauthorized)))
{
    var client = new Classifier(new CloudSettings(), new FakeCredential(), http);
    try { await client.CheckAsync(default); throw new Exception("HTTP 401 must fail."); }
    catch (ClassifierException ex)
    {
        Assert(ex.Status == 401 && ex.ErrorCode == "PermissionDenied" && !ex.Message.Contains("DO_NOT_LOG_RESPONSE"),
            "ClassifierException exposes only safe HTTP status and code, not DI message.");
    }
}

const string deniedAction = "Microsoft.CognitiveServices/accounts/FormRecognizer/documentclassifiers/read";
const string secondAction = "Microsoft.CognitiveServices/accounts/FormRecognizer/documentclassifiers:analyze/action";
const string actionPrefix = "Microsoft.CognitiveServices/";
var maximumAction = actionPrefix + new string('A', 256 - actionPrefix.Length);
foreach (var (message, expectedAction) in new (object? Message, string? Action)[]
{
    ($"Principal 'PRINCIPAL_SENTINEL' token JWT_SECRET_SENTINEL lacks action '{deniedAction}' for identity IDENTITY_SENTINEL.", deniedAction),
    ($"First '{deniedAction}', then '{secondAction}', PRINCIPAL_SENTINEL.", deniedAction),
    ("Principal PRINCIPAL_SENTINEL token JWT_SECRET_SENTINEL was denied.", null),
    (actionPrefix, null),
    ("microsoft.CognitiveServices/accounts/read", null),
    (maximumAction, maximumAction),
    (maximumAction + "A", null),
    (null, null),
    (401, null)
})
{
    var body = JsonSerializer.Serialize(new
    {
        error = new
        {
            code = "PermissionDenied", message,
            innererror = new { message = $"INNER_ONLY_SENTINEL {secondAction}" }
        },
        principal = "PRINCIPAL_SENTINEL", token = "JWT_SECRET_SENTINEL"
    });
    var actionLogger = new CapturingLogger();
    using var http = new HttpClient(new FailureHandler(body, HttpStatusCode.Unauthorized));
    var client = new Classifier(new CloudSettings(), new FakeCredential(), http);
    await Readiness.CheckAsync("classifier_metadata", client.CheckAsync, actionLogger, default);
    var entry = actionLogger.Entries.Single();
    Assert(Equals(entry.Fields["DeniedAction"], expectedAction) && Equals(entry.Fields["ErrorCode"], "PermissionDenied")
        && Equals(entry.Fields["Status"], 401) && entry.Exception is null
        && !entry.Message.Contains("PRINCIPAL_SENTINEL") && !entry.Message.Contains("JWT_SECRET_SENTINEL")
        && !entry.Message.Contains("IDENTITY_SENTINEL") && !entry.Message.Contains("INNER_ONLY_SENTINEL"),
        "Only first bounded Cognitive Services action survives; principal, token, identity and full message are discarded.");
}
using (var http = new HttpClient(new FailureHandler(JsonSerializer.Serialize(new
{
    error = new { code = "PermissionDenied", message = $"PRINCIPAL_SENTINEL lacks '{deniedAction}' JWT_SECRET_SENTINEL." }
}), HttpStatusCode.Unauthorized)))
{
    var client = new Classifier(new CloudSettings(), new FakeCredential(), http);
    try { await client.CheckAsync(default); throw new Exception("HTTP 401 must fail."); }
    catch (ClassifierException ex)
    {
        Assert(ex.DeniedAction == deniedAction && !ex.Message.Contains("PRINCIPAL_SENTINEL")
            && !ex.Message.Contains("JWT_SECRET_SENTINEL"), "ClassifierException retains only the denied action identifier.");
    }
}

// Diagnostic-only JWT fixtures are never validated or sent to Azure.
var expectedPrincipal = Guid.NewGuid();
var expectedTenant = Guid.NewGuid();
var expectedRequestId = Guid.NewGuid();
string DiagnosticJwt(object claims) =>
    $"testheader.{WebEncoders.Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(claims))}.testsignature";
var diagnosticJwt = DiagnosticJwt(new
{
    oid = expectedPrincipal, tid = expectedTenant, aud = "https://cognitiveservices.azure.com",
    ignored = "RAW_CLAIM_SECRET_SENTINEL"
});
var diagnosticClock = new TestClock(now);
var managedCredential = new TestManagedIdentityCredential(diagnosticJwt);
IdentityDiagnosticsSettings DiagnosticSettings() => new()
{
    ExpectedPrincipalId = expectedPrincipal.ToString(), ExpectedTenantId = expectedTenant.ToString()
};
using (var http = new HttpClient(new IdentityDiagnosticHandler(diagnosticJwt, HttpStatusCode.Unauthorized, expectedRequestId.ToString())))
{
    var logger = new CapturingLogger();
    var diagnostic = new TokenDiagnostics(managedCredential, new TestEnvironment("Production"),
        DiagnosticSettings(), logger, diagnosticClock);
    var client = new Classifier(new CloudSettings(), managedCredential, http, diagnostic);
    for (var index = 0; index < 2; index++)
    {
        try { await client.CheckAsync(default); throw new Exception("HTTP 401 must fail."); }
        catch (ClassifierException ex) { Assert(ex.Status == 401, "Identity diagnostics do not alter original DI 401."); }
    }
    var entry = logger.Entries.Single();
    Assert(entry.EventId.Id == 4102 && entry.EventId.Name == "DocumentIntelligenceIdentityDiagnostic"
        && Equals(entry.Fields["OidPresentAndValid"], true) && Equals(entry.Fields["OidMatchesExpected"], true)
        && Equals(entry.Fields["TenantMatchesExpected"], true)
        && Equals(entry.Fields["Audience"], DiagnosticAudience.ExpectedCognitiveServices)
        && Equals(entry.Fields["ServiceRequestId"], expectedRequestId) && Equals(entry.Fields["UtcTime"], now),
        "Once-only MI diagnostic examines the exact bearer token attached to the rejected request.");
    var allowedFields = new HashSet<string>
    {
        "OidPresentAndValid", "OidMatchesExpected", "TenantMatchesExpected", "Audience",
        "UtcTime", "ServiceRequestId", "{OriginalFormat}"
    };
    Assert(entry.Exception is null && entry.Fields.Keys.All(allowedFields.Contains)
        && !entry.Message.Contains(expectedPrincipal.ToString()) && !entry.Message.Contains(expectedTenant.ToString())
        && !entry.Message.Contains(diagnosticJwt) && !entry.Message.Contains("RAW_CLAIM_SECRET_SENTINEL"),
        "Identity diagnostic exposes only allowlisted booleans/enum/time/request GUID; never raw IDs, token or claims.");
}
foreach (var status in new[] { HttpStatusCode.OK, HttpStatusCode.Forbidden })
{
    var logger = new CapturingLogger();
    using var http = new HttpClient(new IdentityDiagnosticHandler(diagnosticJwt, status, expectedRequestId.ToString()));
    var diagnostic = new TokenDiagnostics(managedCredential, new TestEnvironment("Production"),
        DiagnosticSettings(), logger, diagnosticClock);
    var client = new Classifier(new CloudSettings(), managedCredential, http, diagnostic);
    try { await client.CheckAsync(default); } catch (ClassifierException) { }
    Assert(logger.Entries.Count == 0, "Identity diagnostics run only on DI 401, not success or HTTP 403.");
}
foreach (var (credential, environment, options) in new (TokenCredential, string, IdentityDiagnosticsSettings)[]
{
    (new FakeCredential(), "Production", DiagnosticSettings()),
    (new AzureCliCredential(), "Production", DiagnosticSettings()),
    (managedCredential, "Development", DiagnosticSettings()),
    (managedCredential, "Production", new()),
    (managedCredential, "Production", new() { ExpectedPrincipalId = "INVALID_EXPECTED_ID_SENTINEL" })
})
{
    var logger = new CapturingLogger();
    var diagnostic = new TokenDiagnostics(credential, new TestEnvironment(environment), options, logger, diagnosticClock);
    using var response = new HttpResponseMessage();
    diagnostic.ObserveUnauthorized(new AccessToken(diagnosticJwt, now.AddHours(1)), response.Headers);
    Assert(logger.Entries.Count == 0, "Diagnostic gate rejects CLI/fake credentials, Development, absent or invalid expected principal.");
}
foreach (var (jwt, validOid, matchesOid, matchesTenant, audience) in
    new (string, bool, bool, bool, DiagnosticAudience)[]
{
    (DiagnosticJwt(new { oid = expectedPrincipal, tid = expectedTenant, aud = "https://cognitiveservices.azure.com/" }),
        true, true, true, DiagnosticAudience.ExpectedCognitiveServices),
    (DiagnosticJwt(new { oid = expectedPrincipal, tid = expectedTenant, aud = "AUDIENCE_SECRET_SENTINEL" }),
        true, true, true, DiagnosticAudience.Unexpected),
    (DiagnosticJwt(new { oid = Guid.NewGuid(), tid = Guid.NewGuid(), aud = "https://cognitiveservices.azure.com" }),
        true, false, false, DiagnosticAudience.ExpectedCognitiveServices),
    (DiagnosticJwt(new { oid = "OID_SECRET_SENTINEL", tid = "TENANT_SECRET_SENTINEL", aud = new[] { "https://cognitiveservices.azure.com" } }),
        false, false, false, DiagnosticAudience.MissingOrMalformed),
    (DiagnosticJwt(new { ignored = "RAW_CLAIM_SECRET_SENTINEL" }),
        false, false, false, DiagnosticAudience.MissingOrMalformed),
    ("NOT_A_JWT_SECRET_SENTINEL", false, false, false, DiagnosticAudience.MissingOrMalformed),
    ("header.***.signature", false, false, false, DiagnosticAudience.MissingOrMalformed),
    (new string('A', 32769), false, false, false, DiagnosticAudience.MissingOrMalformed),
    ($"header.{new string('A', 16385)}.signature", false, false, false, DiagnosticAudience.MissingOrMalformed)
})
{
    var logger = new CapturingLogger();
    var diagnostic = new TokenDiagnostics(managedCredential, new TestEnvironment("Production"),
        DiagnosticSettings(), logger, diagnosticClock);
    using var response = new HttpResponseMessage();
    diagnostic.ObserveUnauthorized(new AccessToken(jwt, now.AddHours(1)), response.Headers);
    var entry = logger.Entries.Single();
    Assert(Equals(entry.Fields["OidPresentAndValid"], validOid) && Equals(entry.Fields["OidMatchesExpected"], matchesOid)
        && Equals(entry.Fields["TenantMatchesExpected"], matchesTenant) && Equals(entry.Fields["Audience"], audience)
        && entry.Exception is null && !entry.Message.Contains("SECRET_SENTINEL") && !entry.Message.Contains(jwt),
        "Bounded diagnostic JWT inspection logs only comparison results/audience enum, including malformed inputs.");
}
{
    var logger = new CapturingLogger();
    var options = DiagnosticSettings();
    options.ExpectedTenantId = null;
    var diagnostic = new TokenDiagnostics(managedCredential, new TestEnvironment("Production"), options, logger, diagnosticClock);
    using var response = new HttpResponseMessage();
    response.Headers.TryAddWithoutValidation("apim-request-id", "INVALID_REQUEST_ID_SECRET_SENTINEL");
    response.Headers.TryAddWithoutValidation("x-ms-request-id", expectedRequestId.ToString());
    await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() =>
        diagnostic.ObserveUnauthorized(new AccessToken(diagnosticJwt, now.AddHours(1)), response.Headers))));
    var entry = logger.Entries.Single();
    Assert(Equals(entry.Fields["TenantMatchesExpected"], false) && Equals(entry.Fields["ServiceRequestId"], expectedRequestId)
        && !entry.Message.Contains("INVALID_REQUEST_ID_SECRET_SENTINEL"),
        "Interlocked guard is one-shot under concurrency; optional tenant is false and request ID safely falls back.");
}
{
    var logger = new CapturingLogger();
    var diagnostic = new TokenDiagnostics(managedCredential, new TestEnvironment("Production"),
        DiagnosticSettings(), logger, diagnosticClock);
    using var response = new HttpResponseMessage();
    response.Headers.TryAddWithoutValidation("apim-request-id", "https://service.example/?sig=REQUEST_SECRET_SENTINEL");
    response.Headers.TryAddWithoutValidation("x-ms-request-id", new[] { expectedRequestId.ToString(), "REQUEST_SECRET_SENTINEL" });
    diagnostic.ObserveUnauthorized(new AccessToken(diagnosticJwt, now.AddHours(1)), response.Headers);
    var entry = logger.Entries.Single();
    Assert(entry.Fields["ServiceRequestId"] is null && !entry.Message.Contains("REQUEST_SECRET_SENTINEL"),
        "Invalid or multi-valued correlation headers are omitted, never logged verbatim.");
}

{
    var beforeSend = new FakeCheckpoint(new Job { Pages = [new() { PageNumber = 1 }] });
    var handler = new BinaryHandler
    {
        OnSend = () => Assert(beforeSend.Snapshots.Any(j => j.Pages[0].Status == "submitting"
            && j.Pages[0].OperationId is null && j.Pages[0].Submissions == 1),
            "The internal retry attempt is checkpointed before actual transport dispatch.")
    };
    using var http = new HttpClient(handler);
    var client = new Classifier(new CloudSettings(), new FakeCredential(), http);
    await new PageProcessor(client, pages, clock).StepAsync(beforeSend, work, default);
    Assert(handler.Requests == 1 && beforeSend.Job.Pages[0].OperationId is not null,
        "Classification dispatch succeeds with no usage-counter dependency.");
}

using (var pdf = new PdfSplitter())
{
    await Reject(() => pdf.ReadAsync(new MemoryStream("notpdf"u8.ToArray()), default), "Reject non-PDF bytes.");
}
using (var pdf = new PdfSplitter())
{
    await Reject(() => pdf.ReadAsync(new MemoryStream(), default), "Reject empty actual input.");
}
using (var pdf = new PdfSplitter())
{
    await Reject(() => pdf.ReadAsync(new MemoryStream("%PDF-broken"u8.ToArray()), default), "Reject malformed actual PDF.");
}
var temporary = Path.Combine(Path.GetTempPath(), $"wellpump-tests-{Guid.NewGuid():N}");
Directory.CreateDirectory(temporary);
try
{
    string MakePdf(string name, int count, bool encrypted = false)
    {
        var path = Path.Combine(temporary, name);
        using var document = new PdfDocument();
        for (var index = 0; index < count; index++) document.AddPage();
        if (encrypted) document.SecuritySettings.OwnerPassword = "offline-test-only";
        document.Save(path);
        return path;
    }
    var small = MakePdf("small.pdf", 3);
    var outputs = Path.Combine(temporary, "pages");
    Directory.CreateDirectory(outputs);
    var split = await PdfSubprocess.ExecuteAsync(small, outputs, UploadPolicy.DocumentIntelligenceMaxPageBytes, TimeSpan.FromSeconds(60), default);
    Assert(split.Count == 3, "Real killable subprocess splits into three physical PDFs.");
    foreach (var path in split)
    {
        using var page = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        Assert(page.PageCount == 1, "Each saved PDF physically contains exactly one page.");
    }
    using (var splitter = new PdfSplitter())
    {
        var boundary = MakePdf("100.pdf", 100);
        splitter.SplitFile(boundary, UploadPolicy.DocumentIntelligenceMaxPageBytes, default);
        Assert(splitter.Pages.Count == 100, "Actual page count accepts 100.");
    }
    var manyOutput = Path.Combine(temporary, "101-pages");
    Directory.CreateDirectory(manyOutput);
    var manyPages = await PdfSubprocess.ExecuteAsync(MakePdf("101.pdf", 101), manyOutput,
        UploadPolicy.DocumentIntelligenceMaxPageBytes, TimeSpan.FromSeconds(60), default);
    Assert(manyPages.Count == 101, "Real subprocess accepts more than 100 physical pages.");
    var aggregateOutput = Path.Combine(temporary, "aggregate");
    Directory.CreateDirectory(aggregateOutput);
    for (var index = 1; index <= 4; index++)
    {
        using var output = File.Create(Path.Combine(aggregateOutput, $"{index}.pdf"));
        output.Write("%PDF-"u8);
        output.SetLength(26L * 1024 * 1024);
    }
    Assert(new DirectoryInfo(aggregateOutput).GetFiles().Sum(f => f.Length) > 100L * 1024 * 1024
        && PdfSubprocess.ValidateOutputs(aggregateOutput, UploadPolicy.DocumentIntelligenceMaxPageBytes).Count == 4,
        "Parent output validation has no former 100 MiB aggregate cap.");
    var large = Path.Combine(temporary, "large.pdf");
    using (var document = new PdfDocument())
    {
        document.AddPage();
        document.Info.Subject = new string('x', 26 * 1024 * 1024);
        document.Save(large);
    }
    Assert(new FileInfo(large).Length > 25L * 1024 * 1024, "Large fixture actually exceeds 25 MiB.");
    using (var splitter = new PdfSplitter())
    {
        await using var input = File.OpenRead(large);
        await splitter.ReadAsync(input, default);
        Assert(splitter.Pages.Count == 1, "Real PDF over 25 MiB passes input streaming and splitting.");
    }
    using (var splitter = new PdfSplitter())
        await Reject(() => Task.Run(() => splitter.SplitFile(MakePdf("encrypted.pdf", 1, true), UploadPolicy.DocumentIntelligenceMaxPageBytes, default)), "Reject encrypted PDF even with empty user password.");
    var timedOutput = Path.Combine(temporary, "timeout");
    Directory.CreateDirectory(timedOutput);
    await Reject(() => PdfSubprocess.ExecuteAsync(small, timedOutput, UploadPolicy.DocumentIntelligenceMaxPageBytes, TimeSpan.FromMilliseconds(1), default), "Subprocess timeout kills parser.");
    var perPageOutput = Path.Combine(temporary, "page-bound");
    Directory.CreateDirectory(perPageOutput);
    await Reject(() => PdfSubprocess.ExecuteAsync(small, perPageOutput, 5, TimeSpan.FromSeconds(60), default), "Per-page output cap enforced.");
    // Optional fixture argument is read-only and never copied back or changed.
    if (args.Length == 1)
    {
        var fixtureOutput = Path.Combine(temporary, "fixture");
        Directory.CreateDirectory(fixtureOutput);
        var fixturePages = await PdfSubprocess.ExecuteAsync(Path.GetFullPath(args[0]), fixtureOutput,
            UploadPolicy.DocumentIntelligenceMaxPageBytes, TimeSpan.FromSeconds(60), default);
        Assert(fixturePages.Count > 0, "Existing fixture splits offline without modification.");
    }
}
finally { Directory.Delete(temporary, true); }
Console.WriteLine($"All {checks} automated checks passed. No live Azure calls.");

sealed record CapturedLog(LogLevel Level, EventId EventId, Exception? Exception, Dictionary<string, object?> Fields, string Message);
sealed class CapturingLogger : ILogger<TokenDiagnostics>
{
    public List<CapturedLog> Entries { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var fields = ((IEnumerable<KeyValuePair<string, object?>>)state!).ToDictionary(pair => pair.Key, pair => pair.Value);
        lock (Entries) Entries.Add(new(logLevel, eventId, exception, fields, formatter(state, exception)));
    }
}
sealed class FailureHandler(string body, HttpStatusCode status = HttpStatusCode.Forbidden) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
        Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
}
sealed class TestEnvironment(string environment) : IHostEnvironment
{
    public string EnvironmentName { get; set; } = environment;
    public string ApplicationName { get; set; } = "OfflineTests";
    public string ContentRootPath { get; set; } = Path.GetTempPath();
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
sealed class TestManagedIdentityCredential(string token) : ManagedIdentityCredential(ManagedIdentityId.SystemAssigned)
{
    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
        new(token, DateTimeOffset.UtcNow.AddHours(1));
    public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
        ValueTask.FromResult(GetToken(requestContext, cancellationToken));
}
sealed class IdentityDiagnosticHandler(string expectedToken, HttpStatusCode status, string requestId) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (request.Headers.Authorization?.Scheme != "Bearer" || request.Headers.Authorization.Parameter != expectedToken)
            throw new InvalidOperationException("Diagnostic must inspect exactly the token attached to the failing request.");
        var response = new HttpResponseMessage(status) { Content = new StringContent("""{"error":{"code":"PermissionDenied"}}""") };
        response.Headers.TryAddWithoutValidation("apim-request-id", requestId);
        response.Headers.TryAddWithoutValidation("ignored-secret-header", "HEADER_SECRET_SENTINEL");
        return Task.FromResult(response);
    }
}
sealed class TestClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;
    public override DateTimeOffset GetUtcNow() => Now;
}
sealed class FakeCheckpoint(Job job) : IJobCheckpoint
{
    public Job Job { get; } = job;
    public List<Job> Snapshots { get; } = [];
    public Action? OnSave { get; init; }
    public Task SaveAsync()
    {
        Snapshots.Add(JsonSerializer.Deserialize<Job>(JsonSerializer.Serialize(Job, Storage.Json), Storage.Json)!);
        OnSave?.Invoke();
        return Task.CompletedTask;
    }
}
sealed class FakeClassifier : IClassifier
{
    public int Submissions { get; private set; }
    public bool FailSubmission { get; init; }
    public Task<string> SubmitAsync(Stream page, CancellationToken ct)
    {
        Submissions++;
        if (FailSubmission) throw new ClassifierException("Uncertain network outcome.", true);
        return Task.FromResult(Guid.NewGuid().ToString());
    }
    public Task<ClassificationPoll> PollAsync(string operationId, CancellationToken ct) => Task.FromResult(new ClassificationPoll(true, "notes", .9));
}
sealed class FakePages : IPageSource
{
    public Action? OnOpen { get; init; }
    public Task<Stream> OpenAsync(WorkItem work, int page, CancellationToken ct)
    {
        OnOpen?.Invoke();
        return Task.FromResult<Stream>(new MemoryStream("%PDF-single-page"u8.ToArray()));
    }
}
sealed class FakeCredential : TokenCredential
{
    public Action? OnToken { get; init; }
    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
    {
        OnToken?.Invoke();
        return new("offline-test-token", DateTimeOffset.UtcNow.AddHours(1));
    }
    public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) => ValueTask.FromResult(GetToken(requestContext, cancellationToken));
}
sealed class BinaryHandler : HttpMessageHandler
{
    public Action? OnSend { get; init; }
    public int Requests { get; private set; }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests++;
        OnSend?.Invoke();
        if (request.Headers.Authorization?.Scheme != "Bearer" || request.Headers.Contains("Ocp-Apim-Subscription-Key")
            || request.Content?.Headers.ContentType?.MediaType != "application/octet-stream"
            || !request.RequestUri!.Query.Contains("split=none")
            || Encoding.ASCII.GetString(await request.Content.ReadAsByteArrayAsync(ct)) != "%PDF-single-page")
            throw new InvalidOperationException("Request was not keyless binary single-page classification.");
        var response = new HttpResponseMessage(HttpStatusCode.Accepted);
        response.Headers.Add("Operation-Location", "https://doc-wellpump-swc-mx01.cognitiveservices.azure.com/documentintelligence/documentClassifiers/wellpump-page-classifier-v1.1/analyzeResults/00000000-0000-0000-0000-000000000001?api-version=2024-11-30");
        return response;
    }
}
