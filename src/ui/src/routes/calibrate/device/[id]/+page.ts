import { error } from '@sveltejs/kit';
import type { PageLoad } from './$types';

export const load: PageLoad = async ({ params, url, route }) => {
	const { id } = params;
	
	if (!id || id === 'undefined') {
		throw error(404, 'Device ID is required');
	}

	return {
		deviceId: id
	};
};