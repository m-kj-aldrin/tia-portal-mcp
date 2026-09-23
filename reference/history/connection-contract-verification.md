# Connection contract verification

This preserves the dated checklist from the read contract. Checked and unchecked entries describe that checkpoint only; consult the [current evidence index](../../docs/evidence.md) before treating a case as verified or assigning work.

### First prototype and verification

The connection policy above is implemented. Completed live checks below are based on the user's manual V20 tests on 2026-09-21. Unchecked items are live evidence still open; the corresponding behavior is implemented and covered offline. See [prototype verification evidence](connection-prototype.md#verification-evidence).

- [x] Retain attachments to two user-selected TIA processes and verify that invalidating one leaves the other usable.
- [x] With background checks enabled, detect project closure, preserve the other connection, and require explicit reconnect after reopening.
- [ ] Verify fresh path checks for replacement, closing, path changes and a projectless process gaining a project, including with dashboard polling paused.
- [x] With background checks paused, close and reopen the same test project at the same path and reject a read through the old context. The supplied response reached the retained-native-project mismatch guard. Resume monitoring and verify that the other process remains readable and explicit reconnection restores the first.
- [ ] Verify a project change during a read returns an error without retrying against the replacement project or returning a payload from a detected invalid context.
- [ ] Queue a request, invalidate and reconnect its process, and verify the old request cannot execute through the new attachment. Check process exit and reused-PID handling as well.
- [x] Verify explicit attachment disposal leaves a user-started TIA UI instance and its project open, preserves the other process's connection, and permits explicit reconnection.
- [ ] Measure the guard overhead.
- Headless startup and attachment are omitted. This baseline creates no headless instance whose shutdown needs validation.

Lifecycle scenarios require isolated disposable test projects or deliberate user actions. They are not permission for the existing read-only tools to close, save or modify the user's project. Record observed V20 behavior separately from offline checks. The read-only cutover is done. The unchecked items above are remaining live evidence, not missing connection code.
