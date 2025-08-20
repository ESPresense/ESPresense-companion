<script lang="ts">
	import { showAlert, showConfirm, showCustomModal, closeModal } from './modalUtils';
	import AlertModal from './modals/AlertModal.svelte';
	import ConfirmModal from './modals/ConfirmModal.svelte';

	function handleShowAlert() {
		showAlert({
			title: 'Test Alert',
			message: 'This is a test alert message using the new modal system!',
			type: 'info'
		});
	}

	function handleShowSuccessAlert() {
		showAlert({
			title: 'Success!',
			message: 'Operation completed successfully.',
			type: 'success'
		});
	}

	function handleShowWarningAlert() {
		showAlert({
			title: 'Warning',
			message: 'This action might have consequences.',
			type: 'warning'
		});
	}

	function handleShowErrorAlert() {
		showAlert({
			title: 'Error',
			message: 'Something went wrong. Please try again.',
			type: 'error'
		});
	}

	function handleShowConfirm() {
		const modalId = showCustomModal(ConfirmModal, {
			title: 'Delete Item',
			message: 'Are you sure you want to delete this item? This action cannot be undone.',
			confirmText: 'Delete',
			cancelText: 'Keep',
			type: 'warning'
		});

		// In a real app, you would handle the confirm/cancel events
		// For demo purposes, we'll just close it after 5 seconds
		setTimeout(() => closeModal(modalId), 5000);
	}

	function handleShowCustomModal() {
		showCustomModal(AlertModal, {
			title: 'Custom Modal',
			message: 'This is a custom modal with props passed in.',
			type: 'success'
		});
	}
</script>

<div class="p-6 space-y-4">
	<h2 class="text-2xl font-bold mb-4">Modal System Demo</h2>

	<div class="grid grid-cols-1 md:grid-cols-2 gap-4">
		<div class="card p-4 space-y-2">
			<h3 class="text-lg font-semibold">Alert Modals</h3>
			<button class="btn btn-primary w-full" on:click={handleShowAlert}>
				Show Info Alert
			</button>
			<button class="btn btn-success w-full" on:click={handleShowSuccessAlert}>
				Show Success Alert
			</button>
			<button class="btn btn-warning w-full" on:click={handleShowWarningAlert}>
				Show Warning Alert
			</button>
			<button class="btn btn-error w-full" on:click={handleShowErrorAlert}>
				Show Error Alert
			</button>
		</div>

		<div class="card p-4 space-y-2">
			<h3 class="text-lg font-semibold">Other Modals</h3>
			<button class="btn btn-warning w-full" on:click={handleShowConfirm}>
				Show Confirm Modal
			</button>
			<button class="btn btn-secondary w-full" on:click={handleShowCustomModal}>
				Show Custom Modal
			</button>
		</div>
	</div>

	<div class="mt-6 p-4 bg-surface-100-800-token rounded-container">
		<h3 class="text-lg font-semibold mb-2">Usage Example:</h3>
		<pre class="text-sm overflow-x-auto"><code>{`import { showAlert, showConfirm } from '$lib/modalUtils';

// Simple alert
showAlert({
  title: 'Alert',
  message: 'Something happened!',
  type: 'info'
});

// Confirmation dialog
showConfirm({
  title: 'Delete Item',
  message: 'Are you sure?',
  type: 'warning'
}).then(confirmed => {
  if (confirmed) {
    // User confirmed
    console.log('Item deleted');
  }
});`}</code></pre>
	</div>
</div>