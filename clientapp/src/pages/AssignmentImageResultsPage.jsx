import React, { useEffect, useMemo, useState } from 'react';
import { useNavigate, useParams } from 'react-router-dom';
import Layout from '../components/Layout';
import { Button, Card, Badge } from '../components/ui';

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

  // Some backends return 0..1, some 0..100
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

  const storageKey = useMemo(() => `image-results:${assignmentId}`, [assignmentId]);
  const [data, setData] = useState(null);

  useEffect(() => {
    setData(safeJsonParse(localStorage.getItem(storageKey)));
  }, [storageKey]);

  const percent = useMemo(() => toPercent(pickSimilarity(data)), [data]);
  const passed = data?.passed;
  const title = data?.assignmentTitle || data?.title || `Задание ${assignmentId}`;
  const expectedUrl = data?.expectedUrl || data?.referenceUrl;
  const actualUrl = data?.actualUrl || data?.submittedUrl || data?.submissionUrl;

  const onBack = () => {
    // If opened as a new tab/window from the solve page, allow simple close
    if (window.opener) {
      window.close();
      return;
    }
    navigate(`/assignment/${assignmentId}`);
  };

  return (
    <Layout>
      <div className="max-w-6xl mx-auto px-4 py-6">
        <div className="flex items-center justify-between gap-3 mb-4">
          <div>
            <h1 className="text-2xl font-semibold">{title}</h1>
            <div className="flex items-center gap-2 mt-2">
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
