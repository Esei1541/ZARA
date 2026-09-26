import assert from 'node:assert/strict';
import { test } from 'node:test';
import { TextDecoder } from 'node:util';
import { createRecoveryBytes, parseRecoveryFragment } from '../recovery.mjs';

const request = '0123456789abcdef'.repeat(4);

test('canonical fragment creates the exact UTF-8 recovery file without BOM', () => {
  assert.deepEqual(parseRecoveryFragment(`#v=1&request=${request}`), { kind: 'valid', request });
  const bytes = createRecoveryBytes(request);
  assert.notDeepEqual(Array.from(bytes.slice(0, 3)), [0xef, 0xbb, 0xbf]);
  assert.equal(new TextDecoder('utf-8', { fatal: true }).decode(bytes),
    `{"format":"zara-recovery","version":1,"request":"${request}"}`);
});

test('missing, duplicate, malformed and unsupported fragment fields are rejected', () => {
  for (const missing of ['', '#']) {
    assert.deepEqual(parseRecoveryFragment(missing), { kind: 'missing' });
  }
  for (const invalid of [
    '#request=' + request,
    '#v=1',
    '#v=2&request=' + request,
    '#v=1&request=' + request + '&request=' + request,
    '#v=1&v=1&request=' + request,
    '#v=1&request=' + request + '&extra=1',
    '#request=' + request + '&v=1',
    '#v=1&request=' + request.toUpperCase(),
    '#v=1&request=' + request.slice(1),
    '#v=1&request=' + 'g' + request.slice(1),
    '#v=1&request=%30' + request.slice(1),
  ]) {
    assert.deepEqual(parseRecoveryFragment(invalid), { kind: 'invalid' }, invalid);
  }
});

test('recovery file cannot be made from an invalid request', () => {
  for (const invalid of ['', request.toUpperCase(), request.slice(1), 'g' + request.slice(1)]) {
    assert.throws(() => createRecoveryBytes(invalid), TypeError);
  }
});

test('download uses a generic binary MIME type and preserves the key filename', async (t) => {
  let onDownload;
  let downloadedBlob;
  let clicked = false;
  const button = { addEventListener: (_event, callback) => { onDownload = callback; } };
  const status = { hidden: true };
  const link = { click: () => { clicked = true; }, remove: () => {} };
  const previousDocument = Object.getOwnPropertyDescriptor(globalThis, 'document');
  const previousWindow = Object.getOwnPropertyDescriptor(globalThis, 'window');
  t.after(() => {
    if (previousDocument) Object.defineProperty(globalThis, 'document', previousDocument);
    else delete globalThis.document;
    if (previousWindow) Object.defineProperty(globalThis, 'window', previousWindow);
    else delete globalThis.window;
  });
  globalThis.document = {
    getElementById: (id) => id === 'download' ? button : status,
    createElement: () => link,
    body: { appendChild: () => {} },
  };
  globalThis.window = { location: { hash: `#v=1&request=${request}` }, setTimeout: () => {} };
  t.mock.method(URL, 'createObjectURL', (blob) => {
    downloadedBlob = blob;
    return 'blob:recovery-test';
  });

  await import('../recovery.mjs?download-test');
  onDownload();

  assert.equal(clicked, true);
  assert.equal(link.download, 'zara-recovery.key');
  assert.equal(downloadedBlob.type, 'application/octet-stream');
  assert.equal(await downloadedBlob.text(), new TextDecoder().decode(createRecoveryBytes(request)));
  assert.equal(status.hidden, false);
});
