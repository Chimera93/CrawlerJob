import { useEffect, useState } from "react";

const api = import.meta.env.VITE_API_URL || "http://localhost:8080";
const route = () => window.location.hash.slice(1) || "/";

function App() {
  const [path, setPath] = useState(route);
  useEffect(() => {
    const change = () => setPath(route());
    window.addEventListener("hashchange", change);
    return () => window.removeEventListener("hashchange", change);
  }, []);
  return <main>
    <header><h1>Website Crawl Jobs</h1><nav><a href="#/">Start crawl</a><a href="#/history">History</a></nav></header>
    {path === "/history" ? <History /> : path.startsWith("/jobs/") ? <Details id={path.split("/")[2]} /> : <Start />}
  </main>;
}

function Start() {
  const [url, setUrl] = useState("");
  const [maxDepth, setMaxDepth] = useState(2);
  const [error, setError] = useState("");
  const [loading, setLoading] = useState(false);
  async function submit(event) {
    event.preventDefault();
    setError("");
    setLoading(true);
    try {
      const response = await fetch(`${api}/api/jobs`, {
        method: "POST", headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ url, maxDepth: Number(maxDepth) })
      });
      if (!response.ok) throw new Error((await response.json()).error || "Could not start crawl.");
      const { jobId } = await response.json();
      window.location.hash = `/jobs/${jobId}`;
    } catch (e) { setError(e.message); }
    finally { setLoading(false); }
  }
  return <section><h2>Start a crawl</h2><form onSubmit={submit}>
    <label>Website URL<input type="url" required placeholder="https://example.com" value={url} onChange={e => setUrl(e.target.value)} /></label>
    <label>Maximum depth<input type="number" min="0" max="10" value={maxDepth} onChange={e => setMaxDepth(e.target.value)} /></label>
    <button disabled={loading}>{loading ? "Starting…" : "Start crawl"}</button>
  </form>{error && <p className="error">{error}</p>}</section>;
}

function History() {
  const [data, setData] = useState(null);
  const [error, setError] = useState("");
  useEffect(() => { fetch(`${api}/api/jobs?page=1&pageSize=50`).then(r => {
    if (!r.ok) throw new Error("Could not load crawl history.");
    return r.json();
  }).then(setData).catch(e => setError(e.message)); }, []);
  return <section><h2>Previous crawls</h2>{error && <p className="error">{error}</p>}
    {!data ? <p>Loading history…</p> : data.items.length === 0 ? <p>No crawl jobs yet.</p> :
      <table><thead><tr><th>URL</th><th>Status</th><th>Created</th><th>Progress</th></tr></thead><tbody>
        {data.items.map(job => <tr key={job.jobId}><td><a href={`#/jobs/${job.jobId}`}>{job.url}</a></td><td>{job.status}</td><td>{new Date(job.createdAt).toLocaleString()}</td><td>{job.pagesCrawled} pages</td></tr>)}
      </tbody></table>}
  </section>;
}

function Details({ id }) {
  const [job, setJob] = useState(null);
  const [tree, setTree] = useState([]);
  const [error, setError] = useState("");
  const [canceling, setCanceling] = useState(false);

  async function cancelJob() {
    setCanceling(true);
    setError("");
    try {
      const response = await fetch(`${api}/api/jobs/${id}/cancel`, { method: "POST" });
      if (!response.ok) throw new Error(response.status === 409
        ? "This job can no longer be canceled."
        : "Could not cancel the job.");
      const updated = await fetch(`${api}/api/jobs/${id}`);
      if (!updated.ok) throw new Error("Could not refresh job status.");
      setJob(await updated.json());
    } catch (e) { setError(e.message); }
    finally { setCanceling(false); }
  }

  useEffect(() => {
    let active = true;
    async function refresh() {
      try {
        const response = await fetch(`${api}/api/jobs/${id}`);
        if (!response.ok) throw new Error(response.status === 404 ? "Job not found." : "Could not load job.");
        const current = await response.json();
        if (!active) return;
        setJob(current);
        setError("");
        if (current.status === "Completed" || current.status === "Failed" || current.status === "Canceled") {
          const result = await fetch(`${api}/api/jobs/${id}/tree`);
          if (result.ok && active) setTree(await result.json());
          return;
        }
      } catch (e) { if (active) setError(e.message); }
    }
    refresh();
    const timer = setInterval(refresh, 2000);
    return () => { active = false; clearInterval(timer); };
  }, [id]);
  return <section><h2>Job details</h2>{error && <p className="error">{error}</p>}
    {!job ? !error && <p>Loading job…</p> : <>
      <dl><dt>Job ID</dt><dd>{job.jobId}</dd><dt>URL</dt><dd>{job.url}</dd><dt>Status</dt><dd>{job.status}</dd>
        <dt>Pages crawled</dt><dd>{job.pagesCrawled}</dd><dt>Created</dt><dd>{new Date(job.createdAt).toLocaleString()}</dd>
        <dt>Started</dt><dd>{job.startedAt ? new Date(job.startedAt).toLocaleString() : "—"}</dd>
        <dt>Completed</dt><dd>{job.completedAt ? new Date(job.completedAt).toLocaleString() : "—"}</dd>
      </dl>
      {job.status === "Running" || job.status === "Pending" ? <progress max="200" value={Math.min(job.pagesCrawled, 200)} /> : null}
      {job.status === "Running" || job.status === "Pending"
        ? <button onClick={cancelJob} disabled={canceling}>{canceling ? "Canceling…" : "Cancel crawl"}</button>
        : null}
      {job.failureReason && <p className="error">{job.failureReason}</p>}
      {job.status === "Completed" && <><h3>Discovered pages</h3>{tree.length ? tree.map(node => <Tree key={node.url} node={node} />) : <p>No HTML pages discovered.</p>}</>}
      {job.status === "Canceled" && <><h3>Pages found before cancellation</h3>{tree.length ? tree.map(node => <Tree key={node.url} node={node} />) : <p>No HTML pages discovered.</p>}</>}
    </>}
  </section>;
}

function Tree({ node }) {
  return <ul><li><a href={node.url} target="_blank" rel="noreferrer">{node.url}</a> <span>Domain link ratio: {(node.domainLinkRatio * 100).toFixed(1)}%</span>
    {node.children?.map(child => <Tree key={child.url} node={child} />)}</li></ul>;
}

export default App;
