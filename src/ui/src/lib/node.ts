import { base } from '$app/paths';
import type { Settings, Node, NodeSetting } from './types';

export async function loadSettings(id: string): Promise<Settings> {
    const response = await fetch(`${base}/api/node/${id}/settings`);
    if (!response.ok) throw new Error("Something went wrong loading settings (error="+response.status+" "+response.statusText+")");
    return await response.json();
}

export async function saveSettings(newSettings: Settings): Promise<Settings> {
    const response = await fetch(`${base}/api/node/${newSettings.id}/settings`, {
        method: 'POST',
        headers: {
            'Content-Type': 'application/json',
        },
        body: JSON.stringify(newSettings),
    });
    if (!response.ok) throw new Error("Something went wrong loading settings (error="+response.status+" "+response.statusText+")");
    return await response.json();
}

export async function restartNode(nodeId: string): Promise<void> {
    const response = await fetch(`${base}/api/node/${nodeId}/restart`, { method: 'POST' });
    if (!response.ok) throw new Error(response.statusText);
}

export async function updateNodeSelf(nodeId: string): Promise<void> {
    const response = await fetch(`${base}/api/node/${nodeId}/update`, { method: 'POST' });
    if (!response.ok) throw new Error(response.statusText);
}

export async function saveNodeSettings(nodeId: string, settings: NodeSetting): Promise<void> {
    const response = await fetch(`${base}/api/node/${nodeId}/settings`, {
        method: 'PUT',
        headers: {
            'Content-Type': 'application/json'
        },
        body: JSON.stringify(settings)
    });
    if (!response.ok) throw new Error(response.statusText);
}

export async function fetchNode(nodeId: string): Promise<Node> {
    const response = await fetch(`${base}/api/node/${nodeId}/settings`);
    if (!response.ok) throw new Error(response.statusText);
    return await response.json();
}