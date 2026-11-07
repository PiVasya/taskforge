// Portable image packages; no third-party ZIP runtime. Writer uses STORE because
// raster images are already compressed. Reader also accepts standard DEFLATE ZIPs.
const enc = new TextEncoder();
const dec = new TextDecoder('utf-8', { fatal: true });
export const ZIP_MAX_BYTES = 160 * 1024 * 1024;
export const ZIP_MAX_FILES = 550;
export const ZIP_MAX_ITEM = 24 * 1024 * 1024;
const TABLE = new Uint32Array(256);
for (let i = 0; i < 256; i++) {
  let c = i;
  for (let j = 0; j < 8; j++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
  TABLE[i] = c >>> 0;
}
export function crc32(data) {
  let c = 0xffffffff;
  for (const byte of data) c = TABLE[(c ^ byte) & 255] ^ (c >>> 8);
  return (c ^ 0xffffffff) >>> 0;
}
const u16 = (view, pos) => view.getUint16(pos, true);
const u32 = (view, pos) => view.getUint32(pos, true);
function validPath(name) {
  return typeof name === 'string' && name.length <= 220 && name.length > 0
    && !name.startsWith('/') && !name.includes('\\') && !name.includes('//')
    && name.split('/').every((part) => part && part !== '.' && part !== '..');
}
function header(size) { return new DataView(new ArrayBuffer(size)); }
function blobBytes(data) { return data instanceof Uint8Array ? Promise.resolve(data) : new Blob([data]).arrayBuffer().then((b) => new Uint8Array(b)); }

export async function writeImageZip(entries) {
  if (!Array.isArray(entries) || !entries.length || entries.length > ZIP_MAX_FILES) throw new Error('Недопустимое количество файлов ZIP.');
  const names = new Set(), chunks = [], central = [];
  let offset = 0, total = 0;
  for (const entry of entries) {
    if (!validPath(entry.path) || names.has(entry.path)) throw new Error('Некорректное или повторяющееся имя файла ZIP.');
    names.add(entry.path);
    const name = enc.encode(entry.path);
    const bytes = await blobBytes(entry.bytes);
    if (bytes.length > ZIP_MAX_ITEM) throw new Error(`Превышен размер файла ${entry.path}`);
    total += bytes.length;
    if (total > ZIP_MAX_BYTES) throw new Error('Архив слишком большой.');
    const crc = crc32(bytes);
    let packed = bytes;
    let method = 0;
    if (entry.path === 'graph.json' && bytes.length > 1024 && typeof CompressionStream !== 'undefined') {
      try {
        const encoded = new Uint8Array(await new Response(new Blob([bytes]).stream()
          .pipeThrough(new CompressionStream('deflate-raw'))).arrayBuffer());
        if (encoded.length < bytes.length) { packed = encoded; method = 8; }
      } catch { /* STORE remains a portable fallback. */ }
    }
    const local = header(30);
    local.setUint32(0, 0x04034b50, true);
    local.setUint16(4, 20, true); local.setUint16(6, 0x800, true);
    local.setUint16(8, method, true);
    local.setUint32(14, crc, true);
    local.setUint32(18, packed.length, true); local.setUint32(22, bytes.length, true);
    local.setUint16(26, name.length, true);
    chunks.push(local.buffer, name, packed);
    const dir = header(46);
    dir.setUint32(0, 0x02014b50, true);
    dir.setUint16(4, 20, true); dir.setUint16(6, 20, true);
    dir.setUint16(8, 0x800, true); dir.setUint16(10, method, true);
    dir.setUint32(16, crc, true);
    dir.setUint32(20, packed.length, true); dir.setUint32(24, bytes.length, true);
    dir.setUint16(28, name.length, true); dir.setUint32(42, offset, true);
    central.push(dir.buffer, name);
    offset += local.byteLength + name.length + packed.length;
  }
  const cdLength = central.reduce((sum, part) => sum + part.byteLength, 0);
  const eocd = header(22);
  eocd.setUint32(0, 0x06054b50, true);
  eocd.setUint16(8, entries.length, true);
  eocd.setUint16(10, entries.length, true);
  eocd.setUint32(12, cdLength, true);
  eocd.setUint32(16, offset, true);
  return new Blob([...chunks, ...central, eocd.buffer], { type: 'application/zip' });
}

export async function readImageZip(file) {
  if (!file || file.size > ZIP_MAX_BYTES || file.size < 22) throw new Error('Размер ZIP не поддерживается.');
  const raw = new Uint8Array(await file.arrayBuffer());
  const view = new DataView(raw.buffer, raw.byteOffset, raw.byteLength);
  let end = -1;
  for (let p = raw.length - 22; p >= Math.max(0, raw.length - 65557); p--) {
    if (u32(view, p) === 0x06054b50 && p + 22 + u16(view, p + 20) === raw.length) { end = p; break; }
  }
  if (end === -1 || u16(view, end + 4) || u16(view, end + 6)) throw new Error('ZIP повреждён или является многотомным.');
  const count = u16(view, end + 10);
  const size = u32(view, end + 12), start = u32(view, end + 16);
  if (count < 1 || count > ZIP_MAX_FILES || count !== u16(view, end + 8) || start + size > end) throw new Error('Недопустимая таблица файлов ZIP.');
  const entries = new Map();
  let pos = start, total = 0;
  for (let i = 0; i < count; i++) {
    if (pos + 46 > raw.length || u32(view, pos) !== 0x02014b50) throw new Error('Повреждён каталог ZIP.');
    const flags = u16(view, pos + 8), method = u16(view, pos + 10);
    const crc = u32(view, pos + 16), compressed = u32(view, pos + 20), length = u32(view, pos + 24);
    const nameLen = u16(view, pos + 28), extraLen = u16(view, pos + 30), commentLen = u16(view, pos + 32);
    const localOffset = u32(view, pos + 42);
    if (flags & 1 || !(method === 0 || method === 8) || length > ZIP_MAX_ITEM || compressed > ZIP_MAX_BYTES) throw new Error('Шифрование, формат сжатия или размер ZIP не поддерживаются.');
    if (pos + 46 + nameLen + extraLen + commentLen > raw.length) throw new Error('Повреждено имя файла ZIP.');
    const name = dec.decode(raw.subarray(pos + 46, pos + 46 + nameLen));
    pos += 46 + nameLen + extraLen + commentLen;
    if (name.endsWith('/')) continue;
    if (!validPath(name) || entries.has(name)) throw new Error('ZIP содержит небезопасное или повторяющееся имя файла.');
    total += length;
    if (total > ZIP_MAX_BYTES) throw new Error('Слишком много распакованных данных.');
    if (localOffset + 30 > raw.length || u32(view, localOffset) !== 0x04034b50) throw new Error('Повреждён ZIP.');
    const dataStart = localOffset + 30 + u16(view, localOffset + 26) + u16(view, localOffset + 28);
    if (dataStart + compressed > start) throw new Error('Данные ZIP пересекаются с каталогом.');
    const data = raw.subarray(dataStart, dataStart + compressed);
    let bytes;
    if (method === 0) bytes = new Uint8Array(data);
    else {
      if (typeof DecompressionStream === 'undefined') throw new Error('Браузер не поддерживает распаковку ZIP.');
      try {
        const reader = new Blob([data]).stream().pipeThrough(new DecompressionStream('deflate-raw')).getReader();
        const parts = [];
        let received = 0;
        for (;;) {
          const { done, value } = await reader.read();
          if (done) break;
          received += value.byteLength;
          if (received > length) { await reader.cancel(); throw new Error('ZIP содержит файл с ложным размером.'); }
          parts.push(value);
        }
        bytes = new Uint8Array(received);
        let at = 0;
        for (const part of parts) { bytes.set(part, at); at += part.byteLength; }
      } catch { throw new Error(`Нельзя распаковать ${name}.`); }
    }
    if (bytes.length !== length || crc32(bytes) !== crc) throw new Error(`Неверная контрольная сумма: ${name}`);
    entries.set(name, bytes);
  }
  if (pos !== start + size) throw new Error('Повреждён каталог ZIP.');
  return entries;
}
