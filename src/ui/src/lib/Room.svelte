<script lang="ts">
	import { getContext } from 'svelte';
	import { polygonCentroid, type ScaleOrdinal } from 'd3';
	import type { LayerCakeContext, Room, RoomProps } from '$lib/types';

	const { xScale, yScale } = getContext<LayerCakeContext>('LayerCake');

	export let room: Room;
	export let wallThickness: number = 0.1;
	const colors = getContext<ScaleOrdinal<string, string>>('colors');

	// Calculate the scaled stroke width based on the wall thickness
	$: scaledStrokeWidth = Math.abs($xScale(wallThickness) - $xScale(0));
	$: centroid = polygonCentroid(room.points);
	$: scaledRoom = room.points.map((p) => [$xScale(p[0]), $yScale(p[1])]);
</script>

<g>
	<!-- Render room with wall thickness as stroke -->
	<path
		d={`M${scaledRoom.join('L')}Z`}
		fill={`url(#${room.id})`}
		fill-opacity="0.25"
		stroke={colors(room.id)}
		stroke-opacity="0.9"
		stroke-width={scaledStrokeWidth}
		stroke-linejoin="round"
	/>

	<!-- Room gradient definition -->
	<linearGradient id={room.id} x1="0%" y1="0%" x2="100%" y2="100%">
		<stop offset="0.0%" stop-color="{colors(room.id)}0F" />
		<stop offset="100.0%" stop-color="{colors(room.id)}FF" />
	</linearGradient>

	<!-- Room label -->
	<text
		dominant-baseline="middle"
		text-anchor="middle"
		x={$xScale(centroid[0])}
		y={$yScale(centroid[1])}
		fill="white"
		font-size="10px"
	>
		{room.name}
	</text>
</g>
