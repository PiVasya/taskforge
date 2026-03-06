import React, { useEffect, useMemo, useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import Layout from '../components/Layout';
import { Card, Badge, Button } from '../components/ui';
import { ArrowLeft, Calendar, Tag, ExternalLink } from 'lucide-react';
import StatementViewer from '../components/tiptap/StatementViewer';
import { getUpdatesIndex, getUpdatePost } from '../api/updates';

function fmtDate(iso) {
  try {
    const d = new Date(iso);
    if (Number.isNaN(d.getTime())) return iso || '';
    return d.toLocaleDateString('ru-RU', { year: 'numeric', month: 'long', day: '2-digit' });
  } catch {
    return iso || '';
  }
}

export default function UpdatePostPage() {
  const { postId } = useParams();

  const [loading, setLoading] = useState(true);
  const [error, setError] = useState(null);

  const [meta, setMeta] = useState(null);
  const [contentJson, setContentJson] = useState('');

  useEffect(() => {
    (async () => {
      try {
        setLoading(true);
        setError(null);

        const idx = await getUpdatesIndex();
        const m = idx.find((x) => String(x.id) === String(postId));
        if (!m) {
          setError('Пост не найден');
          return;
        }
        setMeta(m);

        const dto = await getUpdatePost(m.file);
        setContentJson(dto?.contentJson || dto?.content || '');
      } catch (e) {
        setError('Не удалось загрузить пост');
      } finally {
        setLoading(false);
      }
    })();
  }, [postId]);

  const extraLinks = useMemo(() => {
    const links = meta?.links;
    if (!Array.isArray(links)) return [];
    return links
      .map((x) => ({ title: x?.title || x?.url, url: x?.url }))
      .filter((x) => x.url);
  }, [meta]);

  return (
    <Layout>
      <div className="flex flex-col gap-4">
        <div className="flex items-center justify-between gap-3">
          <Link to="/news" className="btn-outline">
            <ArrowLeft size={18} />
            <span className="ml-2">Назад</span>
          </Link>

          <Link to="/courses" className="btn-outline">
            Перейти к курсам
          </Link>
        </div>

        {loading && <div className="text-neutral-500">Загрузка…</div>}
        {error && <div className="text-red-500">{error}</div>}

        {!loading && !error && meta && (
          <>
            <Card>
              <div className="flex flex-col gap-3">
                <div className="text-2xl font-semibold">{meta.title}</div>

                <div className="flex flex-wrap gap-2 items-center text-sm text-neutral-500 dark:text-neutral-400">
                  <span className="inline-flex items-center gap-2">
                    <Calendar size={16} /> {fmtDate(meta.date)}
                  </span>

                  {meta?.pinned && <Badge variant="outline">PIN</Badge>}
                </div>

                {meta.summary && <div className="text-neutral-700 dark:text-neutral-200">{meta.summary}</div>}

                {(meta.tags || []).length > 0 && (
                  <div className="flex flex-wrap gap-2">
                    {(meta.tags || []).map((t) => (
                      <Badge key={t} variant="secondary">
                        <Tag size={14} className="mr-1" />#{t}
                      </Badge>
                    ))}
                  </div>
                )}

                {extraLinks.length > 0 && (
                  <div className="flex flex-col gap-1">
                    <div className="text-sm font-semibold opacity-80">Ссылки</div>
                    <div className="flex flex-wrap gap-2">
                      {extraLinks.map((l) => (
                        <a key={l.url} className="btn-outline" href={l.url} target="_blank" rel="noreferrer">
                          <ExternalLink size={18} />
                          <span className="ml-2">{l.title}</span>
                        </a>
                      ))}
                    </div>
                  </div>
                )}
              </div>
            </Card>

            <Card>
              <StatementViewer value={contentJson || ''} />
            </Card>
          </>
        )}
      </div>
    </Layout>
  );
}
