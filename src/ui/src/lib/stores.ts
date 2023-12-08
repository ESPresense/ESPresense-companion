import { readable, writable, derived } from 'svelte/store';
import { base } from '$app/paths';
import type { Device, Config } from './types';

export const showAll: SvelteStore<boolean> = writable(false);
export const config = writable<Config>();

let socket: WebSocket;

export const history = writable(['/']);

async function getConfig() {
  const response = await fetch(`${base}/api/state/config`);
  config.set(await response.json());
}

getConfig();

export const devices = readable<Device[]>([], function start(set) {
  let deviceMap = new Map();

  function updateDevicesFromMap() {
    var a = Array.from(deviceMap.values())
    set(a);
  }

  function fetchDevices() {
    fetch(`${base}/api/state/devices`)
      .then(d => d.json())
      .then(r => {
        deviceMap = new Map(r.map(device => [device.id, device]));
        updateDevicesFromMap();
      })
      .catch((ex) => {
        console.log(ex);
      });
  }

  fetchDevices();

  const interval = setInterval(() => {
    fetchDevices();
  }, 60000);

  function setupWebsocket() {
    var loc = new URL(`${base}/ws`, window.location.href);
    var new_uri = (loc.protocol === "https:" ? "wss:" : "ws:") + "//" + loc.host + loc.pathname;
    socket = new WebSocket(new_uri);
    socket.addEventListener('message', async function (event) {
      var eventData = JSON.parse(event.data);
      if (eventData.type === "deviceChanged" && eventData.data?.id) {
        deviceMap.set(eventData.data.id, eventData.data);
        updateDevicesFromMap();
      } else if (eventData.type == "configChanged") {
        getConfig();
      } else
        console.log(event.data);
    });
  }

  setupWebsocket();

  return function stop() {
    clearInterval(interval);
  };
});

export const nodes = readable<Node[]>([], function start(set) {
  var errors = 0;
  var outstanding = false;
  const interval = setInterval(() => {
    if (outstanding) return;
    outstanding = true;
    fetch(`${base}/api/state/nodes?includeTele=true`)
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

export const calibration = readable({}, function start(set) {
  async function fetchAndSet() {
    const response = await fetch(`${base}/api/state/calibration`);
    var data = await response.json();
    set(data);
  }

  fetchAndSet()
  const interval = setInterval(() => {
    fetchAndSet();
  }, 1000);

  return function stop() {
    clearInterval(interval);
  };
});
