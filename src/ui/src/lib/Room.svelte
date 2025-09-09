<script lang="ts">
	import { getContext } from 'svelte';
	import { polygonCentroid } from 'd3';
	import type { LayerCakeContext, Room } from '$lib/types';
	import { config } from '$lib/stores';
	import { getRoomColor } from '$lib/colors';

	const { xScale, yScale } = getContext<LayerCakeContext>('LayerCake');

	export let room: Room;
	// Use shared color util so 2D/3D match and config overrides apply

	// Calculate the scaled stroke width based on the wall thickness
	$: scaledStrokeWidth = wallThickness == 0 ? 1 : Math.abs($xScale(wallThickness) - $xScale(0));
	$: centroid = polygonCentroid(room.points);
	$: scaledRoom = room.points.map((p) => [$xScale(p[0]), $yScale(p[1])]);
	$: baseColor = getRoomColor($config, room.id);
	$: wallColor = $config?.map?.wallColor ?? baseColor;
	$: wallOpacity = $config?.map?.wallOpacity ?? 0.35;
	$: wallThickness = $config?.map?.wallThickness ?? 0;
</script>

<path d={`M${scaledRoom.join('L')}Z`} fill={`url(#${room.id})`} fill-opacity="0.25" stroke={wallColor} stroke-opacity={wallOpacity} stroke-width={scaledStrokeWidth} stroke-linejoin="miter-clip" />

<linearGradient id={room.id} x1="0%" y1="0%" x2="100%" y2="100%">
	<stop offset="0.0%" stop-color={`${baseColor}0F`} />
	<stop offset="100.0%" stop-color={`${baseColor}FF`} />
</linearGradient>

<text dominant-baseline="middle" text-anchor="middle" x={$xScale(centroid[0])} y={$yScale(centroid[1])} fill="white" font-size="10px">
	{room.name}
</text>
