import { apiUrl } from '$lib/api';
import type { LoadEvent } from '@sveltejs/kit';

export async function load({ fetch, params }: LoadEvent) {
	return await fetch(apiUrl(`/api/device/${params.id}`))
		.then((response) => {
			if (!response.ok) throw new Error(response.statusText);
			return response.json();
		})
		.catch((e: unknown) => {
			const error = e as Error;
			return {
				settings: {
					originalId: params.id,
					id: params.id,
					name: null,
					'rssi@1m': null,
					error: error.message
				}
			};
		});
}
