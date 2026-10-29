import { useEffect, useState } from "react";

interface Configuration {
  classifierId: string;
  documentIntelligenceResource: string;
  maxFileBytes: number | null;
  maxPagesPerDocument: number | null;
  retentionHours: number;
}
interface PageResult {
  pageNumber: number; status: string; classification: string | null;
  confidence: number | null; error: string | null; viewAvailable: boolean;
}
interface Job {
  id: string; fileName: string; createdAt: string; expiresAt: string; status: string;
  error: string | null; nextAttemptAt: string; pages: PageResult[];
}
const statusLabel = (status: string) => status.replaceAll("_", " ");
const terminal = (status: string) => ["completed", "failed", "completed_with_errors"].includes(status);

async function request<T>(url: string, options?: RequestInit): Promise<T> {
  const response = await fetch(url, { ...options, cache: "no-store", credentials: "same-origin" });
  if (!response.ok) {
    let message = `Request failed (${response.status}).`;
    try {
      const body = await response.json() as { error?: string; detail?: string };
      message = body.error ?? body.detail ?? message;
    } catch { /* A proxy can return a non-JSON error. Keep its explicit HTTP status. */ }
    throw new Error(message);
  }
  return response.json() as Promise<T>;
}

export default function App() {
  const [config, setConfig] = useState<Configuration | null>(null);
  const [jobs, setJobs] = useState<Job[]>([]);
  const [selectedId, setSelectedId] = useState(() => localStorage.getItem("wellpump-selected-job") ?? "");
  const [file, setFile] = useState<File | null>(null);
  const [error, setError] = useState("");
  const [connection, setConnection] = useState("Connecting to backend…");
  const [uploading, setUploading] = useState(false);
  const [announcement, setAnnouncement] = useState("");

  useEffect(() => {
    const controller = new AbortController();
    let timer: ReturnType<typeof setTimeout>;
    async function poll() {
      try {
        // Configuration grants the HttpOnly browser capability before any job requests.
        const configuration = await request<Configuration>("/api/config", { signal: controller.signal });
        if (controller.signal.aborted) return;
        setConfig(configuration);
        const history = await request<Job[]>("/api/documents", { signal: controller.signal });
        if (controller.signal.aborted) return;
        setJobs(history);
        setConnection("Backend connected · updates every 5 seconds");
      } catch (failure) {
        if (!controller.signal.aborted)
          setConnection(failure instanceof Error ? failure.message : "Unable to refresh results.");
      } finally {
        if (!controller.signal.aborted) timer = setTimeout(() => void poll(), 5000);
      }
    }
    void poll();
    return () => { controller.abort(); clearTimeout(timer); };
  }, []);

  function chooseFile(selected: File | undefined) {
    setFile(null);
    setError("");
    if (!selected) return;
    if (!selected.name.toLowerCase().endsWith(".pdf")) { setError("Choose a PDF file."); return; }
    if (selected.size === 0) {
      setError("Choose a non-empty PDF."); return;
    }
    setFile(selected);
  }
  function selectJob(id: string) {
    setSelectedId(id);
    localStorage.setItem("wellpump-selected-job", id); // ID is not a capability; the backend still authorizes every request.
  }
  async function upload(event: React.FormEvent) {
    event.preventDefault();
    if (!file || uploading) return;
    setUploading(true); setError(""); setAnnouncement("Uploading and validating PDF…");
    try {
      const body = new FormData();
      body.append("file", file);
      const job = await request<Job>("/api/documents", { method: "POST", body });
      setJobs(previous => [job, ...previous.filter(item => item.id !== job.id)]);
      selectJob(job.id);
      setAnnouncement("Upload accepted. Classification runs in the background.");
    } catch (failure) {
      setError(failure instanceof Error ? failure.message : "Upload failed.");
      setAnnouncement("");
    } finally { setUploading(false); }
  }
  const selected = jobs.find(job => job.id === selectedId) ?? jobs[0];
  const complete = selected?.pages.filter(page => terminal(page.status)).length ?? 0;

  return (
    <main>
      <header>
        <p className="eyebrow">DOCUMENT WORKSPACE / PUBLIC PROOF OF CONCEPT</p>
        <h1>Wellpump page classifier</h1>
        <p className="intro">Upload a PDF. Explore a classification for every page.</p>
      </header>
      <section className="notice" aria-labelledby="limits-heading">
        <h2 id="limits-heading">Service notice</h2>
        <p>No application upload, page-count, daily-submission, job, storage or request-rate caps.
          Azure service quotas and available hosting resources still apply.
          Public usage can incur unbounded classification and storage charges.</p>
        <p role="status">{connection}</p>
      </section>
      <section className="card" aria-labelledby="upload-heading">
        <h2 id="upload-heading">Upload your PDF</h2>
        <p id="file-guidance">Non-empty PDF only · No application file-size or page-count cap
          · No encrypted PDFs. Each split page must fit Document Intelligence&apos;s 500 MB service limit.</p>
        <form onSubmit={event => void upload(event)} aria-busy={uploading}>
          <label htmlFor="pdf-file">PDF document</label>
          <input id="pdf-file" type="file" accept=".pdf,application/pdf" disabled={uploading}
            aria-describedby={`file-guidance${error ? " file-error" : ""}`}
            aria-invalid={Boolean(error)}
            onChange={event => chooseFile(event.target.files?.[0])} />
          <p className="selection">{file ? `${file.name} · ${(file.size / (1024 * 1024)).toFixed(2)} MiB` : "No PDF selected."}</p>
          <button type="submit" disabled={!file || !config || uploading}>
            {uploading ? "Uploading…" : "Upload & classify"}
          </button>
          <p role="status">{announcement}</p>
          {error && <p id="file-error" className="error" role="alert">{error}</p>}
        </form>
        <p className="muted">Anonymous access is isolated to this browser cookie for 7 days.
          Do not upload sensitive documents. App access expires after {config?.retentionHours ?? 24} hours;
          soft-deleted blobs may remain recoverable for 7 days.
          Parsing uses a 60-second safety timeout; Azure timeouts and hosting capacity still apply.
          Anyone with a copied page link can read that page until the 5-minute SAS expires.</p>
      </section>
      <section className="card" aria-labelledby="history-heading">
        <h2 id="history-heading">Your documents</h2>
        {jobs.length === 0 ? <p>No documents in this browser session yet.</p> :
          <ul className="job-list">{jobs.map(job => <li key={job.id}>
            <button type="button" className={selected?.id === job.id ? "" : "secondary"}
              aria-pressed={selected?.id === job.id} onClick={() => selectJob(job.id)}>
              <span>{job.fileName}</span><small>{statusLabel(job.status)} · {new Date(job.createdAt).toLocaleString()}</small>
            </button>
          </li>)}</ul>}
      </section>
      <section className="card" aria-labelledby="results-heading">
        <div className="section-title"><h2 id="results-heading">Page results</h2>
          <span className="badge">{complete} / {selected?.pages.length ?? 0} FINISHED</span></div>
        {selected && <>
          <p className="selection"><strong>{selected.fileName}</strong> · {statusLabel(selected.status)}</p>
          <p className="muted">Expires {new Date(selected.expiresAt).toLocaleString()}.
            {selected.pages.length === 0 && !terminal(selected.status) ? " Validating and splitting in the background…" : ""}</p>
          <progress value={complete} max={Math.max(1, selected.pages.length)}
            aria-label={`Processing progress for ${selected.fileName}`} />
          {selected.error && <p className="error" role="alert">{selected.error}</p>}
        </>}
        <p id="results-guidance">Original page order. View page opens a single-page PDF from blob storage in a
          new tab using a fresh read-only link that expires after 5 minutes.</p>
        <div className="table-scroll" role="region" aria-label="Page classification results" tabIndex={0}>
          <table aria-describedby="results-guidance">
            <caption className="sr-only">Per-page classification status and document links</caption>
            <thead><tr><th scope="col">Page</th><th scope="col">Status / error</th><th scope="col">Classification</th>
              <th scope="col">Confidence</th><th scope="col">View page</th></tr></thead>
            <tbody>{!selected ? <tr><td colSpan={5} className="empty">Upload a PDF to see results.</td></tr> :
              [...selected.pages].sort((a, b) => a.pageNumber - b.pageNumber).map(page => <tr key={page.pageNumber}>
                <td>{page.pageNumber}</td>
                <td><span className={`page-status ${page.status}`}>{statusLabel(page.status)}</span>
                  {page.error && <small className="error">{page.error}</small>}</td>
                <td>{page.classification ? statusLabel(page.classification) : "—"}</td>
                <td>{page.confidence !== null ? `${(page.confidence * 100).toFixed(1)}%` : "—"}</td>
                <td>{page.viewAvailable ? <a href={`/api/documents/${selected.id}/pages/${page.pageNumber}/view`}
                  target="_blank" rel="noopener noreferrer" aria-label={`View page ${page.pageNumber} (new tab)`}>
                  View page ↗</a> : "Not stored"}</td>
              </tr>)}</tbody>
          </table>
        </div>
      </section>
      <footer>
        <h2>Connection</h2>
        <dl>
          <div><dt>Classifier</dt><dd>{config?.classifierId ?? "wellpump-page-classifier-v1.1"}</dd></div>
          <div><dt>Document Intelligence</dt><dd>{config?.documentIntelligenceResource ?? "doc-wellpump-swc-mx01"}</dd></div>
          <div><dt>Blob container</dt><dd>uploads</dd></div>
        </dl>
        <p>System-assigned managed identity · No API keys · Each physical PDF page is submitted independently.</p>
      </footer>
    </main>
  );
}
