export function buildConversationTitle(value, maxLength = 160) {
  const normalized = String(value ?? '').replace(/\s+/g, ' ').trim();
  if (!normalized) return 'AI-чат';
  const chars = Array.from(normalized);
  if (chars.length <= maxLength) return normalized;
  const keep = Math.max(1, maxLength - 1);
  return `${chars.slice(0, keep).join('')}…`;
}
