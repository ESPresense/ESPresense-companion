import { base } from "$app/paths";
import { toastStore, type ToastSettings } from "@skeletonlabs/skeleton";

export async function load({ fetch, params }) {
  return await fetch(`${base}/api/device/${params.id}`)
    .then((response) => {
      if (response.status != 200) throw new Error(response.statusText);
      var data = response.json();
      return data;
    })
    .catch((e) => {
      console.log(e);
      const t: ToastSettings = { message: e, background: 'variant-filled-error' };
      toastStore.trigger(t);
      return { settings: { "originalId": params.id, "id": null, "name": null, "rssi@1m": null } };
    });
}