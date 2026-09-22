'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { createHash } = require('node:crypto');

const CORE_STEPS = ['preflight', 'tags.create-table', 'tags.metadata-only', 'tags.delete-tag', 'tags.create-tag',
  'tags.create-constant', 'tags.edit-boolean', 'tags.edit-constant', 'tags.native-error', 'tags.delete-entry',
  'tags.import.create', 'tags.import.override', 'block.external.create', 'block.external.update',
  'udt.external.create', 'udt.external.update'];

function loadResume(options, writes) {
  if (!options.resumeReport) return null;
  const file = path.resolve(options.resumeReport);
  const text = fs.readFileSync(file, 'utf8');
  const prior = JSON.parse(text);
  assert.equal(prior.schemaVersion, 1, 'Unsupported acceptance report schema.');
  assert.ok(['failed', 'blocked'].includes(prior.status), 'Only a stopped report can be resumed.');
  assert.equal(prior.uncertainWrite, false, 'An uncertain write must be investigated, not resumed.');
  assert.equal(prior.processId, options.processId, 'Resume process must match the recorded process.');
  assert.equal(path.win32.normalize(prior.projectPath).toLowerCase(), path.win32.normalize(options.projectPath).toLowerCase(), 'Resume project must match the recorded project.');
  assert.match(prior.prefix, /^McpAT_[a-f0-9]{12}$/);
  assert.equal(typeof prior.plcObjectId, 'string');
  if (options.plcObjectId) assert.equal(options.plcObjectId, prior.plcObjectId, 'Resume CPU must match the recorded CPU.');
  const inherited = [...(prior.inheritedPassedSteps || []), ...prior.steps.filter(step => step.status === 'passed')
    .map(step => ({ ...step, evidenceReport: file }))];
  const completed = new Set(inherited.map(step => step.id));
  for (const id of CORE_STEPS) assert.ok(completed.has(id), `Resume requires verified core scenario ${id}.`);
  const stopped = prior.steps.filter(step => step.status !== 'passed');
  assert.ok(stopped.length, 'The report has no stopped scenario.');
  for (const step of stopped) {
    assert.ok(['source-consistency', 'source-format-snapshots', 'cross-references',
      'block.simatic-ml.roundtrip', 'udt.simatic-ml.roundtrip', 'block.simatic-sd.roundtrip', 'udt.simatic-sd.roundtrip'].includes(step.id),
    'This failure is outside the supported read-only continuation checkpoints.');
    assert.ok(!prior.calls.some(call => call.step === step.id && writes.includes(call.request.params?.name)),
      'A stopped scenario attempted a write. Inspect its effects; it cannot be automatically resumed.');
  }
  for (const [kind, suffix] of [['block', 'FC'], ['udt', 'UDT'], ['tag', 'Signal'], ['table', 'Tags']]) {
    assert.equal(prior.fixture[`${kind}Name`], `${prior.prefix}_${suffix}`, 'Recorded fixture name does not match this run.');
    assert.equal(typeof prior.fixture[`${kind}Id`], 'string');
    assert.ok(prior.fixture[`${kind}Id`].length > 0);
  }
  return { prior, file, inherited, completed, sha256: createHash('sha256').update(text).digest('hex') };
}

module.exports = { CORE_STEPS, loadResume };
