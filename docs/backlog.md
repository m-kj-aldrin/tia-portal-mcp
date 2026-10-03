# Backlog

Work agreed with the user but not yet implemented. Remove an item when it is done; the current contract documents remain authoritative.

Updated 2026-09-28 after the declarative Datastar dashboard migration.

## Native refinements requiring live checks

- **Extra native reads.** A [read-only attribute probe](evidence.md#manual-native-attribute-probe) on disposable SclStyle found named `GetAttributes` faster than separate block property reads in both a 9-block run and a 57-block run after adding 48 uniform SCL fixtures. The named result returned a native `ProgrammingLanguage` enum rather than the typed reader's string; the all-readable-attributes overload was much slower and returned a wrapper. Eight device items gave inconsistent timing for bulk-value reuse. Before changing the product reader, confirm value normalization and end-to-end relevance on a more varied representative V20 project. The separate [Device-reference probe](evidence.md#manual-native-device-reference-probe) found lookup small relative to a sampled full Device read and no demonstrated cross-request proxy-cache gain.
- **Duplicate native traversal.** The block, UDT and tag-table readers walk software units and groups separately. Device, block, UDT, tag-table and technology-object detail readers each have a `PathOf` walker. `OpennessWrites` has both `Destination` and `TechnologyDestination`. Merging native traversal needs a live check.
