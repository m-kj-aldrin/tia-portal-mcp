'use strict';

const fs = require('node:fs');
const path = require('node:path');

const TRANSIENT_RENAME_CODES = new Set(['EPERM', 'EACCES', 'EBUSY']);
const RENAME_ATTEMPTS = 5;
const RETRY_DELAY_MS = 50;
const sleep = milliseconds => Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, milliseconds);

// These retries are exclusively local filesystem renames. Neither report writing
// nor its callers repeat an MCP request when evidence persistence fails.
function renameReport(source, target, io, pause) {
  for (let attempt = 1; ; attempt++) {
    try { io.renameSync(source, target); return; }
    catch (error) {
      if (attempt >= RENAME_ATTEMPTS || !TRANSIENT_RENAME_CODES.has(error.code)) throw error;
      pause(RETRY_DELAY_MS);
    }
  }
}

function writeReport(output, report, renderMarkdown, dependencies = {}) {
  const io = dependencies.io || fs;
  const pause = dependencies.sleep || sleep;
  // Compute both representations from the same synchronous snapshot before any
  // output changes. Commit JSON first; never advance Markdown after a JSON failure.
  const json = JSON.stringify(report, null, 2);
  const markdown = renderMarkdown(report);
  const jsonFile = path.join(output, 'report.json');
  const markdownFile = path.join(output, 'report.md');
  io.writeFileSync(jsonFile + '.tmp', json);
  io.writeFileSync(markdownFile + '.tmp', markdown);
  renameReport(jsonFile + '.tmp', jsonFile, io, pause);
  renameReport(markdownFile + '.tmp', markdownFile, io, pause);
  // On permanent failure the uncommitted temp is intentionally retained for
  // diagnosis; existing evidence is never deleted to bypass a filesystem lock.
}

module.exports = { RENAME_ATTEMPTS, RETRY_DELAY_MS, writeReport };
