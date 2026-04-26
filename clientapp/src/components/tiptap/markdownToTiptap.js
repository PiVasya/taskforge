function textNode(text, marks) {
  if (!text) return null;
  return marks && marks.length ? { type: "text", text, marks } : { type: "text", text };
}

export function inlineTextToNodes(value) {
  const text = String(value ?? "");
  if (!text) return [];

  const nodes = [];
  let i = 0;

  while (i < text.length) {
    const start = text.indexOf("`", i);
    if (start < 0) {
      const node = textNode(text.slice(i));
      if (node) nodes.push(node);
      break;
    }

    if (start > i) {
      const node = textNode(text.slice(i, start));
      if (node) nodes.push(node);
    }

    const end = text.indexOf("`", start + 1);
    if (end < 0) {
      const node = textNode(text.slice(start));
      if (node) nodes.push(node);
      break;
    }

    const codeText = text.slice(start + 1, end);
    const node = textNode(codeText, [{ type: "code" }]);
    if (node) nodes.push(node);
    i = end + 1;
  }

  return nodes;
}

function paragraph(line) {
  return { type: "paragraph", content: inlineTextToNodes(line) };
}

function listItem(line) {
  return {
    type: "listItem",
    content: [paragraph(line)],
  };
}

function isBlank(line) {
  return !String(line ?? "").trim();
}

function parseFenceStart(line) {
  const match = String(line ?? "").match(/^\s*```\s*([\w+-]*)\s*$/);
  return match ? (match[1] || "") : null;
}

export function plainTextToTiptapDoc(value) {
  const raw = String(value ?? "").replace(/\r/g, "");
  if (!raw.trim()) return { type: "doc", content: [] };

  const lines = raw.split("\n");
  const content = [];
  let i = 0;

  while (i < lines.length) {
    const line = lines[i];

    if (isBlank(line)) {
      i += 1;
      continue;
    }

    const fenceLang = parseFenceStart(line);
    if (fenceLang !== null) {
      const code = [];
      i += 1;
      while (i < lines.length && !/^\s*```\s*$/.test(lines[i])) {
        code.push(lines[i]);
        i += 1;
      }
      if (i < lines.length) i += 1;
      content.push({
        type: "codeBlock",
        attrs: { language: fenceLang || null },
        content: code.length ? [{ type: "text", text: code.join("\n") }] : [],
      });
      continue;
    }

    const heading = line.match(/^\s{0,3}(#{1,3})\s+(.+)$/);
    if (heading) {
      content.push({
        type: "heading",
        attrs: { level: heading[1].length },
        content: inlineTextToNodes(heading[2].trim()),
      });
      i += 1;
      continue;
    }

    const ordered = line.match(/^\s*(\d+)[.)]\s+(.+)$/);
    if (ordered) {
      const items = [];
      const start = Math.max(1, parseInt(ordered[1], 10) || 1);
      while (i < lines.length) {
        if (isBlank(lines[i])) {
          const next = lines[i + 1];
          if (next && /^\s*\d+[.)]\s+/.test(next)) {
            i += 1;
            continue;
          }
          break;
        }
        const item = lines[i].match(/^\s*\d+[.)]\s+(.+)$/);
        if (!item) break;
        items.push(listItem(item[1].trim()));
        i += 1;
      }
      content.push({ type: "orderedList", attrs: { start }, content: items });
      continue;
    }

    const bullet = line.match(/^\s*[-*]\s+(.+)$/);
    if (bullet) {
      const items = [];
      while (i < lines.length) {
        if (isBlank(lines[i])) {
          const next = lines[i + 1];
          if (next && /^\s*[-*]\s+/.test(next)) {
            i += 1;
            continue;
          }
          break;
        }
        const item = lines[i].match(/^\s*[-*]\s+(.+)$/);
        if (!item) break;
        items.push(listItem(item[1].trim()));
        i += 1;
      }
      content.push({ type: "bulletList", content: items });
      continue;
    }

    content.push(paragraph(line.trim()));
    i += 1;
  }

  return { type: "doc", content };
}
