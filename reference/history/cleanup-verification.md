# Code and documentation cleanup verification — 2026-09-23

This records the cleanup following the review of commit `f9528f9`. It is a dated implementation/transport checkpoint, not current runtime state or additional native engineering acceptance. Current contracts and evidence are indexed in [current documentation](../../docs/README.md).

## Changes checked

- Production JSON-RPC admission validates protocol version, request IDs, duplicate fields and message shape before tool dispatch. Protocol responses preserve `id:null`; malformed JSON and invalid envelopes return distinct errors. Client notifications are acknowledged without engineering dispatch. Arrays remain explicitly unsupported by this single-message transport.
- Managed readiness uses token-authenticated `/api/lifecycle/health` and matches the launched PID and executable path after the Windows shell is ready. Token-bearing health and shutdown requests do not follow redirects.
- Redundant dashboard engineering read routes, the obsolete connection-probe read chain, fixed historical status evidence and unused helpers/async STA overload were removed. Existing connection/context guard coverage was retained through supported operations.
- Current documentation separates contracts and user behavior from original dated records. The evidence index distinguishes verified user constants and UDT formats from remaining system-constant, dependency, protection and unit-scope gaps.

## Local checks

- Staged and normal net48/x64 Release builds against the installed V20 Public API passed with zero compiler errors. NuGet reported `NU1900` because its vulnerability advisory feed was unreachable; vulnerability lookup was not verified.
- The Siemens-free .NET 8 harness passed **124/124 groups**, including six new groups that exercise the production RPC admission path and prove invalid envelopes never reach fake engineering operations.
- Top-level JavaScript suites passed **134 tests** with four opt-in HTTP tests initially skipped. This includes the lifecycle regression exercising the production PowerShell readiness function with matching identity, mismatched PID/path, unrelated HTTP success, missing authentication and unavailable-endpoint responses. It starts no listener or server.
- The four opt-in HTTP tests subsequently passed against the single managed server: origin policy, current tool publication/guarded dispatch, authenticated lifecycle identity and invalid RPC envelopes/null error IDs.

## Managed reload and publication

The previous tracked server, PID **66852**, exited through token-protected graceful shutdown. Its older build did not implement authenticated identity health; the new helper reported that limitation without preventing the existing authenticated stop path.

After the normal Release build succeeded, the helper started the updated executable as PID **38128**, full access, at `http://127.0.0.1:5000/`. It verified authenticated readiness against that PID and the checkout's exact executable path. A subsequent status call confirmed the same identity. The passive implementation phase was `native-compile-delete-export`.

The complete `tools/list` definitions matched before and after the reload: **24 tools**, SHA-256 of their serialized definitions:

```text
f5baf5461b13a7c64c4499a1dcaf9df68c5d1eea1621912623f2e69327b0f87f
```

Local compact comparison records are retained in ignored `test-results/cleanup-validation/before.json` and `after.json`.

The reload released the one previous bridge attachment. Post-reload status showed zero attachments. The user must reconnect through the dashboard and approve TIA access if prompted. Validation did not attach, reconnect, save, compile, import, write, close a TIA project or perform a PLC online operation. Native acceptance suites were not rerun. Existing engineering evidence retains its original scope.
