// clientapp/src/pages/SupportPage.jsx
// Страница связи с техподдержкой: выбор типа вопроса и текст сообщения.

import React, { useState } from 'react';
import { useNavigate } from 'react-router-dom';
import Layout from '../components/Layout';
import { Field, Textarea, Select, Button } from '../components/ui';
import AppErrorPanel from '../components/AppErrorPanel';
import { createSupportTicket } from '../api/support';
import { handleApiError } from '../utils/handleApiError';
import { useNotify } from '../components/notify/NotifyProvider';

export default function SupportPage() {
  const nav = useNavigate();
  const notify = useNotify();
  const [type, setType] = useState('');
  const [message, setMessage] = useState('');
  const [sending, setSending] = useState(false);
  const [error, setError] = useState(null);

  const submit = async (e) => {
    e.preventDefault();
    if (!type || !message.trim()) {
      setError({
        primaryMessage: 'Не все поля заполнены.',
        userHint: 'Чтобы создать обращение, нужно выбрать тип и написать сообщение.',
        howToFix: ['Выберите тип обращения.', 'Опишите проблему или вопрос в поле сообщения.'],
        severity: 'validation',
        messages: ['Не все поля заполнены.'],
      });
      return;
    }
    try {
      setSending(true);
      setError(null);
      await createSupportTicket({ type, message: message.trim() });
      notify.success('Обращение создано. Теперь вы сможете продолжить переписку в разделе поддержки.');
      nav(-1);
    } catch (err) {
      const parsed = handleApiError(err, notify, 'Не удалось создать обращение');
      setError(parsed);
    } finally {
      setSending(false);
    }
  };

  return (
    <Layout>
      <div className="max-w-xl mx-auto">
        <h1 className="text-2xl font-semibold mb-6">Связь с техподдержкой</h1>
        <p className="mb-6 text-neutral-600 dark:text-neutral-400 text-sm">
          Если у вас вопрос, вы нашли ошибку или хотите предложить улучшение —
          заполните форму ниже. Мы постараемся ответить как можно скорее.
        </p>
        {error ? <div className="mb-4"><AppErrorPanel error={error} title="Обращение не создано" /></div> : null}
        <form onSubmit={submit} className="space-y-5">
          <Field label="Тип обращения">
            <Select value={type} onChange={(e) => setType(e.target.value)} required>
              <option value="" disabled>Выберите тип…</option>
              <option value="bug">Ошибка</option>
              <option value="question">Вопрос</option>
              <option value="suggestion">Предложение</option>
              <option value="other">Другое</option>
            </Select>
          </Field>
          <Field label="Сообщение" hint="Опишите проблему, вопрос или предложение максимально подробно.">
            <Textarea
              value={message}
              onChange={(e) => setMessage(e.target.value)}
              rows={6}
              placeholder="Опишите вашу проблему или предложение…"
              required
            />
          </Field>
          <div className="flex gap-3">
            <Button type="submit" disabled={sending}>
              {sending ? 'Отправка…' : 'Отправить'}
            </Button>
            <Button
              type="button"
              variant="outline"
              onClick={() => nav(-1)}
              disabled={sending}
            >
              Отмена
            </Button>
          </div>
        </form>
      </div>
    </Layout>
  );
}
