import { apiUrl } from '$lib/api';

export async function load({ fetch, params }) {
	return await fetch(apiUrl(`/api/node/${params.id}`))
		.then((response) => {
			if (!response.ok) throw new Error(response.statusText);
			var data = response.json();
			return data;
		})
		.catch((e) => {
			return { settings: { id: params.id, name: null, error: e } };
		});
}
