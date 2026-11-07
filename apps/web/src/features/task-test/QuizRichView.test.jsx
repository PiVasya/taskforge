import React from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { QuizQuestionView, QuizChoiceView, quizHasInlineBlank } from './QuizRichView';

const asDocument = (content) => JSON.stringify({ type: 'doc', content });
const paragraph = (content) => ({ type: 'paragraph', content });

describe('quiz Tiptap read-only rendering', () => {
  it('renders old TXT literally without parsing Markdown or requiring an editor instance', () => {
    const html = renderToStaticMarkup(<QuizQuestionView question={{ prompt: 'x < 7\n`not code`' }} />);
    expect(html).toContain('x &lt; 7');
    expect(html).toContain('`not code`');
    expect(html).not.toContain('contenteditable');
  });

  it('renders fractions, roots and superscripts in answer choices', () => {
    const contentJson = asDocument([paragraph([
      { type: 'mathInline', attrs: { kind: 'fraction', numerator: '1', denominator: '7' } },
      { type: 'text', text: '7', marks: [{ type: 'superscript' }] },
      { type: 'mathInline', attrs: { kind: 'sqrt', expression: '7' } },
    ])]);
    const html = renderToStaticMarkup(<QuizChoiceView option={{ contentJson }} />);
    expect(html).toContain('<mfrac>');
    expect(html).toContain('<msqrt>');
    expect(html).toContain('<sup>7</sup>');
    expect(html).not.toContain('contenteditable');
  });

  it('preserves whitespace inside a superscript rather than silently rewriting authored math text', () => {
    const contentJson = asDocument([paragraph([
      { type: 'text', text: 'x', marks: [] },
      { type: 'text', text: ' 7', marks: [{ type: 'superscript' }] },
    ])]);
    const html = renderToStaticMarkup(<QuizChoiceView option={{ contentJson }} />);
    expect(html).toContain('<sup> 7</sup>');
    expect(html).not.toContain('contenteditable');
  });

  it('renders exactly one inline fill answer even when its underscores have different marks', () => {
    const question = { promptContentJson: asDocument([paragraph([
      { type: 'text', text: 'A__', marks: [{ type: 'bold' }] },
      { type: 'text', text: '_B', marks: [{ type: 'italic' }] },
    ])]) };
    expect(quizHasInlineBlank(question)).toBe(true);
    const html = renderToStaticMarkup(<QuizQuestionView question={question} fill={{ value: '42', readOnly: true }} />);
    expect(html.match(/class="fill-input"/g)).toHaveLength(1);
    expect(html).toContain('value="42"');
    expect(html).toContain('<strong>A</strong>');
    expect(html).toContain('<em>B</em>');
  });

  it('keeps adaptive image data but rejects unsafe links and image sources', () => {
    const contentJson = asDocument([
      { type: 'image', attrs: { src: '/api/files/image.png', monochrome: true, sourceDark: true, sourceBackground: '#000000' } },
      paragraph([{ type: 'text', text: 'bad link', marks: [{ type: 'link', attrs: { href: 'javascript:alert(1)' } }] }]),
      { type: 'image', attrs: { src: 'javascript:alert(1)' } },
    ]);
    const html = renderToStaticMarkup(<QuizQuestionView question={{ promptContentJson: contentJson }} />);
    expect(html).toContain('data-tf-mono="true"');
    expect(html).toContain('data-tf-mono-dark="true"');
    expect(html).toContain('src="/api/files/image.png"');
    expect(html).not.toContain('javascript:');
  });
});
