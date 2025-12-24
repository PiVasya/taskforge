import React, { useState } from 'react';
import { useNavigate } from 'react-router-dom';
import Layout from '../components/Layout';
import { Field, Textarea, Select, Button, Card } from '../components/ui';
import { createSupportTicket } from '../api/support';

export default function SupportCreatePage() {
  const nav = useNavigate();
  const [type, setType] = useState('');
  const [message, setMessage] = useState('');
  const [sending, setSending] = useState(false);
  const [error, setError] = useState('');

  const submit = async (e) => {
    e.preventDefault();
    if (!type || !message.trim()) {
      setError('Пожалуйста, заполните все поля.');
      return;
    }
    try {
      setSending(true);
      setError('');
      const { ticketId } = await createSupportTicket({ type, message });
      alert('Ваше обращение создано ✅');
      nav(`/support/${ticketId}`);
    } catch (err) {
      setError(err.message || 'Не удалось отправить обращение');
    } finally {
      setSending(false);
    }
  };

  return (
    <Layout>
      <div className="max-w-xl mx-auto">
        <h1 className="text-2xl font-semibold mb-6">Новое обращение</h1>
        {error && <div className="mb-4 text-red-500">{error}</div>}
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
                placeholder="Опишите вашу проблему или предложение…"
                required
              />
            </Field>
            <div className="flex gap-2 justify-end">
              <Button variant="outline" type="button" onClick={() => nav(-1)} disabled={sending}>Отмена</Button>
              <Button type="submit" disabled={sending}>{sending ? 'Отправка…' : 'Отправить'}</Button>
            </div>
          </form>
        </Card>
      </div>
    </Layout>
  );
}
