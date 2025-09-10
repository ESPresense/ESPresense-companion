import type { Action } from 'svelte/action';
import { computePosition, offset, flip, shift, autoUpdate } from '@floating-ui/dom';

// A lightweight tooltip action that opens on hover/focus
export const tooltip: Action<HTMLElement, string> = (node, content) => {
	let tooltipEl: HTMLDivElement | null = null;
	let cleanupAutoUpdate: (() => void) | null = null;
	// Generate a unique, stable ID for this tooltip instance
	const tooltipId = `tooltip-${crypto.randomUUID()}`;

	/**
	 * Ensure the tooltip DOM element exists, creating and initializing it if needed.
	 *
	 * If a tooltip element has already been created this is a no-op. When created,
	 * the element is configured with presentational classes, absolute positioning,
	 * non-interactive pointer behavior, initial text from `content`, ARIA attributes
	 * (`role="tooltip"`, `aria-hidden="true"`), and assigned the stable `tooltipId`.
	 *
	 * This function mutates the module-scoped `tooltipEl` (and indirectly relies on
	 * `content` and `tooltipId`) and has no return value.
	 */
	function ensureTooltip() {
		if (tooltipEl) return;
		tooltipEl = document.createElement('div');
		tooltipEl.className = 'card preset-filled-secondary-500 p-4';
		tooltipEl.style.position = 'absolute';
		tooltipEl.style.zIndex = '1000';
		tooltipEl.style.pointerEvents = 'none';
		tooltipEl.textContent = content ?? '';
		// Set ARIA attributes for accessibility
		tooltipEl.setAttribute('role', 'tooltip');
		tooltipEl.setAttribute('aria-hidden', 'true');
		tooltipEl.id = tooltipId;
	}

	async function position() {
		if (!tooltipEl) return;
		const { x, y } = await computePosition(node, tooltipEl, {
			placement: 'top',
			middleware: [offset(6), flip(), shift()]
		});
		tooltipEl.style.left = `${x}px`;
		tooltipEl.style.top = `${y}px`;
	}

	function show() {
		ensureTooltip();
		if (!tooltipEl) return;
		if (!tooltipEl.parentNode) document.body.appendChild(tooltipEl);
		// Update ARIA attributes for accessibility
		tooltipEl.setAttribute('aria-hidden', 'false');
		node.setAttribute('aria-describedby', tooltipId);
		// autoUpdate repositions on scroll/resize/layout changes
		cleanupAutoUpdate = autoUpdate(node, tooltipEl, position);
		void position();
	}

	function hide() {
		if (cleanupAutoUpdate) {
			cleanupAutoUpdate();
			cleanupAutoUpdate = null;
		}
		if (tooltipEl && tooltipEl.parentNode) {
			// Update ARIA attributes for accessibility
			tooltipEl.setAttribute('aria-hidden', 'true');
			node.removeAttribute('aria-describedby');
			tooltipEl.remove();
		}
	}

	// Wire explicit DOM events; avoid framework-specific prop spreading
	const onEnter = () => show();
	const onLeave = () => hide();
	const onFocus = () => show();
	const onBlur = () => hide();

	node.addEventListener('mouseenter', onEnter);
	node.addEventListener('mouseleave', onLeave);
	node.addEventListener('focus', onFocus, true);
	node.addEventListener('blur', onBlur, true);

	return {
		update(text: string) {
			content = text;
			if (tooltipEl) tooltipEl.textContent = text ?? '';
			// If visible, reposition in case content size changed
			void position();
		},
		destroy() {
			hide();
			node.removeEventListener('mouseenter', onEnter);
			node.removeEventListener('mouseleave', onLeave);
			node.removeEventListener('focus', onFocus, true);
			node.removeEventListener('blur', onBlur, true);
		}
	};
};
