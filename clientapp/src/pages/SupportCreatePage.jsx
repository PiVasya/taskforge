// clientapp/src/pages/SupportCreatePage.jsx
// Создание нового обращения.

import React, { useState } from 'react';
import { useNavigate } from 'react-router-dom';
import Layout from '../components/Layout';
import { Field, Select, Textarea, Button, Card } from '../components/ui';
import { createSupportTicket } from '../api/support';
import { useNotify } from '../components/notify/NotifyProvider';

export default function SupportCreatePage() {
  const nav = useNavigate();
  const notify = useNotify();
  const [type, setType] = useState('');
  const [message, setMessage] = useState('');
  const [sending, setSending] = useState(false);
  const [error, setError] = useState('');

  const submit = async (e) => {
    e.preventDefault();
    if (!type || !message.trim()) {
      const msg = 'Пожалуйста, заполните все поля.';
      setError(msg);
      notify.warn(msg);
      return;
    }
    try {
      setSending(true);
      setError('');
      const res = await createSupportTicket({ type, message: message.trim() });
      const ticketId = res?.ticketId || res?.id || res?.Id;
      notify.success('Обращение создано');

      // По требованиям: после создания возвращаем на главную,
      // чтобы пользователь мог потом открыть обращения и перейти в переписку.
      nav('/');
    } catch (err) {
      const msg = err?.message || 'Не удалось создать обращение';
      setError(msg);
      notify.error(msg);
    } finally {
      setSending(false);
    }
  };

  return (
    <Layout>
      <div className="max-w-xl mx-auto">
        <h1 className="text-2xl font-semibold mb-6">Новое обращение</h1>

        {error && <div className="text-red-500 mb-4">{error}</div>}

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

            <Field label="Сообщение" hint="Опишите проблему, вопрос или предложение максимально подробно.">
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
