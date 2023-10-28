import { readable, writable, derived } from 'svelte/store';
import { base } from '$app/paths';
import type { FirmwareManifest, Release, WorkflowRun } from '$lib/types';

export const updateMethod: SvelteStore<string> = writable("self");
export const flavor: SvelteStore<string> = writable();
export const version: SvelteStore<string> = writable();
export const artifact: SvelteStore<string> = writable();

export const firmwareTypes = writable<FirmwareManifest|null>(null, function start(set) {
  fetch(`${base}/api/firmware/types`).then(r => r.json()).then(r => set(r));
});

export const cpuNames = derived(firmwareTypes, a => a?.cpus?.reduce((acc, cur) => {
  acc.set(cur.value,cur.name);
  return acc;
}, new Map<string, string>()));

export const flavorNames = derived(firmwareTypes, a => a?.flavors?.reduce((acc, cur) => {
  acc.set(cur.value,cur.name);
  return acc;
}, new Map<string, string>()));

export const artifacts = readable<Map<string, WorkflowRun[]>>(new Map(), function start(set) {
  let errors = 0;
  let outstanding = false;

  async function fetchData() {
    try {
      const res = await fetch("https://api.github.com/repos/ESPresense/ESPresense/actions/workflows/build.yml/runs?status=success&per_page=100", { credentials: "same-origin" });
      const data: { workflow_runs: WorkflowRun[] } = await res.json();
      const wf = data.workflow_runs.filter(i => i.pull_requests.length > 0 && i.head_repository.full_name === "ESPresense/ESPresense");

      set(wf.reduce((p: Map<string, WorkflowRun[]>, c) => {
        if (p.has(c.head_branch)) {
          p.get(c.head_branch)?.push(c);
        } else {
          p.set(c.head_branch, [c]);
        }
        return p;
      }, new Map<string, WorkflowRun[]>()));

      errors = 0;
      outstanding = false;
    } catch (ex) {
      outstanding = false;
      if (++errors > 5) set(new Map<string, WorkflowRun[]>());
      console.log(ex);
    }
  }

  const interval = setInterval(() => {
    if (outstanding) return;
    outstanding = true;
    fetchData();
  }, 60000);

  fetchData();

  return function stop() {
    clearInterval(interval);
  };
});

export const releases = readable<Map<string, Release[]>>(new Map(), function start(set) {
  let errors = 0;
  let outstanding = false;

  async function fetchData() {
    try {
      const res = await fetch('https://api.github.com/repos/ESPresense/ESPresense/releases', { credentials: 'same-origin' });
      const data: Release[] = await res.json();

      const response = data
        .filter(i => i.assets.length > 5)
        .reduce((p: Map<string, Release[]>, c) => {
          const key = c.prerelease ? 'Beta' : 'Release';
          p.get(key) ? p.get(key)?.push(c) : p.set(key, [c]);
          return p;
        }, new Map<string, Release[]>());

      set(response);

      errors = 0;
      outstanding = false;
    } catch (ex) {
      outstanding = false;
      if (++errors > 5) set(new Map<string, Release[]>());
      console.log(ex);
    }
  }

  const interval = setInterval(() => {
    if (outstanding) return;
    outstanding = true;
    fetchData();
  }, 15 * 60000);

  fetchData();

  return function stop() {
    clearInterval(interval);
  };
});

export function getFirmwareUrl(updateMethod: string, version: string, artifact: string, firmware: string): string {
  if (firmware)
    switch (updateMethod) {
      case "artifact":
        if (artifact) return `https://nightly.link/ESPresense/ESPresense/actions/runs/${artifact}/${firmware}.zip`;
      case "release":
        if (version) return `https://github.com/ESPresense/ESPresense/releases/download/${version}/${firmware}`;
      default:
    }
  return "#ERR";
}

type Callback = (percentComplete: number, message: string) => void;

export async function firmwareUpdate(id: string, url: string, callback: Callback): Promise<void> {
  const response = await fetch(`${base}/api/firmware/update/${id}?` + new URLSearchParams({ url: url }), { method: 'PUT' });
  if (!response.ok) {
    throw new Error(`HTTP Error! Status: ${response.status}`);
  }

  const reader = response.body?.getReader();
  if (!reader) {
    return;
  }

  let buffer = '';

  while (true) {
    const { done, value } = await reader.read();
    if (done) {
      break;
    }

    buffer += new TextDecoder("utf-8").decode(value);

    let boundary = buffer.lastIndexOf('\n');
    if (boundary === -1) {
      continue;
    }

    const completeData = buffer.slice(0, boundary);
    const lines = completeData.split('\n');

    buffer = buffer.slice(boundary + 1);

    lines.forEach(line => {
      if (line) {
        try {
          const json = JSON.parse(line);
          const { percentComplete, message } = json;
          callback(percentComplete, message);
        } catch (e) {
          console.error('Could not parse line:', line);
        }
      }
    });
  }
}

