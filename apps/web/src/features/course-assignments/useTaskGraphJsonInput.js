import { useEffect, useRef, useState } from 'react';
import { summarizeTaskGraphPayload } from './courseTaskGraphJson';

export const MAX_JSON_IMPORT_FILE_BYTES = 24 * 1024 * 1024;

export default function useTaskGraphJsonInput({ notify, onChange }) {
  const [text, setText] = useState('');
  const [source, setSource] = useState('Текстовое поле');
  const [preview, setPreview] = useState('пусто');
  const [loading, setLoading] = useState(false);
  const fileSequence = useRef(0);
  const latestText = useRef('');

  useEffect(() => {
    if (!text.trim()) {
      setPreview('пусто');
      return undefined;
    }
    setPreview('Анализ JSON…');
    const timer = window.setTimeout(() => {
      try {
        setPreview(summarizeTaskGraphPayload(JSON.parse(text)));
      } catch {
        setPreview('JSON не читается');
      }
    }, 350);
    return () => window.clearTimeout(timer);
  }, [text]);

  const onTextChange = (value, nextSource = 'Текстовое поле') => {
    if (nextSource === 'Текстовое поле') fileSequence.current += 1;
    latestText.current = value;
    setText(value);
    setSource(nextSource);
    onChange?.();
  };

  const onPaste = (event) => {
    const pasted = event.clipboardData?.getData('text/plain');
    if (!pasted) {
      notify.warn('Браузер не передал текст из буфера. Для большого JSON используйте «Выбрать файл».');
    } else if (pasted === latestText.current) {
      notify.warn('Буфер содержит тот же JSON, который уже загружен. Проверьте источник или выберите новый файл.');
    } else {
      const before = latestText.current;
      window.setTimeout(() => {
        if (latestText.current === before)
          notify.warn('Вставка не заменила содержимое поля. Выберите JSON-файл напрямую — не используйте старый пакет.');
      }, 150);
    }
  };

  const onFile = async (file) => {
    if (!file) return;
    if (!/\.json$/i.test(file.name)) {
      notify.warn('Выберите файл с расширением .json.');
      return;
    }
    const sequence = ++fileSequence.current;
    setLoading(true);
    // The old package must never remain actionable when the next read fails.
    onTextChange('', 'Загрузка файла…');
    try {
      if (file.size > MAX_JSON_IMPORT_FILE_BYTES)
        throw new Error(`Файл превышает предел ${(MAX_JSON_IMPORT_FILE_BYTES / 1048576).toFixed(0)} МиБ. Разделите пакет на части.`);
      const value = await file.text();
      if (sequence !== fileSequence.current) return;
      if (!value.trim()) throw new Error('JSON-файл пуст');
      onTextChange(value, `Файл: ${file.name}`);
      notify.info(`JSON загружен: ${file.name} (${file.size.toLocaleString('ru-RU')} байт)`);
    } catch (error) {
      if (sequence === fileSequence.current)
        notify.error(`Не удалось загрузить JSON: ${error.message || 'ошибка чтения'}`);
    } finally {
      if (sequence === fileSequence.current) setLoading(false);
    }
  };

  return { text, source, preview, loading, onTextChange, onPaste, onFile };
}
