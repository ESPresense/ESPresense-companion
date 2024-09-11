import { error } from '@sveltejs/kit';
import type { PageLoad } from './$types';
import { fetchDeviceSettings } from '$lib/device'

export const load: PageLoad = async ({ fetch, params }) => {
	if (!params.id) {
		throw error(400, 'No device id');
	}
	try {
		var settings = fetchDeviceSettings(fetch, params.id);
		return { id: params.id, settings: settings };
	}
	catch (e) {
		return { settings: { originalId: params.id, id: null, name: null, 'rssi@1m': null, error: e } };
	}
};
