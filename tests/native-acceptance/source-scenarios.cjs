'use strict';

const assert = require('node:assert/strict');
const { createHash } = require('node:crypto');

// This module only describes calls for the supplied acceptance client. It neither
// attaches to TIA nor opens a transport, and it never saves or compiles a project.
async function runSourceScenarios(ctx) {
  assert.match(ctx.prefix, /^[A-Za-z][A-Za-z0-9_]{0,95}$/, 'Use a short ASCII fixture prefix.');
  assert.ok(ctx.fixture, 'A shared fixture record is required.');

  for (const kind of ['block', 'udt']) {
    const spec = specification(kind, ctx.prefix, ctx.fixture.tagName);
    ctx.fixture[`${kind}Name`] = spec.name;
    let currentId = null;
    let created = false;
    let updated = false;
    let verified = false;

    await ctx.step(`${kind}.external.create`, `Create ${spec.name} from external source and read it back`, async () => {
      await assertAbsent(ctx, spec);
      const written = await write(ctx, spec, 'external-source', [{ name: spec.fileName, content: spec.content }]);
      currentId = await reacquire(ctx, spec, written);
      ctx.fixture[`${kind}Id`] = currentId;
      const read = await readSource(ctx, spec, currentId, 'external-source');
      spec.assertSemantics(read.source.documents, false);
      created = verified = true;
      return { objectId: currentId, name: spec.name, format: 'external-source' };
    });

    await ctx.step(`${kind}.external.update`, `Edit the complete exported ${spec.name} source and verify replacement`, async () => {
      requirePrerequisite(created && verified, `${kind} external creation and readback`);
      await assertOwned(ctx, spec, currentId);
      const read = await readSource(ctx, spec, currentId, 'external-source');
      spec.assertSemantics(read.source.documents, false);
      const documents = editableDocuments(read.source);
      assert.equal(documents.length, 1, 'External source must contain exactly one complete document.');
      documents[0].content = spec.edit(documents[0].content);
      assertSingleExternalDeclaration(documents[0].content, spec);
      // The export and edit precede the final identity check. A user replacement
      // observed since the earlier read must never become our update target.
      await assertOwned(ctx, spec, currentId);
      verified = false;
      const written = await write(ctx, spec, 'external-source', documents);
      currentId = await reacquire(ctx, spec, written);
      ctx.fixture[`${kind}Id`] = currentId;
      const after = await readSource(ctx, spec, currentId, 'external-source');
      spec.assertSemantics(after.source.documents, true);
      updated = verified = true;
      return { objectId: currentId, name: spec.name, format: 'external-source' };
    });

    requirePrerequisite(updated && verified, `${kind} external update and readback`);
  }
  if (!ctx.deferFormats) await runFormatScenarios(ctx);
}

async function runFormatScenarios(ctx) {
  const objects = ['block', 'udt'].map(kind => ({
    spec: specification(kind, ctx.prefix, ctx.fixture.tagName), currentId: ctx.fixture[`${kind}Id`]
  }));
  await ctx.step('source-consistency', 'Verify retained source fixtures and the native consistency prerequisite.', async () => {
    for (const { spec, currentId } of objects) {
      await assertOwned(ctx, spec, currentId);
      const read = await readSource(ctx, spec, currentId, 'external-source');
      spec.assertSemantics(read.source.documents, true);
      if (read.metadata.state?.isConsistent !== true) {
        const error = new Error(`TIA reports ${spec.name} as inconsistent. Compile the selected PLC software offline in TIA, then explicitly resume this report. The runner never compiles.`);
        error.code = 'compileRequired';
        throw error;
      }
    }
  });
  // Check references while the user-compiled fixtures are still consistent.
  if (ctx.beforeFormats) await ctx.beforeFormats();
  const prepared = [];
  for (const format of ['simatic-ml', 'simatic-sd']) {
    for (const owned of objects) {
      const { spec, currentId } = owned;
      if (ctx.completedStepIds?.has(`${spec.kind}.${format}.roundtrip`)) continue;
      const id = `${spec.kind}.${format}.export`;
      try {
        await ctx.step(id, `Capture explicit ${format} from the consistent ${spec.name} before any format imports.`, async () => {
          await assertOwned(ctx, spec, currentId);
          const exported = await readSource(ctx, spec, currentId, format);
          const documents = editableDocuments(exported.source);
          assert.ok(documents.some(document => document.content.includes(spec.name)), 'Exported documents must retain the owned declaration name.');
          prepared.push({ owned, format, documents });
        });
      } catch (error) {
        if (error.code !== 'nativeExportUnavailable') throw error;
        // This was a read, with a still-consistent retained fixture and only
        // native export errors. Preserve the gap and test independent formats.
        // No failed write, transport error or context loss takes this path.
        (ctx.gaps ||= []).push({ id, reason: error.message });
      }
    }
  }
  // A native import can mark its object inconsistent again. Capturing all native
  // representations first avoids demanding another compile between imports.
  for (const { owned, format, documents } of prepared) {
    const { spec } = owned;
    await ctx.step(`${spec.kind}.${format}.roundtrip`, `Import the captured ${spec.name} ${format} documents and verify source`, async () => {
      await assertOwned(ctx, spec, owned.currentId);
      spec.assertSemantics((await readSource(ctx, spec, owned.currentId, 'external-source')).source.documents, true);
      const written = await write(ctx, spec, format, documents);
      owned.currentId = await reacquire(ctx, spec, written);
      ctx.fixture[`${spec.kind}Id`] = owned.currentId;
      spec.assertSemantics((await readSource(ctx, spec, owned.currentId, 'external-source')).source.documents, true);
      return { objectId: owned.currentId, name: spec.name, format };
    });
  }
}

function specification(kind, prefix, tagName) {
  if (kind === 'block') {
    const name = `${prefix}_FC`;
    if (tagName !== undefined) {
      assert.equal(typeof tagName, 'string');
      assert.ok(tagName.startsWith(`${prefix}_`), 'An optional tag reference must belong to this fixture prefix.');
      assert.match(tagName, /^[A-Za-z][A-Za-z0-9_]*$/, 'Use a plain owned tag name in the SCL fixture.');
    }
    const tagOutput = tagName ? '      ReferencedTag : Bool;\n' : '';
    const tagRead = tagName ? `   #ReferencedTag := "${tagName}";\n` : '';
    return {
      kind, name, list: 'list_blocks', read: 'get_block', write: 'write_blocks', fileName: `${name}.scl`,
      content: `FUNCTION "${name}" : Void\nVERSION : 0.1\n   VAR_OUTPUT\n      Marker : Int;\n${tagOutput}   END_VAR\nBEGIN\n   #Marker := 17;\n${tagRead}END_FUNCTION\n`,
      edit(content) {
        const marker = /(#(?:"Marker"|Marker)\s*:=\s*)(?:Int#)?17(\s*;)/gi;
        assert.equal([...content.matchAll(marker)].length, 1, 'Find exactly one original Marker assignment before editing.');
        return content.replace(marker, (_match, before, after) => `${before}29${after}`);
      },
      assertSemantics(documents, updated) {
        assert.equal(documents.length, 1);
        const content = documents[0].content;
        assertSingleExternalDeclaration(content, this);
        assert.match(content, new RegExp(`#(?:"Marker"|Marker)\\s*:=\\s*(?:Int#)?${updated ? 29 : 17}\\s*;`, 'i'));
        if (updated) assert.doesNotMatch(content, /#(?:"Marker"|Marker)\s*:=\s*(?:Int#)?17\s*;/i);
        if (tagName) assert.match(content, new RegExp(`#(?:"ReferencedTag"|ReferencedTag)\\s*:=\\s*"${tagName}"\\s*;`, 'i'));
      }
    };
  }
  const name = `${prefix}_UDT`;
  return {
    kind, name, list: 'list_udts', read: 'get_udt', write: 'write_udts', fileName: `${name}.udt`,
    content: `TYPE "${name}"\nVERSION : 0.1\n   STRUCT\n      Flag : Bool;\n   END_STRUCT;\nEND_TYPE\n`,
    edit(content) {
      assert.equal([...content.matchAll(/\bEND_STRUCT\s*;/gi)].length, 1, 'Find exactly one UDT structure before editing.');
      assert.doesNotMatch(content, /\bSampleCount\b/i, 'The new field must not exist before editing.');
      return content.replace(/\bEND_STRUCT\s*;/i, '   SampleCount : Int;\n   END_STRUCT;');
    },
    assertSemantics(documents, updated) {
      assert.equal(documents.length, 1);
      const content = documents[0].content;
      assertSingleExternalDeclaration(content, this);
      assert.match(content, /(?:"Flag"|\bFlag)\s*(?:\{[^}]*\}\s*)?:\s*Bool\b/i);
      if (updated) assert.match(content, /(?:"SampleCount"|\bSampleCount)\s*(?:\{[^}]*\}\s*)?:\s*Int\b/i);
      else assert.doesNotMatch(content, /\bSampleCount\b/i);
    }
  };
}

function requirePrerequisite(condition, description) {
  if (condition) return;
  const error = new Error(`Required prior scenario did not verify successfully: ${description}.`);
  error.code = 'prerequisiteFailed';
  throw error;
}

function leaves(roots, kind) {
  assert.ok(Array.isArray(roots), 'Inventory must provide roots.');
  return roots.flatMap(node => node.kind === kind ? [node] : leaves(node.children || [], kind));
}

async function matches(ctx, spec) {
  const inventory = await ctx.call(spec.list, { processId: ctx.processId, plcObjectId: ctx.plcObjectId });
  assert.equal(inventory.plcObjectId, ctx.plcObjectId, 'Inventory must belong to the selected CPU.');
  return leaves(inventory.roots, spec.kind).filter(node =>
    typeof node.name === 'string' && node.name.toLowerCase() === spec.name.toLowerCase());
}

async function assertAbsent(ctx, spec) {
  assert.equal((await matches(ctx, spec)).length, 0,
    `Refusing to create ${spec.name}: a same-name ${spec.kind} already exists in the selected CPU.`);
  assertSingleExternalDeclaration(spec.content, spec);
}

async function assertOwned(ctx, spec, objectId) {
  assert.equal(typeof objectId, 'string');
  assert.ok(objectId.length > 0);
  const found = await matches(ctx, spec);
  assert.equal(found.length, 1, `Expected exactly one owned ${spec.kind} named ${spec.name}.`);
  assert.equal(found[0].name, spec.name);
  assert.equal(found[0].objectId, objectId, 'The current native identity must match this run\'s owned object.');
  const detail = await ctx.call(spec.read, {
    processId: ctx.processId, objectId, includeSource: false, includePath: false
  });
  assert.equal(detail.metadata.objectId, objectId);
  assert.equal(detail.metadata.name, spec.name);
  assert.equal(detail.source, null, 'The ownership check must remain metadata-only.');
}

async function write(ctx, spec, sourceFormat, documents) {
  const result = await ctx.call(spec.write, {
    processId: ctx.processId, plcObjectId: ctx.plcObjectId, sourceFormat, documents
  });
  assert.equal(result.saved, false, 'Source writes must not save the project.');
  assert.equal(result.cleanupFailed, false, 'Native temporary source/file cleanup must succeed.');
  assert.equal(result.format, sourceFormat);
  assert.ok(Array.isArray(result.affectedObjects));
  assert.equal(result.affectedObjects.length, 1, 'This single-declaration fixture must affect exactly one object.');
  assert.equal(result.affectedObjects[0].kind, spec.kind);
  assert.equal(result.affectedObjects[0].name, spec.name, 'A source write must affect only its exact owned declaration.');
  return result;
}

async function reacquire(ctx, spec, written) {
  const found = await matches(ctx, spec);
  assert.equal(found.length, 1, `Expected exactly one ${spec.name} after generation/import.`);
  const node = found[0];
  assert.equal(node.name, spec.name);
  assert.equal(typeof node.objectId, 'string', 'Readback requires a native ID from fresh inventory.');
  assert.ok(node.objectId.length > 0);
  const reportedId = written.affectedObjects[0].objectId;
  if (reportedId !== null && reportedId !== undefined) assert.equal(node.objectId, reportedId);
  return node.objectId;
}

async function readSource(ctx, spec, objectId, sourceFormat) {
  let read;
  try {
    read = await ctx.call(spec.read, {
      processId: ctx.processId, objectId, includeSource: true, includePath: false,
      sourceFormat, includeDependencies: false
    });
  } catch (error) {
    const payload = error.toolResponse?.payload;
    if (['simatic-ml', 'simatic-sd'].includes(sourceFormat) &&
        payload?.metadata?.objectId === objectId && payload.metadata.name === spec.name &&
        payload.metadata.state?.isConsistent === true && payload.source === null && !payload.error &&
        payload.complete === false && Array.isArray(payload.errors) && payload.errors.length > 0 &&
        payload.errors.every(item => item.origin === 'tia-openness' && item.operation === 'sourceExport' && item.format === sourceFormat))
      error.code = 'nativeExportUnavailable';
    throw error;
  }
  assert.equal(read.metadata.objectId, objectId);
  assert.equal(read.metadata.name, spec.name);
  assert.ok(read.source, `Explicit ${sourceFormat} must return source; unsupported formats fail this scenario.`);
  assert.equal(read.source.format, sourceFormat, 'An explicit format must not fall back.');
  assert.equal(read.source.dependenciesIncluded, false);
  assert.ok(Array.isArray(read.source.documents) && read.source.documents.length > 0);
  for (const document of read.source.documents) {
    assert.equal(typeof document.name, 'string');
    assert.equal(typeof document.content, 'string');
    assert.ok(document.content.length > 0);
    assert.deepEqual(document.checksum, {
      algorithm: 'sha-256', scope: 'returned-content', encoding: 'utf-8-no-bom',
      value: createHash('sha256').update(document.content, 'utf8').digest('hex')
    }, 'Export checksum must describe the exact returned UTF-8 content.');
  }
  return read;
}

function editableDocuments(source) {
  // Reader-only checksum fields are deliberately not submitted to write tools.
  return source.documents.map(({ name, content }) => ({ name, content }));
}

function assertSingleExternalDeclaration(content, spec) {
  const declarations = [...content.matchAll(/^\s*(FUNCTION_BLOCK|FUNCTION|ORGANIZATION_BLOCK|DATA_BLOCK|TYPE)\s+"([^"]+)"/gmi)];
  assert.equal(declarations.length, 1, 'Only one complete owned declaration may be submitted.');
  assert.equal(declarations[0][1].toUpperCase(), spec.kind === 'block' ? 'FUNCTION' : 'TYPE');
  assert.equal(declarations[0][2], spec.name);
}

module.exports = { runSourceScenarios, runFormatScenarios };
