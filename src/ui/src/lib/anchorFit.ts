import type { OptimizerCycleReport } from './types';

export interface AnchorFitRow {
	/** "Current" for the calibration in use, otherwise the optimizer's name. */
	label: string;
	/**
	 * "applied"; "selected" for a dry run's pick; otherwise "not applied". Not the gate's reason: the gate judges on
	 * node-to-node links, so "not better" there can sit next to a better fit to the anchors. The outcome line says why.
	 */
	status?: string;
	anchors: number;
	rms: number;
	/** e.g. "better on 2, worse on 1"; absent for the current calibration. */
	versus?: string;
}

/**
 * Rows for the calibration page's "Fit to anchors" table: the current calibration first, then each proposal
 * the last cycle scored on the anchors. Null when the cycle had no anchor heard by enough nodes.
 */
export function anchorFitRows(report: OptimizerCycleReport | null | undefined): AnchorFitRow[] | null {
	const baseline = report?.baselineAnchors;
	if (!report || !baseline) return null;

	const rows: AnchorFitRow[] = [{ label: 'Current', anchors: baseline.anchors, rms: baseline.rms }];
	for (const p of report.proposals ?? []) {
		if (!p.anchors) continue;
		const better = p.anchors.better ?? 0;
		const worse = p.anchors.worse ?? 0;
		const same = Math.max(0, p.anchors.anchors - better - worse);
		rows.push({
			label: p.optimizer,
			status: p.applied ? 'applied' : p.decision === 'Selected' ? 'selected' : 'not applied',
			anchors: p.anchors.anchors,
			rms: p.anchors.rms,
			versus: `better on ${better}, worse on ${worse}` + (same > 0 ? `, same on ${same}` : '')
		});
	}
	return rows;
}
