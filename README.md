# Wellpump page classifier

Anonymous public React + TypeScript frontend hosted by a .NET 10 ASP.NET Core application.
The same app runs a durable Storage Queue worker. Application code and the reviewed
Sweden Central Bicep deployment are included. See [Azure deployment](infra/README.md)
for provisioning, scoped system-identity roles, costs and code redeployment.

**User-approved uncapped usage:** There are no application file-size, page-count,
daily-submission, job-count, retained-byte, concurrent-upload or request-rate caps.
The anonymous public endpoint can incur unbounded classification and storage charges.
Azure service limits, hosting capacity and timeouts still apply; this is not unlimited
compute. Each split page must fit Document Intelligence S0's 500 MB document limit.
The uncapped release was redeployed on 2026-10-29 and verified with a valid original
over 25 MiB, real page classifications/downloads, concurrent uploads, history beyond
30 entries and requests above the former rate limits.

Deployment URL: <https://app-wellpump-demo-7d79.azurewebsites.net>.
This is an anonymous public proof of concept; use non-sensitive PDFs only.

**Deployment verification, 2026-10-28:** The live two-page flow succeeds: both pages
are classified and each page downloads as a one-page PDF through a fresh, five-minute,
read-only HTTPS user-delegation SAS. Readiness and browser ownership isolation pass.
The user-approved Cognitive Services User role is assigned only on the Document
Intelligence account; the redundant custom role and assignment were removed.
Authentication remains managed identity; the application does not retrieve or use
account keys. See the broader-role permission warning below.

## Processing and API

1. `GET /api/config` creates a **256-bit random, host-only, Secure, HttpOnly, SameSite=Strict**
   seven-day browser cookie. Only its SHA-256 hash is used in blob paths/manifests.
2. `POST /api/documents` requires that cookie and an exact allowed `Origin`, with one multipart
   PDF file. The HTTP path validates extension, actual multipart size and `%PDF-` signature;
   **it does not parse untrusted PDFs**. A leased global job ledger tracks durable work,
   an `uploading` manifest is written, and the original is streamed to a random job path.
   `202` means the manifest and durable queue enqueue were confirmed.
3. The worker downloads the original to an isolated temporary directory and starts the same
   application as `dotnet WellpumpClassifier.dll --split-pdf input output maxPageBytes`.
   Only the subprocess loads PDFsharp. It rejects encrypted/malformed PDFs and verifies actual
   page count. Parent enforces a 60-second timeout with `Kill(entireProcessTree: true)`, validates
   numbered outputs, and removes temporary files in `finally`. Only one parser runs
   across instances by a renewable blob lease.
4. Each physically split **one-page binary PDF** is saved as `application/pdf` under
   `uploads/owners/<hash>/jobs/<opaque-id>/pages/<original-page-number>.pdf`, then sent with a
   bearer token to `wellpump-page-classifier-v1.1`, API **2024-11-30**, `split=none`.
   Neither the original multi-page PDF nor a SAS URL is submitted to the classifier.
5. Manifests persist page status, class, confidence, errors, submission attempts, operation
   start time and DI operation ID. Each queue delivery advances one submit/poll/checkpoint
   step, or splits a new document. Restarted workers poll saved operation IDs without
   submitting again. Empty/unknown/invalid classifier results fail explicitly.
6. `GET /api/documents` lists **only this cookie's jobs**; `GET /api/documents/{id}` polls one.
   All status/page endpoints authorize both owner hash and job ID and deny expired records.
   No public cross-owner listing exists. Job IDs alone grant no access.
7. `GET /api/documents/{id}/pages/{positive-page}/view` validates the cookie and server-owned
   page mapping, generates a fresh **five-minute HTTPS read-only exact-blob user-delegation SAS**,
   and returns a no-store redirect. It never saves or logs the SAS. UI uses
   `target="_blank" rel="noopener noreferrer"`. **Copied SAS links are bearer access until expiry.**

PDFsharp **6.2.3 is MIT-licensed**. Newer 6.5 is AGPL/licensed and intentionally not used.
The explicit cryptography dependency overrides an older vulnerable transitive version.

## Durable coordination and failure semantics

- Manifests use renewable 60-second blob leases plus ETag/lease-conditioned writes.
  Lease renews every 20 seconds; queue visibility starts at 120 seconds and renews every
  40 seconds. Lost ownership cancels work; stale writers cannot overwrite checkpoints.
- Work stays in `wellpump-jobs` until terminal. Expected polls/retries update visibility rather
  than relying on queue dequeue count. HTTP 408/429/5xx and transport failures back off
  exponentially (2–300 seconds); at most five page submissions and six consecutive poll
  failures. Existing operations expire after 24 hours without automatic resubmission.
- Permanent classification failures are surfaced per page; infrastructure failures are
  retried eight times, then the job fails and a diagnostic enters `wellpump-poison`.
  Invalid messages/missing manifests are also poisoned. No PDF contents are put in queues.
- A once-per-minute reconciler recovers manifests with an unconfirmed enqueue or a stale
  enqueue heartbeat. If enqueue confirmation fails, upload returns **503**, not fake success;
  saved work remains in browser history and may be recovered automatically. Avoid immediately
  re-uploading such a document. Duplicated queue messages are safe under job leases/checkpoints.
- Interrupted uploads become explicit failures after 15 minutes, including admission entries
  created before a manifest write failed. Partial blobs remain conservatively charged until cleanup.
- Aggregate submission tracking and its UI display have been removed. Configuration,
  uploads and classification do not read or update a daily usage counter.
  `/api/config` has no `quota` object; removed application limits remain JSON `null`.
  Public page results do not expose submission counts. Internal per-page attempt
  metadata remains solely to preserve bounded retries and crash recovery.
- Jobs previously delayed as `quota_blocked` resume through the worker/reconciler
  without waiting for midnight. Document access still expires at its retention deadline.
  Submission APIs have no
  idempotency key: a crash after a successful DI POST but before saving its operation ID can
  cause a later charged resubmission. Removing application counters does not change
  Azure's own usage metering or billing.

## Public-demo safeguards and configuration

Environment variables use ASP.NET Core double-underscore nesting. `.env.sample` documents
every setting; it is **not automatically loaded**. No keys, passwords, connection strings,
client secrets, saved tokens or permissive CORS are supported.

Required production settings:

| Variable | Value |
|---|---|
| `Cloud__Credential` | `ManagedIdentity` (default; explicitly system-assigned) |
| `Cloud__PublicOrigin` | Exact deployed browser HTTPS origin, **no trailing slash** |
| `Cloud__BlobEndpoint` | `https://stwellpumpdocsswmx01.blob.core.windows.net/` |
| `Cloud__QueueEndpoint` | `https://stwellpumpdocsswmx01.queue.core.windows.net/` |
| `Cloud__DocumentIntelligenceEndpoint` | `https://doc-wellpump-swc-mx01.cognitiveservices.azure.com/` |
| `Cloud__ClassifierId` | `wellpump-page-classifier-v1.1` |
| `PORT` | `8080` on the planned host |
| `ASPNETCORE_URLS` | `http://0.0.0.0:8080`; takes precedence over PORT/default 3000 |

Use the hostname **emitted by deployment**, not an assumed `appname.azurewebsites.net` name.
Endpoint defaults are the resources above. Invalid required configuration disables APIs/worker
and returns not-ready; liveness remains available.

| `DemoLimits__` suffix | Default | Scope |
|---|---:|---|
| `RetentionHours` | 24 | App-access lifetime and cleanup eligibility |
| `ParserTimeoutSeconds` | 60 | Killable subprocess timeout |

The former 25 MiB original, 100-page, 100 MiB aggregate, 500/day, per-browser,
global storage/job, concurrency-rejection and request-rate caps were removed at the
user's explicit request. Kestrel has no request-body cap; multipart file length uses
the framework's maximum representable value. The application-specific two-minute
upload deadline is removed. Host/proxy/network timeouts can still interrupt uploads.
The worker checks each physical page against the documented 500 MB S0 service limit;
it does not cap total original or aggregate page-PDF bytes.

Uploads spool to host temporary disk before Blob Storage, and splitting holds the
source PDF in the parser process. With no application caps, large/concurrent PDFs can
exhaust disk or memory, fail, or restart the host. A single worker/parser processes
queued work serially; no throughput or successful processing of arbitrarily large
files is promised. The job ledger and history have no application count limit, so
large histories can also increase memory, transactions and response time.

Ownership cookies, exact-origin enforcement, PDF validation, retention, parser
execution timeout and bounded recovery retries remain. Proxy headers are not trusted.
Authentication and stronger abuse protection are future production requirements.

### Retention, privacy and limitations

App access ends **24 hours after upload admission**; endpoints stop issuing links then, even
if cleanup is delayed. The worker removes each expired job's pages, original and manifest and
releases its admission reservation. It cleans old orphans only in this application's validated
`owners/<hash>/jobs/<id>/` namespace, never unrelated blob prefixes or existing PDF fixtures.
Stale `wellpump-worker-*` local directories older than an hour are removed on worker sweeps.
Historical rate/quota blobs from older releases age out through the existing two-day
cleanup. No new usage counters are created, and old counter values are not read or reset.
Existing storage policies are **not modified**.

The existing account has **7-day blob and container soft-delete retention**. Deleted blobs
may therefore remain recoverable for seven days; **24 hours is not a promise of physical
erasure**. Already-issued SAS can grant access until its five-minute expiry unless the live
blob has been deleted. Azure DI may retain analysis data according to its service policy;
app blob cleanup does not purge service-side analysis results. Do not upload sensitive data.
Persistent application stdout logs include opaque job IDs/error types, not filenames, PDF
contents, cookies, credentials, operation URLs or SAS URLs. Do not enable response-header
logging that captures `Location`, or HTTP logging that captures Cookie/Authorization.

**Memory boundary:** the subprocess has a strict execution deadline and per-page service-size checks, but
there is no kernel address-space/RSS sandbox or dedicated per-parser memory cgroup. Host and
parser share B1's approximately 1.75 GiB; adversarial decompression can still pressure memory
or trigger host OOM before timeout. One parser at a time reduces that risk but does not remove
it. Dedicated sandbox compute with OS memory/CPU quotas is required for hostile production
traffic. B1 Always On is also **not high availability**; durable state allows recovery, not
continuous service through host outages. Cleanup/reconciliation requires the worker running.

## Hosting prerequisites (provisioned separately)

- Private `uploads` container, `wellpump-jobs` and `wellpump-poison` queues must already exist.
  Application never creates containers/queues or changes lifecycle policies.
- System-assigned identity: uploads container **Storage Blob Data Contributor**;
  storage account **Storage Blob Delegator**; each queue **Storage Queue Data Contributor**;
  DI resource **Cognitive Services User**, scoped only to `doc-wellpump-swc-mx01`.
  This broader built-in role was explicitly approved by the user after the narrow
  custom classifier role remained denied. It also permits account-key export,
  connection-secret retrieval and broader model operations; it is not inference-only.
  The application only calls classifier metadata, analyze and result endpoints and
  does not retrieve or use account keys or connection secrets. Successful live access
  establishes compatibility, not which additional permission was necessary.
  Storage shared-key access may remain disabled.
- Linux App Service B1, .NET 10 runtime, **Always On**, initial one worker replica,
  **HTTPS-only at ingress**. App Service terminates TLS; application intentionally does not
  infer security from caller-supplied forwarded headers. Outside App Service, production HTTP
  redirects to the configured HTTPS origin (except health probes).
- No App Insights/Log Analytics dependency. Platform stdout log streaming is sufficient.
  Avoid logging SAS redirect headers. No Basic SCM/FTP auth or credentials are required by source.
- `/healthz`: live **200**. `/readyz`: validates config, storage/queue access,
  user-delegation authorization and classifier metadata access, **no paid inference**;
  dependency/auth failures **503**, generic body. Unknown `/api/*` always **404**, not SPA HTML.
  Other client routes fall back to `index.html`.
  Each failed readiness dependency logs Warning event `4101` (`ReadinessDependencyFailed`)
  under application category `WellpumpClassifier`, with structured fields `Dependency`,
  `ErrorType`, `Status`, `ErrorCode`, and `DeniedAction`. Dependency names are `uploads_container`,
  `jobs_queue`, `poison_queue`, `blob_delegation`, `classifier_metadata`. Storage SDK
  status/error-code identifiers and DI HTTP status/error code are recorded where available;
  other values are null. No exception objects/messages, response bodies, URLs, tokens,
  SAS or headers are logged. Inspect these redacted fields through platform log streaming.
  DI diagnostics parse at most 4096 response bytes (JSON depth at most 16), extracting
  only `error.code` when it is 1–64 ASCII letters/digits/underscore/hyphen/period.
  Missing, malformed, oversized or non-identifier codes are omitted; messages, inner
  errors and headers are never included in the exception diagnostics or log.
  From that same bounded payload's `error.message`, the first identifier matching
  `Microsoft.CognitiveServices/[A-Za-z0-9_/:.\-]+` is retained as `DeniedAction` only
  when its full length is at most 256. The rest of the message, principal/identity and
  token strings are discarded. Overlong identifiers are omitted, never truncated.

### Temporary, one-shot MI token diagnostics

Disabled by default. For a DI 401 investigation only, temporarily set
`Diagnostics__ExpectedPrincipalId` to the expected system-assigned principal GUID and,
optionally, `Diagnostics__ExpectedTenantId` to its expected tenant GUID. Remove both
after troubleshooting. No extra role, API call, token acquisition or API response is added.

Only when the actual request credential is `ManagedIdentityCredential`, environment is
not Development, and expected principal is a valid GUID, the first DI HTTP 401 per process
emits Warning event **4102 / DocumentIntelligenceIdentityDiagnostic**, category
**WellpumpClassifier.TokenDiagnostics**. An Interlocked gate makes this one-shot even
under concurrent failures. It inspects the exact AccessToken attached to the rejected
request, locally; it does not validate the JWT or change an authorization decision.

Fields are only `OidPresentAndValid`, `OidMatchesExpected`, `TenantMatchesExpected`
(booleans), `Audience` (`ExpectedCognitiveServices`, `Unexpected`, `MissingOrMalformed`),
`UtcTime`, and optional `ServiceRequestId`. Without a valid optional expected tenant,
`TenantMatchesExpected` is false. Expected audience is exactly
`https://cognitiveservices.azure.com` with or without one trailing slash.
Token length is bounded to 32768 characters, encoded payload to 16384, and JSON depth to 8.
Malformed payloads produce false comparison flags and `MissingOrMalformed`, without
logging parse errors. No token, raw claim/oid/tenant, expected IDs, unexpected audience,
exception, identity or arbitrary header value is logged. Azure CLI credentials and
Development never emit this diagnostic.

For correlation, only a single canonical GUID from `apim-request-id`, falling back to
`x-ms-request-id`, may become `ServiceRequestId`. Invalid or multi-valued values are
omitted. No other header is inspected or logged. A process restart resets the one-shot;
removing the settings disables diagnostics on subsequent starts.

## Local development and build

Requires stable .NET 10 SDK/runtime and Node 24 LTS (or another supported stable Node release).
No preview .NET/Node dependencies are required.

```sh
# Deliberate local opt-in; Azure CLI login/RBAC is an operator action.
ASPNETCORE_ENVIRONMENT=Development Cloud__Credential=AzureCli \
  Cloud__PublicOrigin=http://localhost:5173 PORT=3000 \
  dotnet run --project backend/WellpumpClassifier.csproj
# Separate terminal:
cd frontend
npm ci
npm run dev
```

Only Development + explicit `Cloud__Credential=AzureCli` enables AzureCliCredential.
Development relaxes the cookie Secure flag for localhost; production never does.
Vite proxies same-origin `/api` to port 3000. No CORS is enabled.
Running with valid cloud configuration starts the worker and **does access Azure**.
To inspect liveness/static assets completely offline, leave production PublicOrigin unset:
worker/APIs stay disabled, `/healthz` is 200 and `/readyz` is 503 without network calls.

```sh
dotnet run --project backend/tests/WellpumpClassifier.Tests.csproj
# Optional existing fixture read-only; outputs are temporary and deleted:
dotnet run --project backend/tests/WellpumpClassifier.Tests.csproj -- /absolute/path/to/fixture.pdf
cd frontend && npm run build && cd ..
bash scripts/publish.sh
cd artifacts/server
PORT=8080 dotnet WellpumpClassifier.dll
```

Publish script builds React into `backend/wwwroot`, runs the .NET automated harness, and
framework-dependent publishes both to `artifacts/server`; install frontend dependencies first.
It requires the `dotnet` executable on PATH for the parser subprocess.
The console harness is the existing project's automated test pattern; it exits nonzero on
failure and does not call live Azure. Tests cover uploads over the former size/page limits,
actual splitting, subprocess kill, encryption, authorization, SAS permissions/expiry,
explicit invalid-result errors, pre-transport retry checkpoints and crash recovery.

If the environment's npm proxy advertises broken nested tarball URLs, use its canonical
`/npm/<package>/-/<tarball>.tgz` route to populate npm's cache, then retry offline-preferred
installation. Missing Rollup platform binaries are npm optional-dependency installation
issues; do not pin a macOS-only package in the shared manifest. Normal registries do not
need this workaround.

## Operator integration smoke

Use a **synthetic, non-sensitive two-page PDF** and authorized cloud resources. Do not run
this procedure against real customer data or as part of unit tests.

1. Configure deployed emitted hostname as PublicOrigin; verify HTTPS-only, system identity,
   RBAC propagation, private container/queues, startup command and Always On.
2. Fetch `/healthz` (200), `/readyz` (200 once RBAC is effective), `/api/not-an-endpoint`
   (404), and a client route (SPA). Denied DI/storage permissions should make readyz 503.
3. In browser A upload the synthetic PDF; expect 202 and original page numbers 1,2.
   Verify each page blob contains exactly one PDF page and result class/confidence comes
   from the service. Configuration and job responses must not expose submission counts.
4. Restart app between submit/poll; verify saved operation IDs resume polling without
   resubmitting. Stop/start during splitting and after storing files to exercise
   idempotent outputs and reconciliation. Induced queue failure must not return 202.
5. Click a page link; new tab shows only that page, SAS is HTTPS `sp=r`, correct blob,
   five-minute expiry. Confirm redirect destination/SAS never appear in application logs.
   After expiry, reuse fails; copied still-live SAS is intentionally bearer access.
6. In independent browser B request A's known job/status/view URLs: 404. Missing cookie,
   mismatched Origin POST, zero/negative/out-of-range page, nonPDF and malformed/encrypted
   PDF must fail explicitly. A valid original over 25 MiB and a valid PDF over 100 pages
   must not fail the former application caps. Parsing failures are asynchronous job
   failures, not fabricated successful classifications.
7. Verify `/api/config` uses null limits and has no `quota` object. With an already
   authorized Blob Data Reader identity, compare historical `quota/` blob names/ETags
   before and after configuration polling and classification:
   no new aggregate counters or updates should appear. Verify the UI has no usage count.
   Do not reset historical counter data. History beyond 30 entries and request rates
   above former limits remain supported.
8. Observe retention cleanup using a shortened supported RetentionHours setting in an
   isolated test deployment. Confirm app-access denial at expiry, own-prefix deletion,
   admission release, and unchanged seven-day soft-delete policy. Restore normal settings.

Manual cloud smoke, browser E2E and host-memory behavior remain operator verification steps.



Adding some random text at the bottom to test Automatic PR approval
