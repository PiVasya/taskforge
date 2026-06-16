


import React, { useState } from 'react';
import { useNavigate } from 'react-router-dom';
import Layout from '../components/Layout';
import { Field, Select, Textarea, Button, Card } from '../components/ui';
import { createSupportTicket } from '../api/support';
import { useNotify } from '../components/notify/NotifyProvider';
import AppErrorPanel from '../components/AppErrorPanel';
import { handleApiError } from '../utils/handleApiError';

export default function SupportCreatePage() {
  const nav = useNavigate();
  const notify = useNotify();
  const [type, setType] = useState('');
  const [message, setMessage] = useState('');
  const [sending, setSending] = useState(false);
  const [error, setError] = useState(null);

  const submit = async (e) => {
    e.preventDefault();
    if (!type || !message.trim()) {
      const parsed = {
        primaryMessage: 'Не все поля заполнены.',
        userHint: 'Без типа обращения и текста сообщение не отправится.',
        howToFix: ['Выберите тип обращения.', 'Введите текст сообщения.'],
        severity: 'validation',
        messages: ['Не все поля заполнены.'],
      };
      setError(parsed);
      notify.warn(parsed.primaryMessage);
      return;
    }
    try {
      setSending(true);
      setError(null);
      await createSupportTicket({ type, message: message.trim() });
      notify.success('Обращение создано');

      
      
      nav('/');
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
        <h1 className="text-2xl font-semibold mb-6">Новое обращение</h1>

        {error ? <div className="mb-4"><AppErrorPanel error={error} title="Обращение не создано" /></div> : null}

        <Card>
          <form onSubmit={submit} className="space-y-4">
            <Field label="Тип обращения">
              <Select value={type} onChange={(e) => setType(e.target.value)} required>
                <option value="" disabled>Выберите тип…</option>
                <option value="bug">Ошибка</option>
                <option value="question">Вопрос</option>
                <option value="suggestion">Предложение</option>
                <option value="other">Другое</option>
              </Select>
            </Field>

            <Field label="Сообщение">
              <Textarea
                value={message}
                onChange={(e) => setMessage(e.target.value)}
                rows={6}
                placeholder="Введите текст…"
                required
              />
            </Field>

            <div className="flex justify-end gap-2">
              <Button variant="outline" type="button" onClick={() => nav(-1)} disabled={sending}>
                Отмена
              </Button>
              <Button type="submit" disabled={sending}>
                {sending ? 'Отправка…' : 'Отправить'}
              </Button>
            </div>
          </form>
        </Card>
      </div>
    </Layout>
  );
}
