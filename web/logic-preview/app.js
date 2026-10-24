"use strict";
const $ = id => document.getElementById(id);
const token = document.querySelector('meta[name="preview-token"]').content;
let currentJob = null, cursor = 0, timer = null, nativeReady = false;
const running = status => ["scanning", "rendering", "cancelling"].includes(status);
function notice(message = "") { $("notice").textContent = message; $("notice").hidden = !message; }
async function api(path, payload) {
  const response = await fetch(path, payload === undefined ? {} : {
    method: "POST", headers: {"Content-Type": "application/json", "X-Preview-Token": token}, body: JSON.stringify(payload)
  });
  const data = await response.json();
  if (!response.ok) throw new Error(data.error || "The request could not be completed.");
  return data;
}
function busy(value) {
  $("render").disabled = value || !nativeReady;
  $("cancel").hidden = !value;
  document.querySelector(".monitor").classList.toggle("running", value);
}
function addResult(entry, index) {
  const row = document.createElement("tr");
  const patch = document.createElement("td"), status = document.createElement("td"), listen = document.createElement("td");
  patch.textContent = entry.relative_source;
  patch.title = entry.source;
  const detail = document.createElement("small");
  detail.textContent = entry.message || ((entry.result?.approximations || []).join("\n"));
  patch.append(detail);
  const badge = document.createElement("span");
  badge.className = "badge " + entry.status;
  badge.textContent = {rendered: "Saved", skipped_existing: "Existing", failed: "Failed", cancelled: "Stopped"}[entry.status] || entry.status;
  status.append(badge);
  if (["rendered", "skipped_existing"].includes(entry.status)) {
    const url = `/api/audio/${currentJob}/${index}`;
    const audio = document.createElement("audio");
    audio.controls = true; audio.preload = "none"; audio.src = url;
    audio.addEventListener("play", () => document.querySelectorAll("audio").forEach(other => { if (other !== audio) other.pause(); }));
    const link = document.createElement("a");
    link.href = url; link.download = entry.relative_source.replace(/\.patch$/i, ".wav"); link.textContent = "Download WAV";
    link.title = entry.output;
    listen.append(audio, link);
  } else { listen.textContent = "—"; }
  row.append(patch, status, listen); $("results").append(row);
  $("empty").hidden = true; $("results-table").hidden = false;
}
async function poll() {
  try {
    const job = await api(`/api/jobs/${currentJob}?after=${cursor}`);
    const active = running(job.status);
    busy(active);
    $("job-status").textContent = {scanning: "Scanning", rendering: "Rendering", cancelling: "Stopping", completed: "Finished", completed_with_errors: "Finished with errors", cancelled: "Stopped", failed: "Failed"}[job.status] || job.status;
    $("job-message").textContent = job.message;
    $("current").textContent = job.current;
    $("rendered").textContent = job.rendered; $("existing").textContent = job.skipped_existing;
    $("failed").textContent = job.failed + job.discovery_issues.filter(issue => issue.status === "discovery_failed").length;
    $("saved-folder").textContent = job.output;
    if (job.total === null) $("progress").removeAttribute("value");
    else { $("progress").max = Math.max(1, job.total); $("progress").value = job.processed; }
    $("progress-label").textContent = job.total === null ? "Finding your patches…" : `${job.processed} of ${job.total} patches processed${job.unrun ? ` · ${job.unrun} unrun` : ""}`;
    job.results.forEach((entry, index) => addResult(entry, cursor + index));
    cursor = job.result_count;
    $("result-count").textContent = `${cursor} results`;
    $("scan-notes").hidden = !job.discovery_issues.length;
    $("scan-list").replaceChildren(...job.discovery_issues.map(issue => { const item = document.createElement("li"); item.textContent = `${issue.source}: ${issue.message}`; return item; }));
    if (job.status === "failed") notice(job.message);
    if (active) timer = setTimeout(poll, 750);
  } catch (error) { notice(error.message); timer = setTimeout(poll, 2000); }
}
document.querySelectorAll("[data-browse]").forEach(button => button.addEventListener("click", async () => {
  button.disabled = true; notice();
  try { const result = await api("/api/browse", {kind: button.dataset.browse}); if (result.path) $("source").value = result.path; }
  catch (error) { notice(error.message); }
  finally { button.disabled = false; }
}));
$("render-form").addEventListener("submit", async event => {
  event.preventDefault(); notice(); busy(true);
  const payload = {source: $("source").value, output: $("output").value || null, au_chain_only: $("chain-only").checked,
    notes: $("notes").value, hold: Number($("hold").value), tail: Number($("tail").value),
    velocity: Number($("velocity").value), bpm: Number($("bpm").value), sample_rate: Number($("sample-rate").value)};
  try {
    const response = await api("/api/jobs", payload);
    clearTimeout(timer); currentJob = response.id; cursor = 0;
    $("results").replaceChildren(); $("results-table").hidden = true; $("empty").hidden = false;
    await poll();
  } catch (error) { notice(error.message); busy(false); }
});
$("cancel").addEventListener("click", async () => {
  try { await api(`/api/jobs/${currentJob}/cancel`, {}); $("job-message").textContent = "Stopping the current render…"; }
  catch (error) { notice(error.message); }
});
api("/api/status").then(status => {
  nativeReady = status.native_ready;
  $("connection").textContent = nativeReady ? "Local · ready" : "Local · renderer unavailable";
  $("output").placeholder = status.default_output + "/library-name";
  busy(false);
  if (!nativeReady) notice("To render audio, open Start-LogicPreview-Web.command on your Mac. This page can still be viewed here.");
  if (status.active_job) { currentJob = status.active_job; poll(); }
}).catch(error => notice(error.message));
