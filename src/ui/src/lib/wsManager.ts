import { resolve } from '$app/paths';
import type { DeviceMessage } from './types';

type EventCallback<T = any> = (data: T) => void;

interface Listeners {
	deviceChanged: Set<EventCallback>;
	deviceRemoved: Set<EventCallback>;
	nodeStateChanged: Set<EventCallback>;
	calibrationChanged: Set<EventCallback>;
	deviceMessage: Set<EventCallback>;
	configChanged: Set<EventCallback>;
	time: Set<EventCallback>;
	locatorStateChanged: Set<EventCallback>;
}

export class WSManager {
	private listeners: Listeners;
	private socket: WebSocket | null = null;
	private pendingSubscriptions: Set<string> = new Set();
	private activeSubscriptions: Set<string> = new Set();
	private openListeners: Set<() => void> = new Set();
	private reconnectAttempts: number = 0;
	private reconnectTimer: number | null = null;
	private readonly baseReconnectDelayMs: number = 1000; // 1 second
	private readonly maxReconnectDelayMs: number = 30000; // 30 seconds

	constructor() {
		this.listeners = {
			deviceChanged: new Set(),
			deviceRemoved: new Set(),
			nodeStateChanged: new Set(),
			calibrationChanged: new Set(),
			deviceMessage: new Set(),
			configChanged: new Set(),
			time: new Set(),
			locatorStateChanged: new Set()
		};
		this.connect();
	}

	private scheduleReconnect() {
		// Prevent duplicate timers
		if (this.reconnectTimer !== null) {
			return;
		}

		// Calculate exponential backoff delay with jitter
		const exponentialDelay = Math.min(
			this.baseReconnectDelayMs * Math.pow(2, this.reconnectAttempts),
			this.maxReconnectDelayMs
		);

		// Add jitter (±25% of the delay)
		const jitter = exponentialDelay * 0.25 * (Math.random() * 2 - 1);
		const delay = Math.max(0, exponentialDelay + jitter);

		console.log(`Scheduling WebSocket reconnection in ${Math.round(delay)}ms (attempt ${this.reconnectAttempts + 1})`);

		this.reconnectTimer = window.setTimeout(() => {
			this.reconnectTimer = null;
			this.connect();
		}, delay);
	}

	private connect() {
		const loc = new URL(resolve('/ws'), window.location.href);
		const protocol = loc.protocol === 'https:' ? 'wss:' : 'ws:';
		const newUri = protocol + '//' + loc.host + loc.pathname + loc.search;

		this.socket = new WebSocket(newUri);

		this.socket.addEventListener('open', () => {
			console.log('WebSocket connected successfully');

			// Reset reconnection attempts on successful connection
			this.reconnectAttempts = 0;

			// (Re)send device message subscriptions: pending ones plus any that were
			// active before a reconnect, otherwise the feed silently stops after a drop.
			const toSend = new Set([...this.activeSubscriptions, ...this.pendingSubscriptions]);
			toSend.forEach((deviceId) => this.sendSubscription('subscribe', deviceId));
			this.pendingSubscriptions.clear();

			this.openListeners.forEach((cb) => cb());
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
					this.listeners.deviceChanged.forEach((cb) => cb(eventData.data));
					break;
				case 'nodeStateChanged':
					this.listeners.nodeStateChanged.forEach((cb) => cb(eventData.data));
					break;
				case 'calibrationChanged':
					this.listeners.calibrationChanged.forEach((cb) => cb(eventData.data));
					break;
				case 'deviceMessage':
					this.listeners.deviceMessage.forEach((cb) => cb(eventData));
					break;
				case 'configChanged':
					this.listeners.configChanged.forEach((cb) => cb(eventData.data));
					break;
				case 'time':
					this.listeners.time.forEach((cb) => cb(eventData.data));
					break;
				case 'deviceRemoved':
					this.listeners.deviceRemoved.forEach((cb) => cb(eventData));
					break;
				case 'locatorStateChanged':
					this.listeners.locatorStateChanged.forEach((cb) => cb(eventData.data));
					break;
				default:
					console.log('Unhandled websocket event:', eventData);
			}
		});

		this.socket.addEventListener('close', () => {
			console.warn('WebSocket closed, cleaning up and scheduling reconnection');

			// Clear stale socket reference
			this.socket = null;

			// Increment reconnection attempts and schedule reconnection
			this.reconnectAttempts++;
			this.scheduleReconnect();
		});

		this.socket.addEventListener('error', (error) => {
			console.error('WebSocket encountered error:', error);

			// Close and cleanup the socket to avoid stale references
			if (this.socket) {
				this.socket.close();
				this.socket = null;
			}
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

	private sendSubscription(command: 'subscribe' | 'unsubscribe', deviceId: string) {
		this.socket?.send(JSON.stringify({ command, type: 'deviceMessage', value: deviceId }));
	}

	public subscribeDeviceMessage(deviceId: string) {
		this.activeSubscriptions.add(deviceId);
		if (this.socket && this.socket.readyState === WebSocket.OPEN) {
			this.sendSubscription('subscribe', deviceId);
		} else {
			this.pendingSubscriptions.add(deviceId);
		}
	}

	public unsubscribeDeviceMessage(deviceId: string) {
		this.activeSubscriptions.delete(deviceId);
		this.pendingSubscriptions.delete(deviceId);
		if (this.socket && this.socket.readyState === WebSocket.OPEN) {
			this.sendSubscription('unsubscribe', deviceId);
		}
	}

	/** Registers a callback invoked every time the socket (re)connects. Returns an unsubscribe function. */
	public onOpen(callback: () => void): () => void {
		this.openListeners.add(callback);
		return () => this.openListeners.delete(callback);
	}

	public sendMessage(message: any) {
		if (this.socket && this.socket.readyState === WebSocket.OPEN) {
			this.socket.send(JSON.stringify(message));
		} else {
			console.warn('Cannot send message, socket is not connected.');
		}
	}

	public disconnect() {
		console.log('Manually disconnecting WebSocket');

		// Clear any pending reconnection timer
		if (this.reconnectTimer !== null) {
			window.clearTimeout(this.reconnectTimer);
			this.reconnectTimer = null;
		}

		// Close and null the socket to stop auto-reconnect
		if (this.socket) {
			this.socket.close();
			this.socket = null;
		}

		// Reset reconnection attempts
		this.reconnectAttempts = 0;
	}
}
