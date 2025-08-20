<script lang="ts">
	let backgroundImage: string | null = null;
	let fileInput: HTMLInputElement;
	let rotation = 0;

	function handleFileUpload(event: Event) {
		const file = (event.target as HTMLInputElement).files?.[0];
		if (file) {
			const reader = new FileReader();
			reader.onload = (e) => {
				backgroundImage = e.target?.result as string;
			};
			reader.readAsDataURL(file);
		}
	}

	function handleUploadClick(event: MouseEvent) {
		event.preventDefault();
		event.stopPropagation();
		if (fileInput) {
			fileInput.click();
		}
	}

	function clearBackground(event: MouseEvent) {
		event.preventDefault();
		event.stopPropagation();
		backgroundImage = null;
		rotation = 0;
		if (fileInput) {
			fileInput.value = '';
		}
	}

	function rotateBackground(event: MouseEvent) {
		event.preventDefault();
		event.stopPropagation();
		rotation = (rotation + 90) % 360;
	}
</script>

<input bind:this={fileInput} type="file" accept=".svg,.png,.jpg,.jpeg" on:change={handleFileUpload} style="display: none;" />

{#if backgroundImage}
	<div class="fixed inset-0 pointer-events-none" style="z-index: 0;">
		<img src={backgroundImage} alt="Background" class="w-full h-full object-contain opacity-25 transition-transform duration-300" style="transform: rotate({rotation}deg);" />
	</div>
{/if}

<div class="fixed top-4 right-4 flex gap-2" style="z-index: 2;">
	{#if !backgroundImage}
		<button
			type="button"
			on:click={handleUploadClick}
			class="bg-blue-500 hover:bg-blue-700 text-white p-2 rounded cursor-pointer"
			title="Upload Background"
			aria-label="Upload Background"
		>
			<svg xmlns="http://www.w3.org/2000/svg" class="h-5 w-5" viewBox="0 0 20 20" fill="currentColor">
				<path fill-rule="evenodd" d="M4 3a2 2 0 00-2 2v10a2 2 0 002 2h12a2 2 0 002-2V5a2 2 0 00-2-2H4zm12 12H4l4-8 3 6 2-4 3 6z" clip-rule="evenodd" />
			</svg>
		</button>
	{:else}
		<button
			type="button"
			on:click={rotateBackground}
			class="bg-blue-500 hover:bg-blue-700 text-white p-2 rounded"
			title="Rotate Background"
			aria-label="Rotate Background"
		>
			<svg xmlns="http://www.w3.org/2000/svg" class="h-5 w-5" viewBox="0 0 20 20" fill="currentColor">
				<path fill-rule="evenodd" d="M4 2a1 1 0 011 1v2.101a7.002 7.002 0 0111.601 2.566 1 1 0 11-1.885.666A5.002 5.002 0 005.999 7H9a1 1 0 010 2H4a1 1 0 01-1-1V3a1 1 0 011-1zm.008 9.057a1 1 0 011.276.61A5.002 5.002 0 0014.001 13H11a1 1 0 110-2h5a1 1 0 011 1v5a1 1 0 11-2 0v-2.101a7.002 7.002 0 01-11.601-2.566 1 1 0 01.61-1.276z" clip-rule="evenodd" />
			</svg>
		</button>
		<button
			type="button"
			on:click={clearBackground}
			class="bg-red-500 hover:bg-red-700 text-white p-2 rounded"
			title="Clear Background"
			aria-label="Clear Background"
		>
			<svg xmlns="http://www.w3.org/2000/svg" class="h-5 w-5" viewBox="0 0 20 20" fill="currentColor">
				<path fill-rule="evenodd" d="M4.293 4.293a1 1 0 011.414 0L10 8.586l4.293-4.293a1 1 0 111.414 1.414L11.414 10l4.293 4.293a1 1 0 01-1.414 1.414L10 11.414l-4.293 4.293a1 1 0 01-1.414-1.414L8.586 10 4.293 5.707a1 1 0 010-1.414z" clip-rule="evenodd" />
			</svg>
		</button>
	{/if}
</div>
