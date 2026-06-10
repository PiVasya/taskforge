import React, { useEffect, useMemo, useState } from 'react';
import { useLocation, useNavigate, useParams } from 'react-router-dom';
import Layout from '../components/Layout';
import { Button, Card, Badge } from '../components/ui';
import { getMyImageSolutionDetails } from '../api/imageSolutions';
import { useNotify } from '../components/notify/NotifyProvider';
import { handleApiError } from '../utils/handleApiError';
import {
  formatDateTime,
  getImageReferenceUrl,
  getImageSimilarityPercent,
  getImageSubmittedUrl,
  getImageThresholdPercent,
  getImageTitle,
  getSolutionStreams,
  getSolutionSubmittedAt,
} from '../utils/solutionsView';

function safeJsonParse(s) {
  try {
    return JSON.parse(s);
  } catch {
    return null;
  }
}

function pickPercent(data) {
  return getImageSimilarityPercent(data);
}

export default function AssignmentImageResultsPage() {
  const { assignmentId } = useParams();
  const navigate = useNavigate();
  const location = useLocation();
  const notify = useNotify();

  const storageKey = useMemo(() => `image-results:${assignmentId}`, [assignmentId]);
  const [data, setData] = useState(null);

  const solutionId = useMemo(() => {
    const sp = new URLSearchParams(location.search);
    const v = sp.get('solutionId');
    return v || null;
  }, [location.search]);

  useEffect(() => {
    let cancelled = false;

    async function load() {
      if (solutionId) {
        try {
          const d = await getMyImageSolutionDetails(solutionId);
          if (!cancelled) {
            setData(d);
            try { localStorage.setItem(storageKey, JSON.stringify(d)); } catch {}
          }
          return;
        } catch (e) {
          if (!cancelled) handleApiError(e, notify, 'Не удалось загрузить image-решение');
        }
      }

      if (!cancelled) setData(safeJsonParse(localStorage.getItem(storageKey)));
    }

    load();
    return () => {
      cancelled = true;
    };
  }, [storageKey, solutionId, notify]);

  useEffect(() => {
    try { window.history.pushState({ tf_close_on_back: true }, '', window.location.href); } catch {}

    const onPop = () => {
      try { window.close(); } catch {}
      setTimeout(() => {
        try {
          if (window.history.length > 1) navigate(-1);
          else navigate(`/assignment/${assignmentId}`);
        } catch {
          navigate(`/assignment/${assignmentId}`);
        }
      }, 50);
    };

    window.addEventListener('popstate', onPop);
    return () => window.removeEventListener('popstate', onPop);
  }, [navigate, assignmentId]);

  const percent = useMemo(() => pickPercent(data), [data]);
  const threshold = getImageThresholdPercent(data);
  const passed = data?.passed ?? data?.Passed;
  const title = data ? getImageTitle(data) : `Задание ${assignmentId}`;
  const expectedUrl = getImageReferenceUrl(data);
  const actualUrl = getImageSubmittedUrl(data);
  const isTrial = Boolean(data?.isTrial);
  const submittedAt = getSolutionSubmittedAt(data);
  const streams = getSolutionStreams(data);

  const onBack = () => {
    try { window.close(); } catch {}
    setTimeout(() => {
      try {
        if (window.history.length > 1) navigate(-1);
        else navigate(`/assignment/${assignmentId}`);
      } catch {
        navigate(`/assignment/${assignmentId}`);
      }
    }, 50);
  };

  return (
    <Layout>
      <div className="max-w-6xl mx-auto px-4 py-6 space-y-4">
        <div className="flex items-center justify-between gap-3">
          <div>
            <h1 className="text-2xl font-semibold">{title}</h1>
            <div className="flex flex-wrap items-center gap-2 mt-2">
              {isTrial ? <Badge variant="secondary">Пробник</Badge> : null}
              {passed === true ? <Badge variant="success">Пройдено</Badge> : null}
              {passed === false ? <Badge variant="destructive">Не пройдено</Badge> : null}
              {percent !== null ? <Badge variant="secondary">Совпадение: {Math.round(percent)}%</Badge> : null}
              {threshold !== null ? <Badge variant="secondary">Порог: {Math.round(threshold)}%</Badge> : null}
              {submittedAt ? <span className="text-sm text-neutral-500">{formatDateTime(submittedAt)}</span> : null}
            </div>
          </div>
          <Button onClick={onBack} variant="outline">Назад</Button>
        </div>

        {!data ? (
          <Card className="p-6">
            <div className="text-sm text-neutral-500">
              Нет данных сравнения. Вернись на страницу задания и нажми «Отправить».
            </div>
          </Card>
        ) : (
          <>
            <div className="grid grid-cols-1 lg:grid-cols-2 gap-4">
              <Card className="p-4">
                <div className="text-lg font-medium mb-2">Эталон</div>
                {expectedUrl ? (
                  <img src={expectedUrl} alt="Эталон" className="w-full rounded-lg border" />
                ) : (
                  <div className="text-sm text-neutral-500">Эталонное изображение недоступно</div>
                )}
              </Card>

              <Card className="p-4">
                <div className="text-lg font-medium mb-2">Что получилось</div>
                {actualUrl ? (
                  <img src={actualUrl} alt="Результат" className="w-full rounded-lg border" />
                ) : (
                  <div className="text-sm text-neutral-500">Изображение результата недоступно</div>
                )}
              </Card>
            </div>

            {(streams?.message || streams?.runnerError || streams?.stdout || streams?.stderr) ? (
              <Card className="p-4 space-y-3">
                {streams.message ? <div className="text-sm text-neutral-600 dark:text-neutral-300">{streams.message}</div> : null}
                {streams.runnerError ? <pre className="text-sm whitespace-pre-wrap text-rose-700 dark:text-rose-300">{streams.runnerError}</pre> : null}
                {streams.stdout ? <pre className="text-xs whitespace-pre-wrap">stdout\n{streams.stdout}</pre> : null}
                {streams.stderr ? <pre className="text-xs whitespace-pre-wrap text-rose-700 dark:text-rose-300">stderr\n{streams.stderr}</pre> : null}
              </Card>
            ) : null}
          </>
        )}
      </div>
    </Layout>
  );
}
