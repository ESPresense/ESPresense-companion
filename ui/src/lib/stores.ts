import { readable } from 'svelte/store';
import { writable } from 'svelte/store';
import { base } from '$app/paths';
import type { Config, Node, Device } from './types';

export const selected = writable<Device | null>();

export const showAll: SvelteStore<boolean> = writable(false);

export const config = writable<Config>();

let socket: WebSocket;

async function getConfig() {
  const response = await fetch(`${base}/api/state/config`);
  config.set(await response.json());
}
getConfig();

function setupWebsocket() {
  var loc = new URL(`${base}/ws`, window.location.href);
  var new_uri = (loc.protocol === "https:" ? "wss:" : "ws:") + "//" + loc.host + loc.pathname;
  socket = new WebSocket(new_uri);
  socket.addEventListener('message', async function (event) {
    console.log(event.data);
    var eventData = JSON.parse(event.data);
    if (eventData.type == "configChanged") {
      getConfig();
    }
  });
}

setupWebsocket();

export const devices = readable<Device[]>([], function start(set) {
  var errors = 0;
  var outstanding = false;
  const interval = setInterval(() => {
    if (outstanding) return;
    outstanding = true;
    fetch(`${base}/api/state/devices`)
      .then(d => d.json())
      .then(r => {
        outstanding = false;
        errors = 0;
        set(r);
      })
      .catch((ex) => {
        outstanding = false;
        if (errors > 5) set([]);
        console.log(ex);
      });
  }, 1000)

  return function stop() {
    clearInterval(interval);
  };
});

