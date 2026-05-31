import React, { useEffect, useMemo, useState } from 'react';
import { useLocation, useNavigate, useParams } from 'react-router-dom';
import Layout from '../components/Layout';
import { Button, Card, Badge } from '../components/ui';
import { getMyImageSolutionDetails } from '../api/imageSolutions';

function safeJsonParse(s) {
  try {
    return JSON.parse(s);
  } catch {
    return null;
  }
}

function toPercent(v) {
  if (v === null || v === undefined) return null;
  const n = Number(v);
  if (Number.isNaN(n)) return null;

  
  const normalized = (n >= 0 && n <= 1) ? (n * 100) : n;
  return Math.round(normalized * 10) / 10;
}

function pickSimilarity(obj) {
  return (
    obj?.similarity ??
    obj?.similarityPercent ??
    obj?.percent ??
    obj?.score ??
    null
  );
}

export default function AssignmentImageResultsPage() {
  const { assignmentId } = useParams();
  const navigate = useNavigate();
  const location = useLocation();

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
          if (cancelled) return;
          setData({
            assignmentTitle: d.assignmentTitle,
            isTrial: d.isTrial,
            passed: d.passed,
            similarityPercent: d.similarityPercent,
            thresholdPercent: d.thresholdPercent,
            referenceUrl: d.referenceUrl,
            submittedUrl: d.submittedUrl,
            stdout: d.stdout,
            stderr: d.stderr,
            runnerError: d.runnerError,
            createdAtUtc: d.createdAtUtc,
          });
          return;
        } catch {
          
        }
      }

      if (cancelled) return;
      setData(safeJsonParse(localStorage.getItem(storageKey)));
    }

    load();
    return () => {
      cancelled = true;
    };
  }, [storageKey, solutionId]);

  
  
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

  const percent = useMemo(() => toPercent(pickSimilarity(data)), [data]);
  const passed = data?.passed;
  const title = data?.assignmentTitle || data?.title || `Задание ${assignmentId}`;
  const expectedUrl = data?.expectedUrl || data?.referenceUrl;
  const actualUrl = data?.actualUrl || data?.submittedUrl || data?.submissionUrl;
  const isTrial = Boolean(data?.isTrial);

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
      <div className="max-w-6xl mx-auto px-4 py-6">
        <div className="flex items-center justify-between gap-3 mb-4">
          <div>
            <h1 className="text-2xl font-semibold">{title}</h1>
            <div className="flex items-center gap-2 mt-2">
              {isTrial && <Badge variant="secondary">Пробник</Badge>}
              {passed === true && <Badge variant="success">Пройдено</Badge>}
              {passed === false && <Badge variant="destructive">Не пройдено</Badge>}
              {percent !== null && (
                <span className="text-sm text-muted-foreground">
                  Совпадение: <span className="font-medium">{percent}%</span>
                </span>
              )}
            </div>
          </div>
          <Button onClick={onBack} variant="outline">Назад</Button>
        </div>

        {!data && (
          <Card className="p-6">
            <div className="text-sm text-muted-foreground">
              Нет данных сравнения. Вернись на страницу задания и нажми «Отправить».
            </div>
          </Card>
        )}

        {data && (
          <div className="grid grid-cols-1 lg:grid-cols-2 gap-4">
            <Card className="p-4">
              <div className="text-lg font-medium mb-2">Эталон</div>
              {expectedUrl ? (
                <img
                  src={expectedUrl}
                  alt="Эталон"
                  className="w-full rounded-lg border"
                />
              ) : (
                <div className="text-sm text-muted-foreground">Эталонное изображение недоступно</div>
              )}
            </Card>

            <Card className="p-4">
              <div className="text-lg font-medium mb-2">Что получилось</div>
              {actualUrl ? (
                <img
                  src={actualUrl}
                  alt="Результат"
                  className="w-full rounded-lg border"
                />
              ) : (
                <div className="text-sm text-muted-foreground">Изображение результата недоступно</div>
              )}
            </Card>
          </div>
        )}
      </div>
    </Layout>
  );
}
