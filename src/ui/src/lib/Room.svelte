<script lang="ts">
	import { polygonCentroid } from 'd3';
	import { getLayerCakeContext } from 'layercake';
	import type { MapScales, Room } from '#lib/types.js';
	import { config } from '#lib/stores.js';
	import { getRoomColor } from '#lib/colors.js';

	const chart = getLayerCakeContext<MapScales>();

	interface Props {
		room: Room;
	}

	let { room }: Props = $props();
	let wallThickness = $derived($config?.map?.wallThickness ?? 0);
	// Use shared color util so 2D/3D match and config overrides apply

	// Calculate the scaled stroke width based on the wall thickness
	let scaledStrokeWidth = $derived(wallThickness == 0 ? 1 : Math.abs(chart.xScale(wallThickness) - chart.xScale(0)));
	let centroid = $derived(polygonCentroid(room.points));
	let scaledRoom = $derived(room.points.map((p) => [chart.xScale(p[0]), chart.yScale(p[1])]));
	let baseColor = $derived(getRoomColor($config, room.id));
	let wallColor = $derived($config?.map?.wallColor ?? baseColor);
	let wallOpacity = $derived($config?.map?.wallOpacity ?? 0.35);
</script>

<path d={`M${scaledRoom.join('L')}Z`} fill={`url(#${room.id})`} fill-opacity="0.25" stroke={wallColor} stroke-opacity={wallOpacity} stroke-width={scaledStrokeWidth} stroke-linejoin="miter-clip" />

<linearGradient id={room.id} x1="0%" y1="0%" x2="100%" y2="100%">
	<stop offset="0.0%" stop-color={`${baseColor}0F`} />
	<stop offset="100.0%" stop-color={`${baseColor}FF`} />
</linearGradient>

<text dominant-baseline="middle" text-anchor="middle" x={chart.xScale(centroid[0])} y={chart.yScale(centroid[1])} fill="white" font-size="10px">
	{room.name}
</text>
