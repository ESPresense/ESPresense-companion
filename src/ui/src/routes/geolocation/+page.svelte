<script lang="ts">
	import { onMount, onDestroy } from 'svelte';
	import { config, devices, nodes } from '$lib/stores';
	import { internalToGps } from '$lib/gpsUtils';
	import type { ConfigGps, Device, Node, Floor, Room } from '$lib/types';

	// OpenLayers imports
	import Map from 'ol/Map';
	import View from 'ol/View';
	import TileLayer from 'ol/layer/Tile';
	import VectorLayer from 'ol/layer/Vector';
	import OSM from 'ol/source/OSM';
	import XYZ from 'ol/source/XYZ';
	import VectorSource from 'ol/source/Vector';
	import Feature from 'ol/Feature';
	import Point from 'ol/geom/Point';
	import Polygon from 'ol/geom/Polygon';
	import { fromLonLat } from 'ol/proj';
	import { getCenter } from 'ol/extent';
	import Style from 'ol/style/Style';
	import CircleStyle from 'ol/style/Circle';
	import Fill from 'ol/style/Fill';
	import Stroke from 'ol/style/Stroke';
	import Text from 'ol/style/Text';

	let mapElement: HTMLDivElement;
	let map: Map | null = null;

	// Sources for map features
	const floorplanSource = new VectorSource();
	const deviceSource = new VectorSource();
	const nodeSource = new VectorSource();

	// Styles
	const roomStyle = new Style({
		stroke: new Stroke({
			color: 'rgba(0, 100, 255, 0.7)',
			width: 2
		}),
		fill: new Fill({
			color: 'rgba(0, 100, 255, 0.1)'
		})
	});

	const deviceStyle = new Style({
		image: new CircleStyle({
			radius: 6,
			fill: new Fill({ color: 'red' }),
			stroke: new Stroke({ color: 'white', width: 1 })
		}),
		text: new Text({
			font: '12px Calibri,sans-serif',
			fill: new Fill({ color: '#000' }),
			stroke: new Stroke({ color: '#fff', width: 3 }),
			offsetY: 15
		})
	});

	const nodeStyle = new Style({
		image: new CircleStyle({
			radius: 5,
			fill: new Fill({ color: 'blue' }),
			stroke: new Stroke({ color: 'white', width: 1 })
		}),
		text: new Text({
			font: '12px Calibri,sans-serif',
			fill: new Fill({ color: '#000' }),
			stroke: new Stroke({ color: '#fff', width: 3 }),
			offsetY: -15
		})
	});

	let initialViewFit = false; // Flag to fit view only once initially

	onMount(() => {
		map = new Map({
			target: mapElement,
			layers: [
				new TileLayer({
					// Use ESRI World Imagery
					source: new XYZ({
						url: 'https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}', // Consider externalizing this URL
						maxZoom: 19, // Optional: Set max zoom level supported by the source
						attributions: 'Sources: Esri, DigitalGlobe, GeoEye, i-cubed, USDA FSA, USGS, AEX, Getmapping, Aerogrid, IGN, IGP, swisstopo, and the GIS User Community'
					})
				}),
				new VectorLayer({
					source: floorplanSource,
					style: roomStyle
				}),
				new VectorLayer({
					source: deviceSource,
					style: (feature) => {
						deviceStyle.getText()?.setText(feature.get('name') || '');
						return deviceStyle;
					}
				}),
				new VectorLayer({
					source: nodeSource,
					style: (feature) => {
						nodeStyle.getText()?.setText(feature.get('name') || '');
						return nodeStyle;
					}
				})
			],
			view: new View({
				center: fromLonLat([0, 0]), // Default center
				zoom: 2 // Default zoom
			})
		});

		// Cleanup on component destroy
		return () => {
			map?.setTarget(undefined);
			map = null;
		};
	});

	// --- Reactive Updates ---

	// Update Floorplan Layer
	$: if (map && $config?.floors && $config?.gps) {
		updateFloorplanFeatures($config.floors, $config.gps);
	}

	// Update Devices Layer
	$: if (map && $devices && $config?.gps) {
		updateDeviceFeatures($devices, $config.gps);
	}

	// Update Nodes Layer
	$: if (map && $nodes && $config?.gps) {
		updateNodeFeatures($nodes, $config.gps);
	}

	// --- Helper Functions ---

	function updateFloorplanFeatures(floors: Floor[], gpsConfig: ConfigGps) {
		floorplanSource.clear();
		const features: Feature[] = [];
		floors.forEach((floor) => {
			floor.rooms?.forEach((room) => {
				const coordinates: number[][] = [];
				let validPolygon = true;
				room.points?.forEach(([x, y]) => {
					const gpsCoords = internalToGps(x, y, gpsConfig);
					if (gpsCoords) {
						coordinates.push(fromLonLat([gpsCoords.longitude, gpsCoords.latitude]));
					} else {
						validPolygon = false;
					}
				});

				// Close the ring if valid
				if (validPolygon && coordinates.length > 0) {
					coordinates.push(coordinates[0]); // Close the polygon ring
					const polygon = new Polygon([coordinates]); // Note: Polygon expects array of rings
					const feature = new Feature({
						geometry: polygon,
						name: room.name || room.id
					});
					features.push(feature);
				} else if (room.points?.length > 0) {
					console.warn(`Could not create valid polygon for room ${room.id} - invalid GPS coordinates calculated.`);
				}
			});
		});
		if (features.length > 0) {
			floorplanSource.addFeatures(features);
			fitViewToSource(floorplanSource); // Fit view after adding features
		}
	}

	function updateDeviceFeatures(deviceList: Device[], gpsConfig: ConfigGps) {
		deviceSource.clear();
		const features: Feature[] = [];
		deviceList.forEach((device) => {
			if (device.location?.x != null && device.location?.y != null) {
				const gpsCoords = internalToGps(device.location.x, device.location.y, gpsConfig);
				if (gpsCoords) {
					const point = new Point(fromLonLat([gpsCoords.longitude, gpsCoords.latitude]));
					const feature = new Feature({
						geometry: point,
						name: device.name || device.id
					});
					features.push(feature);
				}
			}
		});
		if (features.length > 0) {
			deviceSource.addFeatures(features);
			// Don't fit view on device updates alone, floorplan fit is primary
		}
	}

	function updateNodeFeatures(nodeList: Node[], gpsConfig: ConfigGps) {
		nodeSource.clear();
		const features: Feature[] = [];
		nodeList.forEach((node) => {
			if (node.location?.x != null && node.location?.y != null) {
				const gpsCoords = internalToGps(node.location.x, node.location.y, gpsConfig);
				if (gpsCoords) {
					const point = new Point(fromLonLat([gpsCoords.longitude, gpsCoords.latitude]));
					const feature = new Feature({
						geometry: point,
						name: node.name || node.id
					});
					features.push(feature);
				}
			}
		});
		if (features.length > 0) {
			nodeSource.addFeatures(features);
			// Don't fit view on node updates alone
		}
	}

	// Function to fit the map view to a source's extent
	function fitViewToSource(source: VectorSource) {
		if (!map || initialViewFit) return; // Only fit once initially or if forced

		const extent = source.getExtent();
		if (extent && extent[0] !== Infinity) {
			// Check if extent is valid
			map.getView().fit(extent, {
				padding: [50, 50, 50, 50], // Add some padding
				duration: 500 // Animation duration
			});
		} else if (source.getFeatures().length > 0) {
			// Fallback if extent calculation fails but features exist
			const firstGeometry = source.getFeatures()[0].getGeometry();
			if (firstGeometry instanceof Polygon) {
				const firstPolygonCoords = firstGeometry.getCoordinates();
				// For polygons, get the first coordinate of the first (outer) ring
				if (firstPolygonCoords && firstPolygonCoords[0] && firstPolygonCoords[0][0]) {
					const centerCoord = firstPolygonCoords[0][0]; // Use the first vertex as a fallback center
					map.getView().animate({ center: centerCoord, zoom: 17, duration: 500 });
					initialViewFit = true; // Set flag after initial fit
				}
			} else if (firstGeometry instanceof Point) {
				// Handle case where source might contain points (though unlikely for initial fit)
				const pointCoords = firstGeometry.getCoordinates();
				if (pointCoords) {
					map.getView().animate({ center: pointCoords, zoom: 17, duration: 500 });
					initialViewFit = true; // Set flag after initial fit
				}
			}
		}
	}
</script>

<svelte:head>
	<title>ESPresense Companion: Geolocation</title>
	<link rel="stylesheet" href="https://cdn.jsdelivr.net/npm/ol@v9.1.0/ol.css" />
</svelte:head>

<div class="w-full h-full relative">
	<div bind:this={mapElement} class="w-full h-full map-container"></div>
</div>

<style>
	/* Ensure map container takes full height */
	.map-container {
		width: 100%;
		height: 100%;
		position: absolute;
		top: 0;
		left: 0;
	}

	/* Override default OL button styling if needed */
	:global(.ol-zoom .ol-zoom-in),
	:global(.ol-zoom .ol-zoom-out) {
		background-color: rgba(0, 60, 136, 0.7); /* Example */
	}
	:global(.ol-zoom .ol-zoom-in:hover),
	:global(.ol-zoom .ol-zoom-out:hover) {
		background-color: rgba(0, 60, 136, 0.9); /* Example */
	}
</style>
