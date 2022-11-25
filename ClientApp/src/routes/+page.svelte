<script>
	import { createEventDispatcher } from "svelte";
	import { Canvas, Layer, t } from "svelte-canvas";
  import { devices, nodes, events } from '../lib/stores';
  let w;
  let h;
  const origin = {x:0, y:0};
  var scale = 20;

$: render = ({ context: ctx, width, height }) => {
  ctx.setTransform(1, 0, 0, 1, origin.x, origin.y);

  ($nodes ?? []).forEach((node) => {
    ctx.beginPath();
    ctx.fillStyle = node.color ? node.color : "#ffffff80";
    ctx.arc(node.location.x * scale, node.location.y * scale, (node.coverage ?? 0.1)  * scale, 0, 2 * Math.PI);
    ctx.moveTo(node.location.x * scale + (node.coverage ?? 16)  * scale, node.location.y * scale);
    ctx.arc(node.location.x * scale, node.location.y * scale, (node.coverage ?? 16)  * scale, 0, 2 * Math.PI);
    ctx.stroke();

    ctx.fillStyle = 'red';
    ctx.fillText(node.name ?? node.id, node.location.x * scale, node.location.y * scale);
  });

  ($devices ?? []).forEach(device => {
    ctx.beginPath();
    ctx.fillStyle = device.color ? device.color : "#ffffff80";
    ctx.arc(device.location.x * scale, device.location.y * scale, 0.25  * scale, 0, 2 * Math.PI);
    ctx.fill();
    ctx.stroke();

    ctx.fillStyle = 'white';
    ctx.fillText(device.name ?? device.id, (device.location.x + 0.25) * scale, (device.location.y + 0.125) * scale);
  });
};

</script>

<svelte:head>
	<title>Home</title>
	<meta name="description" content="Svelte demo app" />
</svelte:head>

<div bind:clientWidth={w} bind:clientHeight={h} class="blah">
    <Canvas  width={w} height={h}>
        <Layer {render} />
    </Canvas>
</div>

<style>
    .blah {
        width: 100vw;
        height: 100vh;
    }
    canvas {
		background-color: #666;
		-webkit-mask: url(/svelte-logo-mask.svg) 50% 50% no-repeat;
		mask: url(/svelte-logo-mask.svg) 50% 50% no-repeat;
	}
</style>
