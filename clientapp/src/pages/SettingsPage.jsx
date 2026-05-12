import React, { useEffect, useMemo, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import Layout from '../components/Layout';
import { Card, Button } from '../components/ui';
import { getProfile } from '../api/profile';
import { getMyUiSettings, saveMyUiSettings } from '../api/uiSettings';
import AppErrorPanel from '../components/AppErrorPanel';
import { extractApiErrorMessages } from '../utils/handleApiError';

const LS_KEY = 'uiSettings';

function readLocal() {
  try {
    const raw = localStorage.getItem(LS_KEY);
    if (!raw) return null;
    return JSON.parse(raw);
  } catch {
    return null;
  }
}

function writeLocal(v) {
  
  localStorage.setItem('colorTheme', v?.colorTheme || localStorage.getItem('colorTheme') || 'pink');
  localStorage.setItem('mode', v?.mode || localStorage.getItem('mode') || 'dark');
  localStorage.setItem('bgFx', v?.bgFx ? '1' : '0');
  localStorage.setItem('fxMode', v?.fxMode || localStorage.getItem('fxMode') || 'random');
  localStorage.setItem('fxVariant', String(v?.fxVariant ?? localStorage.getItem('fxVariant') ?? '2'));
  localStorage.setItem('codeSolveLayout', v?.codeSolveLayout || localStorage.getItem('codeSolveLayout') || 'split');
  localStorage.setItem(LS_KEY, JSON.stringify(v));

  
  window.dispatchEvent(new Event('tf-ui-settings-changed'));
}

export default function SettingsPage() {
  const nav = useNavigate();

  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState(null);
  const [saved, setSaved] = useState(false);

  const [profileId, setProfileId] = useState(null);

  const [form, setForm] = useState(() =>
    readLocal() || {
      colorTheme: localStorage.getItem('colorTheme') || 'blue',
      mode: localStorage.getItem('mode') || 'light',
      bgFx: localStorage.getItem('bgFx') === '1',
      fxMode: localStorage.getItem('fxMode') || 'random',
      fxVariant: localStorage.getItem('fxVariant') || '2', 
      codeSolveLayout: localStorage.getItem('codeSolveLayout') || 'split',
    }
  );

  const fxOptions = useMemo(
    () => [
      { key: 'random', title: 'Случайный эффект при включении', desc: 'Каждый раз выбирается новый фон.' },
      { key: '0', title: 'Туман', desc: 'Мягкий туман и блёстки.' },
      { key: '1', title: 'Пыль + кометы', desc: 'Пыль, искры и редкие кометы.' },
      { key: '2', title: 'Нейронные связи', desc: 'Движущиеся точки и линии.' },
      { key: '3', title: 'Аврора', desc: 'Большие мягкие световые блики.' },
      { key: '4', title: 'Сердечки', desc: 'Плавающие сердечки на фоне.' },
      { key: '5', title: 'Matrix', desc: 'Падающие символы как в Матрице.' },
      { key: '6', title: 'Соты (мёд)', desc: 'Живые соты с мягкими волнами и искрами.' },
      { key: '7', title: 'Дым (вихри)', desc: 'Интерактивный дым с вихревыми завихрениями.' },
    ],
    []
  );

  useEffect(() => {
    (async () => {
      try {
        setLoading(true);
        setError(null);

        
        try {
          const p = await getProfile();
          setProfileId(p?.id ?? p?.userId ?? null);
        } catch {
          
        }

        
        try {
          const s = await getMyUiSettings();
          if (s && typeof s === 'object') {
            const merged = {
              colorTheme: s.colorTheme || form.colorTheme,
              mode: s.mode || form.mode,
              bgFx: !!s.bgFx,
              fxMode: s.fxMode || form.fxMode,
              fxVariant: String(s.fxVariant ?? form.fxVariant),
              codeSolveLayout: s.codeSolveLayout || form.codeSolveLayout || 'split',
            };
            setForm(merged);
            writeLocal(merged);
          }
        } catch {
          
        }
      } finally {
        setLoading(false);
      }
    })();
    
  }, []);

  const setField = (k, v) => {
    setSaved(false);
    setForm((p) => {
      const next = { ...p, [k]: v };
      
      if (k === 'colorTheme') localStorage.setItem('colorTheme', v);
      if (k === 'mode') localStorage.setItem('mode', v);
      if (k === 'bgFx') localStorage.setItem('bgFx', v ? '1' : '0');
      if (k === 'fxMode') localStorage.setItem('fxMode', v);
      if (k === 'fxVariant') localStorage.setItem('fxVariant', String(v));
      if (k === 'codeSolveLayout') localStorage.setItem('codeSolveLayout', v);
      writeLocal(next);
      
      window.dispatchEvent(new Event('tf-ui-settings-changed'));
      return next;
    });
  };

  const save = async () => {
    setSaving(true);
    setError(null);
    setSaved(false);
    try {
      const payload = {
        colorTheme: form.colorTheme,
        mode: form.mode,
        bgFx: !!form.bgFx,
        fxMode: form.fxMode,
        fxVariant: Number(form.fxVariant),
        codeSolveLayout: form.codeSolveLayout,
      };
      await saveMyUiSettings(payload);
      setSaved(true);
    } catch (e) {
      setError(extractApiErrorMessages(e, 'Не удалось сохранить настройки'));
    } finally {
      setSaving(false);
    }
  };

  return (
    <Layout>
      <div className="max-w-4xl mx-auto space-y-6">
        <div className="flex items-start justify-between gap-4">
          <div>
            <h1 className="text-2xl font-semibold">Настройки</h1>
            <div className="text-sm text-neutral-500 dark:text-neutral-400">Внешний вид и профиль</div>
          </div>
          <div className="flex gap-2">
            <Button variant="outline" onClick={() => nav('/profile')}>Редактировать профиль</Button>
            <Button
              variant="outline"
              disabled={!profileId}
              onClick={() => nav(`/users/${profileId}`)}
              title={!profileId ? 'Сначала загрузите профиль' : 'Открыть публичный профиль'}
            >
              Просмотреть профиль
            </Button>
          </div>
        </div>

        {loading ? <div>Загрузка…</div> : null}
        {error ? (<AppErrorPanel error={error} title="Настройки не сохранены" compact />) : null}
        {saved ? (
          <div className="text-sm text-emerald-600 bg-emerald-50 dark:bg-emerald-900/20 px-3 py-2 rounded-xl">
            Настройки сохранены
          </div>
        ) : null}

        <Card className="p-4 space-y-4">
          <div className="font-semibold">Тема</div>

          <div className="grid gap-3 md:grid-cols-2">
            <div className="space-y-2">
              <div className="text-sm text-neutral-500 dark:text-neutral-400">Режим</div>
              <div className="flex gap-2">
                <Button variant={form.mode === 'light' ? 'primary' : 'outline'} onClick={() => setField('mode', 'light')}>Светлая</Button>
                <Button variant={form.mode === 'dark' ? 'primary' : 'outline'} onClick={() => setField('mode', 'dark')}>Тёмная</Button>
              </div>
            </div>

            <div className="space-y-2">
              <div className="text-sm text-neutral-500 dark:text-neutral-400">Палитра</div>
              <div className="flex flex-wrap gap-2">
                <Button variant={form.colorTheme === 'blue' ? 'primary' : 'outline'} onClick={() => setField('colorTheme', 'blue')}>Синяя</Button>
                <Button variant={form.colorTheme === 'pink' ? 'primary' : 'outline'} onClick={() => setField('colorTheme', 'pink')}>Розовая</Button>
                <Button variant={form.colorTheme === 'apple' ? 'primary' : 'outline'} onClick={() => setField('colorTheme', 'apple')}>Яблоко</Button>
                <Button variant={form.colorTheme === 'red' ? 'primary' : 'outline'} onClick={() => setField('colorTheme', 'red')}>Красная</Button>
                <Button variant={form.colorTheme === 'honey' ? 'primary' : 'outline'} onClick={() => setField('colorTheme', 'honey')}>Мёд</Button>
              </div>
            </div>
          </div>
        </Card>

        <Card className="p-4 space-y-4">
          <div>
            <div className="font-semibold">Решение задач</div>
            <div className="text-sm text-neutral-500 dark:text-neutral-400">
              Выберите, как будет выглядеть страница решения задач с кодом.
            </div>
          </div>

          <div className="grid gap-3 md:grid-cols-2">
            <button
              type="button"
              onClick={() => setField('codeSolveLayout', 'split')}
              className={
                `rounded-2xl border p-4 text-left transition ` +
                (form.codeSolveLayout === 'split'
                  ? 'border-[rgb(var(--accent))] bg-[rgba(var(--accent)/0.10)]'
                  : 'border-[rgba(var(--border)/0.75)] bg-[rgba(var(--card)/0.60)]')
              }
            >
              <div className="font-medium">Как сейчас</div>
              <div className="text-sm text-neutral-500 dark:text-neutral-400">
                Условие слева (2/3), редактор справа (1/3).
              </div>
            </button>

            <button
              type="button"
              onClick={() => setField('codeSolveLayout', 'editorTop')}
              className={
                `rounded-2xl border p-4 text-left transition ` +
                (form.codeSolveLayout === 'editorTop'
                  ? 'border-[rgb(var(--accent))] bg-[rgba(var(--accent)/0.10)]'
                  : 'border-[rgba(var(--border)/0.75)] bg-[rgba(var(--card)/0.60)]')
              }
            >
              <div className="font-medium">Редактор сверху</div>
              <div className="text-sm text-neutral-500 dark:text-neutral-400">
                Поле кода сверху на всю ширину, условие и тесты снизу.
              </div>
            </button>
          </div>
        </Card>

        <Card className="p-4 space-y-4">
          <div className="flex items-center justify-between gap-4">
            <div>
              <div className="font-semibold">Фоновые эффекты</div>
              <div className="text-sm text-neutral-500 dark:text-neutral-400">Туман, пыль, нейросвязи и т.п.</div>
            </div>
            <Button variant={form.bgFx ? 'primary' : 'outline'} onClick={() => setField('bgFx', !form.bgFx)}>
              {form.bgFx ? 'Включено' : 'Выключено'}
            </Button>
          </div>

          <div className="grid gap-3 md:grid-cols-2">
            {fxOptions.map((o) => {
              const isRandom = o.key === 'random';
              const selected = isRandom
                ? form.fxMode === 'random'
                : form.fxMode === 'fixed' && String(form.fxVariant) === o.key;
              return (
                <button
                  key={o.key}
                  type="button"
                  className={
                    'text-left rounded-2xl border px-4 py-3 transition ' +
                    (selected
                      ? 'border-[color:var(--accent-2)] bg-[color:var(--card)] shadow-sm'
                      : 'border-neutral-200/70 dark:border-neutral-800/70 bg-white/40 dark:bg-neutral-900/20 hover:bg-white/60 dark:hover:bg-neutral-900/35')
                  }
                  onClick={() => {
                    if (isRandom) {
                      setField('fxMode', 'random');
                    } else {
                      setField('fxMode', 'fixed');
                      setField('fxVariant', o.key);
                    }
                  }}
                >
                  <div className="font-medium">{o.title}</div>
                  <div className="text-sm text-neutral-500 dark:text-neutral-400">{o.desc}</div>
                </button>
              );
            })}
          </div>
        </Card>

        <div className="flex items-center justify-end gap-2">
          <Button variant="outline" onClick={() => nav(-1)}>Назад</Button>
          <Button onClick={save} disabled={saving}>{saving ? 'Сохранение…' : 'Сохранить в аккаунт'}</Button>
        </div>
      </div>
    </Layout>
  );
}
