import React, { useMemo } from 'react';
import { Link, useParams } from 'react-router-dom';
import { Badge } from '../components/ui';
import { ArrowLeft, ExternalLink, Tag } from 'lucide-react';
import StatementViewer from '../components/tiptap/StatementViewer';
import ChangelogShowcase from '../components/ChangelogShowcase';
import { getUpdatesIndex, getUpdatePost } from '../api/updates';
import { useAuth } from '../auth/AuthContext';
import useQuery from '../hooks/useQuery';

export default function UpdatePostPage() {
  const { postId } = useParams();
  const { access } = useAuth();
  const indexQuery = useQuery({
    queryKey: ['updates', 'index'],
    queryFn: getUpdatesIndex,
    staleTime: 5 * 60_000,
    keepPreviousData: true,
  });
  const meta = useMemo(() => (
    (Array.isArray(indexQuery.data) ? indexQuery.data : []).find((item) => String(item.id) === String(postId)) || null
  ), [indexQuery.data, postId]);
  const postQuery = useQuery({
    queryKey: ['updates', 'post', meta?.file || postId],
    queryFn: () => getUpdatePost(meta.file),
    enabled: Boolean(meta?.file),
    staleTime: 5 * 60_000,
    keepPreviousData: true,
  });
  const postData = postQuery.data || null;
  const loading = indexQuery.isLoading || (Boolean(meta?.file) && postQuery.isLoading);
  const error = indexQuery.error || postQuery.error
    ? 'Не удалось загрузить пост'
    : (!indexQuery.isLoading && indexQuery.data && !meta ? 'Пост не найден' : null);
  const contentJson = useMemo(() => {
    const rawContent = postData?.contentJson ?? postData?.content;
    if (typeof rawContent === 'string') return rawContent;
    if (postData && typeof postData === 'object' && postData.type === 'doc') {
      try { return JSON.stringify(postData); } catch { return ''; }
    }
    return '';
  }, [postData]);

  const extraLinks = useMemo(() => {
    const links = meta?.links;
    if (!Array.isArray(links)) return [];
    return links
      .map((item) => ({ title: item?.title || item?.url, url: item?.url }))
      .filter((item) => item.url);
  }, [meta]);

  const isShowcase = postData?.layout === 'showcase';

  return (
    <>
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
    </>
  );
}
