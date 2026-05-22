# GUI Consistency Contract

This contract defines stable UI semantics for labels, status indicators, and compact controls in the M3Undle web app.

## Scope

Applies to all Razor components under `src/M3Undle.Web/Components`.

## Chip Contract

### Intent types

- `status`: current state of a resource or workflow.
- `severity`: issue seriousness.
- `filter`: toggle that changes current view.
- `navigation`: chip that navigates to another page/section.
- `count`: passive numeric summary.
- `metadata`: taxonomy or informational tag.

### Color semantics

- `Color.Success`: healthy, active, published, successful.
- `Color.Warning`: pending, degraded, caution-required.
- `Color.Error`: failed, blocked, or critical issue.
- `Color.Info`: informational, newly discovered, neutral context.
- `Color.Default`: inactive, disabled, unknown, or neutral baseline.
- `Color.Primary`: app-primary taxonomy or selected object identity.

Do not reuse `Color.Warning` or `Color.Success` for purely decorative emphasis.

### Variant semantics

- `Variant.Filled`: active selection or user-applied filter state.
- `Variant.Outlined`: passive display, inactive filter, or neutral status.
- `Variant.Text`: lightweight inline hints only.

### Tooltip requirements

A tooltip is required for:

- every clickable chip (`OnClick` or `Href`),
- every status/severity chip whose meaning is not obvious from plain text,
- every count chip where the counted entity may be ambiguous.

Tooltip text should answer:

- what this represents,
- what click does (if clickable).

### Click affordance rules

- Clickable chips must use `Style="cursor:pointer;"`.
- Non-clickable chips must not imply interaction.
- In a related chip group, avoid mixing clickable and non-clickable chips unless each clickable chip has a clear tooltip.

### Accessibility rules

- Icon-only actions near chips should have a tooltip.
- Prefer explicit text over color-only communication.
- If a state is critical, pair color with icon and/or text label.

## Icon Button Contract

- Icon-only buttons with actions must have a tooltip.
- Destructive actions must use `Color.Error`.
- Data refresh/reload actions should use refresh icon plus tooltip text beginning with "Reload".

## Alert Contract

- `Severity.Error`: operation failed or data is unusable.
- `Severity.Warning`: risky or degraded but still usable.
- `Severity.Info`: contextual or setup guidance.
- `Severity.Success`: operation completed successfully.

Keep alert copy actionable and concise.

## Inline Style Contract

- Prefer shared component classes and MudBlazor props over ad-hoc inline `Style`.
- Inline styles are acceptable for one-off layout constraints, but repeated styles should move to CSS.

## Enforcement Notes For AI Agents

Before finalizing UI changes:

1. Verify chip intent category and semantic color/variant.
2. Verify tooltip and click affordance requirements.
3. Check for mixed interactive vs passive chips in the same row.
4. Run solution build and tests.

If this contract conflicts with an existing screen pattern, preserve behavior and add a follow-up note in the PR description for contract alignment work.
