import React from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import QuizAttemptReviewQuestions from './QuizAttemptReviewQuestions';
import { QuizQuestionView } from './QuizRichView';
import TaskTestLearnerPreview from './TaskTestLearnerPreview';

const doc = (...content) => JSON.stringify({ type: 'doc', content });
const paragraph = (...content) => ({ type: 'paragraph', content });

const explanation = doc(
  paragraph({ type: 'text', text: 'Так как' }, { type: 'mathInline', attrs: { kind: 'fraction', numerator: '1', denominator: '7' } }),
  { type: 'image', attrs: { src: '/api/files/diagram.png', width: 210, monochrome: true } },
);

function exampleQuestion(isCorrect) {
  return {
    id: isCorrect ? 'yes' : 'no', type: 'single-choice',
    prompt: 'Какое выражение верно?',
    optionsLayout: 'inline', options: [
      { key: 'a', text: '1' }, { key: 'b', text: '2' },
    ],
    userAnswer: { selectedOptionKey: isCorrect ? 'b' : 'a' },
    correctOptionKeys: ['b'], isCorrect,
    explanationContentJson: explanation,
  };
}

describe('post-submission Tiptap explanations', () => {
  it('renders the explanation underneath the correct answer for correct and incorrect results', () => {
    const html = renderToStaticMarkup(<QuizAttemptReviewQuestions questions={[
      exampleQuestion(true), exampleQuestion(false),
    ]} />);
    expect(html).toContain('Верно');
    expect(html).toContain('Неверно');
    expect(html.match(/Пояснение/g)).toHaveLength(2);
    expect(html.match(/\(правильный\)/g)).toHaveLength(2);
    expect(html).toContain('<mfrac>');
    expect(html).toContain('style="width:210px"');
    expect(html).toContain('data-tf-mono="true"');
    expect(html).not.toContain('contenteditable');
    // Explanations follow the answer options, not the prompt or the initial question.
    expect(html.indexOf('(правильный)')).toBeLessThan(html.indexOf('Пояснение'));
  });

  it('renders LaTeX expressions in the explanation only after a review is available', () => {
    const withLatex = { ...exampleQuestion(false), explanationContentJson: doc(
      paragraph({ type: 'text', text: 'По формуле ' }, { type: 'inlineMath', attrs: { latex: '\\frac{1}{7}' } }),
      { type: 'blockMath', attrs: { latex: '\\sum_{i=1}^{n}i^2' } },
    ) };
    const review = renderToStaticMarkup(<QuizAttemptReviewQuestions questions={[withLatex]} />);
    const learner = renderToStaticMarkup(<TaskTestLearnerPreview questions={[withLatex]} />);
    expect(review).toContain('data-type="inlineMath"');
    expect(review).toContain('data-type="blockMath"');
    expect(review).toContain('class="katex"');
    expect(review.indexOf('правильный')).toBeLessThan(review.indexOf('По формуле'));
    expect(learner).not.toContain('По формуле');
    expect(learner).not.toContain('katex');
  });

  it('shows accepted text answers even for inline fill blanks, with an optional explanation', () => {
    const html = renderToStaticMarkup(<QuizAttemptReviewQuestions questions={[{
      id: 'fill', type: 'fill', prompt: 'Вставьте ___', isCorrect: false,
      acceptedAnswers: ['ответ'], userAnswer: { text: 'другое' },
      explanationContentJson: doc(paragraph({ type: 'text', text: 'Правило' })),
    }]} />);
    expect(html).toContain('Правильные ответы:');
    expect(html).toContain('ответ');
    expect(html).toContain('Правило');
    expect(html).toContain('value="другое"');
  });

  it('does not show the explanation while taking the quiz or in learner preview', () => {
    const question = exampleQuestion(false);
    const standalone = renderToStaticMarkup(<QuizQuestionView question={question} />);
    const preview = renderToStaticMarkup(<TaskTestLearnerPreview questions={[question]} />);
    expect(standalone).not.toContain('Пояснение');
    expect(preview).not.toContain('Пояснение');
    expect(preview).not.toContain('Так как');
    expect(preview).not.toContain('correctOptionKeys');
  });

  it('omits the explanation block for previous questions with no explanation', () => {
    const question = { ...exampleQuestion(true), explanationContentJson: '' };
    const html = renderToStaticMarkup(<QuizAttemptReviewQuestions questions={[question]} />);
    expect(html).toContain('(правильный)');
    expect(html).not.toContain('Пояснение');
  });
});
