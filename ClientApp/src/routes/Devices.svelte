<script lang="ts">
  import { getContext, createEventDispatcher } from 'svelte';
  import { config, devices, showAll } from '../lib/stores';
  import { tweened } from 'svelte/motion';
  import { cubicOut } from 'svelte/easing';
  import { zoomIdentity } from 'd3-zoom';

  import type { ScaleOrdinal } from "d3";
  import type { Device } from '../lib/types';

  export let transform = zoomIdentity;

  let colors : ScaleOrdinal<string, string> = getContext('colors');

  const { xScale, yScale } = getContext('LayerCake');

  const r = tweened(5, {
    duration: 400,
    easing: cubicOut
  });

  export let floor = 0;
  $: floorId = $config?.floors[floor]?.id;

  let hovered = "";
  let selected = "";

  let dispatcher = createEventDispatcher();

  function hover (d:Device | null) {
    r.set(d == null ? 5 : 10);
    hovered = d?.id ?? "";
    dispatcher('hovered', d);
  }

  function unselect(){
  }

  function select (d:Device) {
    selected = d?.id ?? "";
    dispatcher('selected', d);
  }
  </script>

<g transform={transform.toString()}>
  {#if $devices }
  {#each $devices.filter(a => $showAll || (((a.name ?? "").length > 0) && (a?.floor?.id ?? floorId) == floorId)) as d}
    <circle cx='{ $xScale(d.location.x) }' cy='{ $yScale(d.location.y) }' fill={d?.room?.id ? colors(d?.room?.id) : "black"} r={ d.id == hovered ? $r : 5 } on:mouseover="{() => { hover(d) }}" on:focus="{() => { select(d) }}" on:mouseout="{() => { hover(null) }}" on:blur="{()=>{unselect()}}" />
    <text x='{ $xScale(d.location.x) + 7}' y='{ $yScale(d.location.y) + 3 }' fill='white' font-size='10px'>{d.name ?? d.id}</text>
  {/each}
  {/if}
</g>