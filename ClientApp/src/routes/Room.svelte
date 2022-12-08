<script lang="ts">
  import { getContext } from 'svelte';
  import { scaleCanvas } from 'layercake';
  import { scaleLinear, polygonHull, polygonCentroid } from "d3";
  import { config, devices, nodes } from '../lib/stores';
  import type { Config, Node, Room } from '../lib/types';

  const { data, xGet, yGet, width, height, xScale, yScale } = getContext('LayerCake');

  export let room:Room;;

  $: hull = room.points;
  $: centroid = polygonCentroid(hull);
  $: scaledHull = hull?.map((p) => {
      return [$xScale(p[0]),$yScale(p[1])];
  });
</script>

<path
stroke='white'
fill='purple'
fill-opacity="0.1"
d={`M${scaledHull.join("L")}Z`}
/>

<text x='{ $xScale(centroid[0]) }' y='{ $yScale(centroid[1]) }' fill='white' font-size='10px'>{room.name}</text>