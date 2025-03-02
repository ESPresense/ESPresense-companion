import { base } from '$app/paths';
import type { LoadEvent } from '@sveltejs/kit';

export async function load({ fetch, params }: LoadEvent) {
	return await fetch(`${base}/api/device/${params.id}`)
		.then((response) => {
			if (response.status != 200) throw new Error(response.statusText);
			var data = response.json();
			return data;
		})
		.catch((e: unknown) => {
			const error = e as Error;
			return { settings: { originalId: params.id, id: params.id, name: null, 'rssi@1m': null, error: error.message } };
		});
}
