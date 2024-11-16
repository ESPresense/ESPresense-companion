<script lang="ts">
	let backgroundImage: string | null = null;
	let fileInput: HTMLInputElement;

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
		if (fileInput) {
			fileInput.value = '';
		}
	}
</script>

<input
	bind:this={fileInput}
	type="file"
	accept=".svg,.png,.jpg,.jpeg"
	on:change={handleFileUpload}
	style="display: none;"
/>

{#if backgroundImage}
	<div class="fixed inset-0 pointer-events-none" style="z-index: 0;">
		<img
			src={backgroundImage}
			alt="Background"
			class="w-full h-full object-contain opacity-25"
		/>
	</div>
{/if}

<div class="fixed top-4 right-4" style="z-index: 2;">
	{#if !backgroundImage}
		<button
			type="button"
			on:click={handleUploadClick}
			class="bg-blue-500 hover:bg-blue-700 text-white font-bold py-2 px-4 rounded cursor-pointer"
		>
			Upload Background
		</button>
	{:else}
		<button
			type="button"
			on:click={clearBackground}
			class="bg-red-500 hover:bg-red-700 text-white font-bold py-2 px-4 rounded"
		>
			Clear
		</button>
	{/if}
</div>
