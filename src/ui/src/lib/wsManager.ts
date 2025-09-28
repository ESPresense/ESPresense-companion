import { resolve } from '$app/paths';
import type { DeviceMessage } from './types';

type EventCallback<T = any> = (data: T) => void;

interface Listeners {
	deviceChanged: Set<EventCallback>;
	deviceRemoved: Set<EventCallback>;
	nodeChanged: Set<EventCallback>;
	deviceMessage: Set<EventCallback>;
	configChanged: Set<EventCallback>;
	time: Set<EventCallback>;
}

export class WSManager {
	private listeners: Listeners;
	private socket: WebSocket | null = null;
	private pendingSubscriptions: Set<string> = new Set();
	private reconnectAttempts: number = 0;
	private reconnectTimer: number | null = null;
	private readonly baseReconnectDelayMs: number = 1000; // 1 second
	private readonly maxReconnectDelayMs: number = 30000; // 30 seconds

	constructor() {
		this.listeners = {
			deviceChanged: new Set(),
			deviceRemoved: new Set(),
			nodeChanged: new Set(),
			deviceMessage: new Set(),
			configChanged: new Set(),
			time: new Set()
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

			// Flush any pending device message subscriptions
			this.pendingSubscriptions.forEach((deviceId) => {
				this.socket!.send(
					JSON.stringify({
						command: 'subscribe',
						type: 'deviceMessage',
						value: deviceId
					})
				);
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
					this.listeners.deviceChanged.forEach((cb) => cb(eventData.data));
					break;
				case 'nodeChanged':
					this.listeners.nodeChanged.forEach((cb) => cb(eventData.data));
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

	public subscribeDeviceMessage(deviceId: string) {
		if (this.socket && this.socket.readyState === WebSocket.OPEN) {
			this.socket.send(
				JSON.stringify({
					command: 'subscribe',
					type: 'deviceMessage',
					value: deviceId
				})
			);
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
