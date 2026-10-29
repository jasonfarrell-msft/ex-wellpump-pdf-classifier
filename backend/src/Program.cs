using Azure.Core;
using Azure.Identity;
using Microsoft.AspNetCore.Http.Features;
using WellpumpClassifier;
using WellpumpClassifier.Data;
using WellpumpClassifier.Models;

if (args.Length > 0 && args[0] == "--split-pdf") return PdfSubprocess.RunChild(args);

var builder = WebApplication.CreateBuilder(args);
if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ASPNETCORE_URLS"))
    && string.IsNullOrEmpty(builder.Configuration["urls"]))
{
    var portValue = Environment.GetEnvironmentVariable("PORT") ?? "3000";
    if (!int.TryParse(portValue, out var port) || port is < 1 or > 65535)
        throw new InvalidOperationException("PORT must be an integer between 1 and 65535.");
    builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
}
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = null);
builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = long.MaxValue;
    options.ValueLengthLimit = 1024;
    options.ValueCountLimit = 4;
});
var settings = builder.Configuration.GetSection("Cloud").Get<CloudSettings>() ?? new();
if (builder.Environment.IsDevelopment() && string.IsNullOrEmpty(settings.PublicOrigin))
    settings.PublicOrigin = "http://localhost:5173";
var runtime = new RuntimeState();
var limits = builder.Configuration.GetSection("DemoLimits").Get<DemoLimits>() ?? new();
try { settings.Validate(builder.Environment.IsDevelopment()); limits.Validate(); runtime.ConfigurationValid = true; }
catch (InvalidOperationException) { runtime.ConfigurationValid = false; }
builder.Services.AddSingleton(settings);
builder.Services.AddSingleton(limits);
builder.Services.AddSingleton(runtime);
builder.Services.AddSingleton<TokenCredential>(_ => settings.Credential == "AzureCli" && builder.Environment.IsDevelopment()
    ? new AzureCliCredential()
    : new ManagedIdentityCredential(ManagedIdentityId.SystemAssigned));
builder.Services.AddSingleton(sp => new Storage(runtime.ConfigurationValid ? settings : new CloudSettings(),
    sp.GetRequiredService<TokenCredential>()));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(builder.Configuration.GetSection("Diagnostics").Get<IdentityDiagnosticsSettings>() ?? new());
builder.Services.AddSingleton<TokenDiagnostics>();
builder.Services.AddSingleton<Admission>();
builder.Services.AddSingleton<Retention>();
builder.Services.AddSingleton<PdfSubprocess>();
// No HttpClientFactory request logging: responses contain operation locations; SAS is never sent through this client.
builder.Services.AddSingleton(_ => new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
{ Timeout = TimeSpan.FromSeconds(30), MaxResponseContentBufferSize = 1024 * 1024 });
builder.Services.AddSingleton<Classifier>();
builder.Services.AddSingleton<IClassifier>(sp => sp.GetRequiredService<Classifier>());
builder.Services.AddSingleton<IPageSource, BlobPageSource>();
builder.Services.AddSingleton<PageProcessor>();
builder.Services.AddHostedService<JobWorker>();

var app = builder.Build();
if (!runtime.ConfigurationValid)
    app.Logger.LogError("Cloud configuration is invalid. Readiness and application APIs are disabled; inspect Cloud settings.");
app.UseRouting();
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["Content-Security-Policy"] =
        "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; " +
        "connect-src 'self'; object-src 'none'; base-uri 'self'; frame-ancestors 'none'; form-action 'self'";
    if (!app.Environment.IsDevelopment())
        context.Response.Headers.StrictTransportSecurity = "max-age=31536000";
    if (context.Request.Path.StartsWithSegments("/api") || context.Request.Path == "/readyz")
        context.Response.Headers.CacheControl = "no-store";
    // App Service must have HTTPS-only enabled at ingress. Do not infer identity, origin or
    // client IP from arbitrary forwarded headers. Outside App Service, enforce TLS here.
    var health = context.Request.Path == "/healthz" || context.Request.Path == "/readyz";
    if (!health && !app.Environment.IsDevelopment() && !context.Request.IsHttps
        && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WEBSITE_SITE_NAME")) && runtime.ConfigurationValid)
    {
        context.Response.Redirect(settings.PublicOrigin + context.Request.Path + context.Request.QueryString);
        return;
    }
    if (context.Request.Path.StartsWithSegments("/api") && !runtime.ConfigurationValid
        && context.GetEndpoint()?.Metadata.GetMetadata<UnknownApi>() is null)
    {
        context.Response.StatusCode = 503;
        await context.Response.WriteAsJsonAsync(new { error = "Application configuration is unavailable." });
        return;
    }
    try { await next(context); }
    catch (BadHttpRequestException ex)
    {
        context.Response.StatusCode = ex.StatusCode;
        await context.Response.WriteAsJsonAsync(new { error = "Invalid or oversized request." });
    }
    catch (InvalidDataException ex)
    {
        context.Response.StatusCode = 400;
        await context.Response.WriteAsJsonAsync(new { error = ex.Message });
    }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
    catch (Exception ex)
    {
        app.Logger.LogError("Request failed ({ErrorType}).", ex.GetType().Name);
        if (context.Response.HasStarted) throw;
        context.Response.StatusCode = 503;
        await context.Response.WriteAsJsonAsync(new { error = "A processing dependency is unavailable. Please retry." });
    }
});
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));
app.MapGet("/readyz", async (Storage storage, Classifier classifier, CancellationToken ct) =>
{
    if (!runtime.ConfigurationValid) return Results.Json(new { status = "not_ready" }, statusCode: 503);
    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
    timeout.CancelAfter(TimeSpan.FromSeconds(15));
    var checks = await Task.WhenAll(
        Readiness.CheckAsync("uploads_container", async token => { await storage.Container.GetPropertiesAsync(cancellationToken: token); }, app.Logger, timeout.Token),
        Readiness.CheckAsync("jobs_queue", async token => { await storage.Queue.GetPropertiesAsync(token); }, app.Logger, timeout.Token),
        Readiness.CheckAsync("poison_queue", async token => { await storage.Poison.GetPropertiesAsync(token); }, app.Logger, timeout.Token),
        Readiness.CheckAsync("blob_delegation", async token =>
        {
            await storage.Service.GetUserDelegationKeyAsync(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(6), token);
        }, app.Logger, timeout.Token),
        Readiness.CheckAsync("classifier_metadata", classifier.CheckAsync, app.Logger, timeout.Token));
    return checks.All(ready => ready) ? Results.Ok(new { status = "ready" })
        : Results.Json(new { status = "not_ready" }, statusCode: 503);
});

app.MapGet("/api/config", (HttpContext context) =>
{
    Capability.Grant(context, app.Environment.IsDevelopment());
    return Results.Ok(new
    {
        classifierId = settings.ClassifierId,
        documentIntelligenceResource = "doc-wellpump-swc-mx01",
        uploadContainer = "uploads",
        maxFileBytes = (long?)null,
        maxPagesPerDocument = (int?)null,
        maxClassifiedPagesPerDay = (int?)null,
        retentionHours = limits.RetentionHours,
        maxPageBytes = UploadPolicy.DocumentIntelligenceMaxPageBytes,
        globalPendingJobs = (int?)null,
        globalRetainedBytes = (long?)null
    });
});
app.MapGet("/api/documents", async (HttpContext context, Storage storage, CancellationToken ct) =>
{
    var owner = Capability.Owner(context);
    if (owner is null) return Results.Unauthorized();
    var jobs = new List<Job>();
    await foreach (var job in storage.ListAsync(owner, ct)) jobs.Add(job);
    return Results.Ok(jobs.Where(j => j.ExpiresAt > DateTimeOffset.UtcNow).OrderByDescending(j => j.CreatedAt).Select(JobView));
});
app.MapGet("/api/documents/{id}", async (string id, HttpContext context, Storage storage, CancellationToken ct) =>
{
    var job = await OwnedJobAsync(id, context, storage, ct);
    return job is null ? Results.NotFound() : Results.Ok(JobView(job));
});
app.MapGet("/api/documents/{id}/pages/{page:int}/view", async (string id, int page, HttpContext context, Storage storage, CancellationToken ct) =>
{
    var job = await OwnedJobAsync(id, context, storage, ct);
    if (job is null || page < 1 || job.Status == "uploading" || !job.Pages.Any(p => p.PageNumber == page && p.BlobReady))
        return Results.NotFound();
    var link = await storage.PageLinkAsync(new(job.OwnerHash, job.Id), page, ct);
    // Generated on demand, never persisted. Referrer-Policy suppresses the API URL on redirect.
    // RedirectHttpResult logs its destination at Information level, which would expose
    // the SAS. Write the redirect directly instead.
    context.Response.StatusCode = StatusCodes.Status302Found;
    context.Response.Headers.Location = link.AbsoluteUri;
    return Results.Empty;
});

app.MapPost("/api/documents", async (HttpContext context, Storage storage, Admission admission, CancellationToken ct) =>
{
    if (!Capability.SameOrigin(context.Request.Headers.Origin, settings.PublicOrigin))
        return Results.Json(new { error = "Upload Origin must match this application." }, statusCode: 403);
    var owner = Capability.Owner(context);
    if (owner is null) return Results.Unauthorized();
    if (!context.Request.HasFormContentType) return Results.BadRequest(new { error = "Use multipart/form-data with one PDF file." });
    var job = new Job
    {
        OwnerHash = owner,
        FileName = "document.pdf",
        ExpiresAt = DateTimeOffset.UtcNow.AddHours(limits.RetentionHours)
    };
    var work = new WorkItem(owner, job.Id);
    await admission.ReserveAsync(work, ct);
    await storage.CreateAsync(job, ct);
    await using var lease = await BlobLease.TryAcquireAsync(storage.Manifest(work), ct);
    if (lease is null) throw new IOException("Unable to acquire upload checkpoint.");
    var checkpoint = await JobCheckpoint.LoadAsync(storage.Manifest(work), lease);
    try
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lease.Token);
        var form = await context.Request.ReadFormAsync(timeout.Token);
        if (form.Files.Count != 1) throw new InvalidDataException("Upload exactly one PDF.");
        var file = form.Files[0];
        var error = UploadPolicy.ValidateMetadata(file.FileName, file.Length);
        if (error is not null) throw new InvalidDataException(error);
        checkpoint.Job.FileName = PdfSplitter.SafeName(file.FileName);
        await using var stream = file.OpenReadStream();
        var signature = new byte[5];
        if (await stream.ReadAsync(signature, timeout.Token) != 5 || !signature.AsSpan().SequenceEqual("%PDF-"u8))
            throw new InvalidDataException("Invalid PDF signature.");
        stream.Position = 0;
        await storage.UploadPdfStreamAsync(storage.Container.GetBlobClient(Storage.Prefix(work) + "original.pdf"),
            stream, checkpoint.Job.FileName, timeout.Token);
        checkpoint.Job.Status = "awaiting_enqueue";
        await checkpoint.SaveAsync();
        await admission.UpdateAsync(work, "pending", null, lease.Token);
    }
    catch (Exception ex) when (!ct.IsCancellationRequested)
    {
        app.Logger.LogError("Job {JobId} file persistence failed ({ErrorType}).", job.Id, ex.GetType().Name);
        checkpoint.Job.Status = "failed";
        checkpoint.Job.Error = ex switch
        {
            InvalidDataException => ex.Message,
            BadHttpRequestException => "Invalid or oversized upload request.",
            _ => "File persistence failed or upload timed out. Upload again."
        };
        await checkpoint.SaveAsync();
        await admission.UpdateAsync(work, "terminal", null, ct);
        return Results.Json(new { error = checkpoint.Job.Error, jobId = job.Id },
            statusCode: ex is InvalidDataException ? 400 : ex is BadHttpRequestException bad ? bad.StatusCode : 503);
    }
    try
    {
        await storage.EnqueueAsync(work, TimeSpan.Zero, lease.Token);
        checkpoint.Job.LastEnqueuedAt = DateTimeOffset.UtcNow;
        checkpoint.Job.Status = "queued";
        await checkpoint.SaveAsync();
    }
    catch (Exception ex) when (!ct.IsCancellationRequested)
    {
        app.Logger.LogError("Job {JobId} enqueue confirmation failed ({ErrorType}); reconciler will recover it.", job.Id, ex.GetType().Name);
        return Results.Json(new
        {
            error = "Files were saved but queuing could not be confirmed. This job is visible in your history and will be recovered automatically; do not immediately re-upload.",
            jobId = job.Id
        }, statusCode: 503);
    }
    return Results.Accepted($"/api/documents/{job.Id}", JobView(checkpoint.Job));
});

app.Map("/api/{**path}", () => Results.NotFound(new { error = "API endpoint not found." })).WithMetadata(new UnknownApi());
app.MapFallbackToFile("index.html");
app.Run();
return 0;

static async Task<Job?> OwnedJobAsync(string id, HttpContext context, Storage storage, CancellationToken ct)
{
    var owner = Capability.Owner(context);
    if (owner is null) return null;
    var work = new WorkItem(owner, id);
    if (!work.IsValid) return null;
    var job = await storage.ReadAsync(work, ct);
    return Capability.Owns(job, owner, id, DateTimeOffset.UtcNow) ? job : null;
}

static object JobView(Job job) => new
{
    job.Id,
    job.FileName,
    job.CreatedAt,
    job.ExpiresAt,
    job.Status,
    job.Error,
    job.NextAttemptAt,
    pages = job.Pages.OrderBy(p => p.PageNumber).Select(p => new
    { p.PageNumber, p.Status, p.Classification, p.Confidence, p.Error, viewAvailable = p.BlobReady })
};

namespace WellpumpClassifier
{
    public sealed class RuntimeState { public bool ConfigurationValid { get; set; } }
    public sealed class UnknownApi;
}
