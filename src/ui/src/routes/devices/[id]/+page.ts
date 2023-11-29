import { base } from "$app/paths";

export async function load({ fetch, params }) {
  return await fetch(`${base}/api/device/${params.id}`)
    .then((response) => {
      if (response.status != 200) throw new Error(response.statusText);
      var data = response.json();
      return data;
    })
    .catch((e) => {
      return { settings: { "originalId": params.id, "id": null, "name": null, "rssi@1m": null, "error": e } };
    });
}