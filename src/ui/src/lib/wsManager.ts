import { base } from '$app/paths';
import type { DeviceMessage } from './types';

type EventCallback<T = any> = (data: T) => void;

interface Listeners {
  deviceChanged: Set<EventCallback>;
  nodeChanged: Set<EventCallback>;
  deviceMessage: Set<EventCallback>;
  configChanged: Set<EventCallback>;
  time: Set<EventCallback>;
}

export class WSManager {
  private listeners: Listeners;
  private socket: WebSocket | null = null;
  private pendingSubscriptions: Set<string> = new Set();

  constructor() {
    this.listeners = {
      deviceChanged: new Set(),
      nodeChanged: new Set(),
      deviceMessage: new Set(),
      configChanged: new Set(),
      time: new Set()
    };
    this.connect();
  }

  private connect() {
    const loc = new URL(`${base}/ws`, window.location.href);
    const protocol = loc.protocol === 'https:' ? 'wss:' : 'ws:';
    const newUri = protocol + '//' + loc.host + loc.pathname + loc.search;

    this.socket = new WebSocket(newUri);

    this.socket.addEventListener('open', () => {
      // Flush any pending device message subscriptions
      this.pendingSubscriptions.forEach((deviceId) => {
        this.socket!.send(JSON.stringify({
          command: 'subscribe',
          type: 'deviceMessage',
          value: deviceId
        }));
      });
      this.pendingSubscriptions.clear();
    });

    this.socket.addEventListener('message', (event: MessageEvent) => {
      let eventData: any;
      try {
        eventData = JSON.parse(event.data);
      } catch (err) {
        console.error('Error parsing message:', err);
        return;
      }

      // Dispatch events based on the type
      switch (eventData.type) {
        case 'deviceChanged':
          this.listeners.deviceChanged.forEach(cb => cb(eventData.data));
          break;
        case 'nodeChanged':
          this.listeners.nodeChanged.forEach(cb => cb(eventData.data));
          break;
        case 'deviceMessage':
          this.listeners.deviceMessage.forEach(cb => cb(eventData));
          break;
        case 'configChanged':
          this.listeners.configChanged.forEach(cb => cb(eventData.data));
          break;
        case 'time':
          this.listeners.time.forEach(cb => cb(eventData.data));
          break;
        default:
          console.log('Unhandled websocket event:', eventData);
      }
    });

    this.socket.addEventListener('close', () => {
      console.warn('WebSocket closed. Consider implementing reconnection logic if needed.');
      // Optionally, implement reconnection logic here.
    });

    this.socket.addEventListener('error', (error) => {
      console.error('WebSocket encountered error:', error);
    });
  }

  public subscribeToEvent(eventType: keyof Listeners, callback: EventCallback) {
    if (this.listeners[eventType]) {
      this.listeners[eventType].add(callback);
    } else {
      console.warn(`No handler for event type "${eventType}"`);
    }
  }

  public unsubscribeFromEvent(eventType: keyof Listeners, callback: EventCallback) {
    if (this.listeners[eventType]) {
      this.listeners[eventType].delete(callback);
    }
  }

  public subscribeDeviceMessage(deviceId: string) {
    if (this.socket && this.socket.readyState === WebSocket.OPEN) {
      this.socket.send(JSON.stringify({
        command: 'subscribe',
        type: 'deviceMessage',
        value: deviceId
      }));
    } else {
      this.pendingSubscriptions.add(deviceId);
    }
  }

  public sendMessage(message: any) {
    if (this.socket && this.socket.readyState === WebSocket.OPEN) {
      this.socket.send(JSON.stringify(message));
    } else {
      console.warn('Cannot send message, socket is not connected.');
    }
  }
}
