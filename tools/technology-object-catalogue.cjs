'use strict';

// Fetches the V20 Openness "Overview of technology objects and versions" topic and
// writes one JSON object per table row. Rowspan cells are carried onto the rows
// they cover. This does not publish an MCP tool.

const fs = require('node:fs');
const path = require('node:path');

const ORIGIN = 'https://docs.tia.siemens.cloud';
const PRETTY_URL = 'en-us/v20/technology-objects/overview-of-technology-objects-and-versions';
const HEADERS = ['CPU', 'Technology', 'Technology object', 'Version of technology object', 'CPU FW'];
const FIELDS = ['cpu', 'technology', 'name', 'version', 'firmware'];

function decode(value) {
  return value
    .replace(/&#x([0-9a-f]+);/gi, (_, hex) => String.fromCodePoint(Number.parseInt(hex, 16)))
    .replace(/&#(\d+);/g, (_, digits) => String.fromCodePoint(Number(digits)))
    .replace(/&ge;/g, '≥')
    .replace(/&gt;/g, '>')
    .replace(/&lt;/g, '<')
    .replace(/&quot;/g, '"')
    .replace(/&nbsp;/g, ' ')
    .replace(/&amp;/g, '&');
}

function clean(value) {
  return decode(value.replace(/<[^>]+>/g, ' ')).replace(/\s+/g, ' ').trim();
}

function cellValue(html) {
  const notes = [];
  const text = clean(html.replace(/<sup\b[^>]*>([\s\S]*?)<\/sup>/gi, (_, inner) => {
    const marker = clean(inner);
    if (marker) notes.push(marker);
    return ' ';
  }));
  return { text, notes };
}

function attributes(tag) {
  const span = name => {
    const match = tag.match(new RegExp(`\\b${name}\\s*=\\s*["'](\\d+)["']`, 'i'));
    return match ? Number(match[1]) : 1;
  };
  return { rowspan: span('rowspan'), colspan: span('colspan') };
}

function blocks(html, tag) {
  const found = [];
  const pattern = new RegExp(`<${tag}\\b([^>]*)>([\\s\\S]*?)<\\/${tag}>`, 'gi');
  for (let match = pattern.exec(html); match; match = pattern.exec(html)) found.push(match);
  return found;
}

function cellsOf(rowHtml) {
  return blocks(rowHtml, 't[dh]').map(match => ({ ...attributes(match[1]), html: match[2], ...cellValue(match[2]) }));
}

function footnotesOf(html) {
  const paragraphs = blocks(html, 'p').map(match => match[2]);
  return (paragraphs.length ? paragraphs : [html]).map(chunk => {
    const value = cellValue(chunk);
    return { marker: value.notes[0] || '', text: value.text };
  }).filter(item => item.marker || item.text);
}

function tableRows(html) {
  const tables = blocks(html, 'table');
  for (const table of tables) {
    const body = table[2];
    const head = blocks(body, 'thead')[0];
    const headerRow = head ? blocks(head[2], 'tr')[0] : blocks(body, 'tr')[0];
    if (!headerRow) continue;
    const headers = cellsOf(headerRow[2]).map(cell => cell.text);
    if (headers.join('\n') !== HEADERS.join('\n')) continue;
    const tbody = blocks(body, 'tbody')[0];
    const section = tbody ? tbody[2] : body;
    const rows = blocks(section, 'tr').map(row => cellsOf(row[2]));
    return head ? rows : rows.slice(1);
  }
  throw new Error('The topic HTML has no technology-object overview table.');
}

function expand(rows) {
  const width = FIELDS.length;
  const pending = Array(width).fill(null);
  const objects = [];
  const footnotes = [];
  rows.forEach((row, index) => {
    if (row.length === 1 && row[0].colspan === width) {
      footnotes.push(...footnotesOf(row[0].html));
      return;
    }
    const values = Array(width);
    let cursor = 0;
    let used = 0;
    while (cursor < width) {
      if (pending[cursor] && pending[cursor].left > 0) {
        values[cursor] = pending[cursor].value;
        pending[cursor].left -= 1;
        cursor += 1;
        continue;
      }
      const cell = row[used];
      if (!cell) throw new Error(`Row ${index + 1} ended before column ${cursor + 1}.`);
      used += 1;
      for (let span = 0; span < cell.colspan; span += 1) {
        if (cursor >= width) throw new Error(`Row ${index + 1} has more columns than the header.`);
        values[cursor] = { text: cell.text, notes: cell.notes };
        pending[cursor] = cell.rowspan > 1 ? { value: values[cursor], left: cell.rowspan - 1 } : null;
        cursor += 1;
      }
    }
    if (used !== row.length) throw new Error(`Row ${index + 1} has unused cells.`);
    if (values.some(value => !value.text)) throw new Error(`Row ${index + 1} has an empty catalogue cell.`);
    const notes = [];
    for (const value of values) {
      for (const marker of value.notes) if (!notes.includes(marker)) notes.push(marker);
    }
    const object = {};
    FIELDS.forEach((field, column) => { object[field] = values[column].text; });
    object.notes = notes;
    objects.push(object);
  });
  if (objects.length === 0) throw new Error('The overview table did not contain any technology objects.');
  return { objects, footnotes };
}

function parseTechnologyObjectCatalogue(html, source) {
  const { objects, footnotes } = expand(tableRows(html));
  return {
    source: {
      publication: 'V20',
      prettyUrl: PRETTY_URL,
      description: 'Technology objects the V20 Openness overview says can be created, grouped by CPU family. Version and firmware are the published cell text. A family row is not a per-order-number support list.',
      ...source
    },
    footnotes,
    objects
  };
}

function segment(id) {
  return encodeURIComponent(id).replace(/%7E/gi, '~');
}

async function getJson(url, options) {
  const response = await fetch(url, options);
  if (!response.ok) throw new Error(`${options && options.method || 'GET'} ${url} returned ${response.status}.`);
  return response.json();
}

function findTopic(node, tocId) {
  if (!node || typeof node !== 'object') return null;
  if (node.tocId === tocId && node.contentId) return node;
  const children = Array.isArray(node) ? node : Object.values(node);
  for (const child of children) {
    const found = findTopic(child, tocId);
    if (found) return found;
  }
  return null;
}

async function fetchTechnologyObjectCatalogue() {
  const resolved = await getJson(`${ORIGIN}/internal/api/webapp/pretty-url/reader`, {
    method: 'POST',
    headers: { accept: 'application/json', 'content-type': 'application/json' },
    body: JSON.stringify({ prettyUrl: PRETTY_URL })
  });
  if (!resolved.documentId || !resolved.tocId) throw new Error('The pretty URL did not resolve to a document and topic.');
  const pages = await getJson(`${ORIGIN}/api/khub/maps/${segment(resolved.documentId)}/pages`, { headers: { accept: 'application/json' } });
  const topic = findTopic(pages, resolved.tocId);
  if (!topic) throw new Error(`The resolved topic ${resolved.tocId} is not in the document pages.`);
  const contentUrl = `${ORIGIN}/api/khub/maps/${segment(resolved.documentId)}/topics/${segment(topic.contentId)}/content?target=DESIGNED_READER`;
  const response = await fetch(contentUrl, { headers: { accept: 'text/html' } });
  if (!response.ok) throw new Error(`GET ${contentUrl} returned ${response.status}.`);
  const html = await response.text();
  return parseTechnologyObjectCatalogue(html, {
    documentId: resolved.documentId,
    contentId: topic.contentId,
    title: topic.title || 'Overview of technology objects and versions',
    fetchedAtUtc: new Date().toISOString()
  });
}

function writeCatalogue(file, catalogue) {
  fs.mkdirSync(path.dirname(file), { recursive: true });
  fs.writeFileSync(file, `${JSON.stringify(catalogue, null, 2)}\n`);
}

function argument(name) {
  const index = process.argv.indexOf(name);
  if (index < 0) return null;
  const value = process.argv[index + 1];
  if (!value || value.startsWith('--')) throw new Error(`${name} needs a value.`);
  return value;
}

async function main() {
  if (process.argv.includes('--help')) {
    console.log('Usage: node tools/technology-object-catalogue.cjs [--out data/technology-object-catalogue.json] [--html file]');
    return;
  }
  const out = path.resolve(argument('--out') || path.join(__dirname, '..', 'data', 'technology-object-catalogue.json'));
  const htmlFile = argument('--html');
  const catalogue = htmlFile
    ? parseTechnologyObjectCatalogue(fs.readFileSync(htmlFile, 'utf8'), { origin: 'file', path: path.resolve(htmlFile), fetchedAtUtc: new Date().toISOString() })
    : await fetchTechnologyObjectCatalogue();
  writeCatalogue(out, catalogue);
  const families = [...new Set(catalogue.objects.map(item => item.cpu))];
  console.log(`Wrote ${catalogue.objects.length} technology objects (${families.join(', ')}) to ${out}`);
}

if (require.main === module) {
  main().catch(error => {
    console.error(error.message);
    process.exitCode = 1;
  });
}

module.exports = { parseTechnologyObjectCatalogue, fetchTechnologyObjectCatalogue, writeCatalogue, PRETTY_URL };
