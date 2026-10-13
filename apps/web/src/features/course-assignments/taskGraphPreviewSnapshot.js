export async function captureTaskGraphPreviewSnapshot({ readMap, readExport }) {
  const before = await readMap();
  const currentExport = await readExport();
  const after = await readMap();
  if (Number(before?.version || 0) !== Number(after?.version || 0)) {
    const error = new Error('Карта изменилась во время проверки. Повторите Preview.');
    error.code = 'IMPORT_PREVIEW_CHANGED';
    throw error;
  }
  return { currentExport, mapVersion: Number(after?.version || 0) };
}
