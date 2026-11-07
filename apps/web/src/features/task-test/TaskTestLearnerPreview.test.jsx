import React from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import TaskTestLearnerPreview from './TaskTestLearnerPreview';

const doc = JSON.stringify({ type: 'doc', content: [
  { type: 'image', attrs: { src: '/api/files/circle.png', width: 260, monochrome: true } },
] });

describe('author preview uses the learner quiz renderer', () => {
  it('shows the saved image size in both a question and an option without exposing the correct answer', () => {
    const html = renderToStaticMarkup(<TaskTestLearnerPreview questions={[{
      type: 'single-choice', promptContentJson: doc,
      options: [
        { key: 'a', contentJson: doc },
        { key: 'b', text: 'Нет' },
      ],
      correctOptionKeys: ['a'], optionsLayout: 'inline',
    }]} />);
    expect(html.match(/style="width:260px"/g)).toHaveLength(2);
    expect(html).toContain('data-tf-mono="true"');
    expect(html).toContain('type="radio"');
    expect(html).not.toContain('correctOptionKeys');
  });

  it('renders a single inline fill blank, just like a learner attempt', () => {
    const html = renderToStaticMarkup(<TaskTestLearnerPreview questions={[{
      type: 'fill', prompt: 'Ответ: ___', acceptedAnswers: ['42'],
    }]} />);
    expect(html.match(/class="fill-input"/g)).toHaveLength(1);
    expect(html).toMatch(/readonly=""/i);
  });
});
