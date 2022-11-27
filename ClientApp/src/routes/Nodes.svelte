<script>
    import { getContext } from 'svelte';
    import { scaleCanvas } from 'layercake';

    import { config, devices, nodes } from '../lib/stores';
    const { data, xGet, yGet, width, height } = getContext('LayerCake');
    const { ctx } = getContext('canvas');

  $: {
    if ($ctx && $devices) {
    ($devices).forEach((node) => {
      $ctx.beginPath();
      $ctx.fillStyle = node.color ? node.color : "#ffffff80";
      $ctx.arc($xGet(node.location.x), $yGet(node.location.y), 0.1 , 0, 2 * Math.PI);
      $ctx.moveTo($xGet(node.location.x + (node.coverage ?? 16)), $yGet(node.location.y));
      $ctx.arc($xGet(node.location.x), $yGet(node.location.y), node.coverage ?? 16, 0, 2 * Math.PI);
      $ctx.stroke();

      $ctx.fillStyle = 'red';
      $ctx.fillText(node.name ?? node.id, $xGet(node.location.x), $yGet(node.location.y));
    });
  }
  };

  </script>