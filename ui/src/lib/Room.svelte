<script lang="ts">
  import { getContext } from 'svelte';
  import { polygonCentroid } from "d3";
  import type { Room } from '$lib/types';

  const { xScale, yScale } = getContext('LayerCake');

  export let room:Room;
  let colors = getContext('colors');

  $: centroid = polygonCentroid(room.points);
  $: scaledHull = room.points?.map((p) => {
      return [$xScale(p[0]),$yScale(p[1])];
  });
</script>

<linearGradient id="{room.id}" x1="0%" y1="0%" x2="100%" y2="100%">
    <stop offset="0.0%" stop-color="{colors(room.id)}00"></stop>
    <stop offset="100.0%" stop-color="{colors(room.id)}FF"></stop>
</linearGradient>

<path stroke='white' style:fill={ "url(#" + room.id + ")" } fill-opacity="0.25" d={`M${scaledHull.join("L")}Z`} />
<text dominant-baseline='middle' text-anchor='middle' x='{ $xScale(centroid[0]) }' y='{ $yScale(centroid[1]) }' fill='white' font-size='10px'>{room.name}</text>