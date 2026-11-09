# LaTeX in TaskForge statements and quizzes

The editor renders LaTeX with KaTeX. After applying the source archive, the maintainer
installs the dependency from `apps/web` with `npm install katex@^0.16.0`. The source archive
does not change `package.json` or `package-lock.json`: the maintainer owns that install.

Both supported Tiptap nodes store the original LaTeX source, not generated HTML or images:

```json
{
  "type": "doc",
  "content": [
    {
      "type": "paragraph",
      "content": [
        { "type": "text", "text": "Find " },
        { "type": "inlineMath", "attrs": { "latex": "\\frac{x^2 + 1}{\\sqrt{7}}" } }
      ]
    },
    { "type": "blockMath", "attrs": { "latex": "\\int_0^1 x^2\\,dx" } }
  ]
}
```

- `inlineMath` is an atom placed inside a paragraph or other inline content.
- `blockMath` is a top-level block within a Tiptap document (not a paragraph child).
- The shared editor toolbar opens the same LaTeX source editor for both kinds, with a
  live preview and validation before insertion.
- Existing `mathInline` fraction/root nodes are still supported and do not require conversion.
- In course graph JSON, the Tiptap document above is **serialized as a JSON string** in
  `description`, `questions[].promptContentJson`, `questions[].options[].contentJson`,
  or `questions[].explanationContentJson` as applicable. Do not replace the legacy
  `prompt`/`options[].text` fields; they remain compatible with the existing API.
- Authoring, learner review, and explanation views render the same source. Empty or
  malformed LaTeX is displayed as escaped source text, not executed as HTML.

KaTeX uses a fixed bounded input size (4,096 characters), expansion limits, and
`trust: false`. Unsupported commands remain visible as source; they do not produce
arbitrary HTML. The normal test privacy and review gating rules are unchanged.
