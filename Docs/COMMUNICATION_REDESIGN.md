# Future Communication Center — product contract

Status: **Design pending. Legacy module retired in schema 8; no replacement implementation exists yet.**

## Operator workflow

1. Choose recipients using the established FMSC Student Picker, family or staff selection, or a campus/class/outstanding-fee filter.
2. Compose a plain message or select a reviewed template. Resolve personalized values from saved school records, not from duplicate contact directories.
3. Preview recipients, text, selected SMS/WhatsApp channel, exclusions and charges; confirm explicitly before attempting delivery.
4. Review an intelligible History with provider-specific submitted/accepted/delivered/unknown/failed distinctions.

Settings are limited to useful provider configuration and template review. Sending is always optional in Admission and Fee Collection, and must never block saving school business data. No background or automatic delivery is enabled by default. Provider security, opt-outs, explicit consent, cost limits, idempotency, durable snapshots and failure reconciliation are required behind the simple UI.

New communication-specific consent and persistence, if required, belong in the replacement schema/migration, not in an old unused Admission add-on. Do not reintroduce redundant or competing manual contact records. **No live provider calls without separately accepted end-to-end integration.**
