import React, { useEffect, useMemo, useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import Layout from '../components/Layout';
import { Badge } from '../components/ui';
import { ArrowLeft, ExternalLink, Tag } from 'lucide-react';
import StatementViewer from '../components/tiptap/StatementViewer';
import ChangelogShowcase from '../components/ChangelogShowcase';
import { getUpdatesIndex, getUpdatePost } from '../api/updates';
import { useAuth } from '../auth/AuthContext';

export default function UpdatePostPage() {
  const { postId } = useParams();
  const { access } = useAuth();
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState(null);
  const [meta, setMeta] = useState(null);
  const [postData, setPostData] = useState(null);
  const [contentJson, setContentJson] = useState('');

  useEffect(() => {
    (async () => {
      try {
        setLoading(true);
        setError(null);

        const index = await getUpdatesIndex();
        const currentMeta = index.find((item) => String(item.id) === String(postId));
        if (!currentMeta) {
          setError('Пост не найден');
          return;
        }
        setMeta(currentMeta);

        const dto = await getUpdatePost(currentMeta.file);
        setPostData(dto || null);

        let nextValue = '';
        const rawContent = dto?.contentJson ?? dto?.content;
        if (typeof rawContent === 'string') {
          nextValue = rawContent;
        } else if (dto && typeof dto === 'object' && dto.type === 'doc') {
          try {
            nextValue = JSON.stringify(dto);
          } catch {
            nextValue = '';
          }
        }
        setContentJson(nextValue);
      } catch {
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
      .map((item) => ({ title: item?.title || item?.url, url: item?.url }))
      .filter((item) => item.url);
  }, [meta]);

  const isShowcase = postData?.layout === 'showcase';

  return (
    <Layout>
      <div className="mx-auto flex max-w-[1540px] flex-col gap-5 pb-10">
        <div className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
          <Link to="/news" className="btn-outline self-start">
            <ArrowLeft size={18} />
            <span>Назад в ленту</span>
          </Link>

          <Link to={access ? "/courses" : "/register"} className="btn-outline self-start sm:self-auto">
            {access ? "Перейти к курсам" : "Создать аккаунт"}
          </Link>
        </div>

        {loading ? <div className="text-neutral-500">Загрузка…</div> : null}
        {error ? <div className="text-red-500">{error}</div> : null}

        {!loading && !error && meta && isShowcase ? (
          <ChangelogShowcase data={postData} meta={meta} />
        ) : null}

        {!loading && !error && meta && !isShowcase ? (
          <>
            <div className="card p-5">
              <div className="flex flex-col gap-3">
                <div className="text-2xl font-semibold sm:text-3xl">{meta.title}</div>

                {meta.summary ? (
                  <div className="max-w-4xl text-neutral-700 dark:text-neutral-200">
                    {meta.summary}
                  </div>
                ) : null}

                {(meta.tags || []).length > 0 ? (
                  <div className="flex flex-wrap gap-2">
                    {(meta.tags || []).map((tag) => (
                      <Badge key={tag} variant="secondary">
                        <Tag size={14} className="mr-1" />#{tag}
                      </Badge>
                    ))}
                  </div>
                ) : null}

                {extraLinks.length > 0 ? (
                  <div className="flex flex-col gap-1">
                    <div className="text-sm font-semibold opacity-80">Ссылки</div>
                    <div className="flex flex-wrap gap-2">
                      {extraLinks.map((link) => (
                        <a key={link.url} className="btn-outline" href={link.url} target="_blank" rel="noreferrer">
                          <ExternalLink size={18} />
                          <span>{link.title}</span>
                        </a>
                      ))}
                    </div>
                  </div>
                ) : null}
              </div>
            </div>

            <div className="card p-5">
              <StatementViewer value={contentJson || ''} />
            </div>
          </>
        ) : null}
      </div>
    </Layout>
  );
}
