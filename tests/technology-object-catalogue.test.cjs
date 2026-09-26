'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const { parseTechnologyObjectCatalogue } = require('../tools/technology-object-catalogue.cjs');

const html = `
<table class="title"><tr><td>Overview of technology objects and versions</td></tr></table>
<table class="table_default">
  <thead>
    <tr>
      <th><p>CPU</p></th>
      <th><p>Technology</p></th>
      <th><p>Technology object</p></th>
      <th><p>Version of technology object</p></th>
      <th><p>CPU FW</p></th>
    </tr>
  </thead>
  <tbody>
    <tr>
      <td rowspan="3"><p>S7-1500</p></td>
      <td rowspan="2"><p>Motion Control<sup>1)</sup></p></td>
      <td><p>TO_SpeedAxis</p></td>
      <td rowspan="2"><p>≥ V5.0</p></td>
      <td rowspan="2"><p>≥ V2.8</p></td>
    </tr>
    <tr>
      <td><p>TO_Cam (S7-1500T)</p></td>
    </tr>
    <tr>
      <td><p>PID Control</p></td>
      <td><p>PID_Compact</p></td>
      <td><p>V2.3</p></td>
      <td><p>≥ V2.0</p></td>
    </tr>
    <tr>
      <td colspan="5">
        <p><sup>1)</sup> Motion parameters match the S7-1500 description.</p>
        <p><sup>2)</sup> Writing parameters is not supported.</p>
      </td>
    </tr>
  </tbody>
</table>`;

test('rowspan cells apply to the following rows and a footnote stays separate', () => {
  const catalogue = parseTechnologyObjectCatalogue(html, { origin: 'test' });
  assert.deepEqual(catalogue.objects, [
    { cpu: 'S7-1500', technology: 'Motion Control', name: 'TO_SpeedAxis', version: '≥ V5.0', firmware: '≥ V2.8', notes: ['1)'] },
    { cpu: 'S7-1500', technology: 'Motion Control', name: 'TO_Cam (S7-1500T)', version: '≥ V5.0', firmware: '≥ V2.8', notes: ['1)'] },
    { cpu: 'S7-1500', technology: 'PID Control', name: 'PID_Compact', version: 'V2.3', firmware: '≥ V2.0', notes: [] }
  ]);
  assert.deepEqual(catalogue.footnotes, [
    { marker: '1)', text: 'Motion parameters match the S7-1500 description.' },
    { marker: '2)', text: 'Writing parameters is not supported.' }
  ]);
  assert.equal(catalogue.source.publication, 'V20');
  assert.equal(catalogue.source.origin, 'test');
});

test('a table without the overview header is rejected', () => {
  assert.throws(() => parseTechnologyObjectCatalogue('<table><tr><td>CPU</td></tr></table>', {}), /overview table/);
});
