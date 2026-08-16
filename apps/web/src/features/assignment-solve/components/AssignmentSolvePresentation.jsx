import React, { useEffect, useState } from 'react';
import { Link, useNavigate } from 'react-router-dom';

import QuotaPill from '../../../components/QuotaPill';
import { Card, Button } from '../../../components/ui';
import IfEditor from '../../../components/IfEditor';
import StatementViewer from '../../../components/tiptap/StatementViewer';
import { ArrowLeft, BarChart3, ChevronDown, GitBranch, LockKeyhole } from 'lucide-react';
import { useSolveDraft } from '../solveDraftStore';

function displayText(value) {
  if (value == null) return '';
  return String(value);
}

function InputTextPreview({ value }) {
  const text = displayText(value);
  if (text === '') {
    return <pre className="whitespace-pre-wrap text-sm text-neutral-500 italic">Входные данные отсутствуют</pre>;
  }

  return <pre className="whitespace-pre-wrap text-sm">{text}</pre>;
}



function clampNumber(value, min, max) {
  return Math.min(max, Math.max(min, value));
}

function TypewriterText({ as: Tag = 'span', text, className = '', playKey = '', durationMs = 480, startDelay = 0, onDone }) {
  const source = String(text ?? '');
  const [visibleCount, setVisibleCount] = useState(0);
  const doneRef = React.useRef(onDone);

  useEffect(() => {
    doneRef.current = onDone;
  }, [onDone]);

  useEffect(() => {
    let frame = 0;
    let timer = 0;
    let startedAt = 0;
    let closed = false;
    const reduced = typeof window !== 'undefined' && window.matchMedia?.('(prefers-reduced-motion: reduce)')?.matches;
    const duration = Math.max(80, Number(durationMs) || 480);

    const finish = () => {
      if (closed) return;
      setVisibleCount(source.length);
      doneRef.current?.();
    };

    setVisibleCount(reduced ? source.length : 0);
    if (!source || reduced) {
      doneRef.current?.();
      return undefined;
    }

    const tick = (timestamp) => {
      if (closed) return;
      if (!startedAt) startedAt = timestamp;
      const progress = Math.min(1, (timestamp - startedAt) / duration);
      setVisibleCount(Math.min(source.length, Math.max(1, Math.ceil(source.length * progress))));
      if (progress >= 1) finish();
      else frame = window.requestAnimationFrame(tick);
    };

    timer = window.setTimeout(() => {
      frame = window.requestAnimationFrame(tick);
    }, Math.max(0, Number(startDelay) || 0));

    return () => {
      closed = true;
      window.clearTimeout(timer);
      window.cancelAnimationFrame(frame);
    };
  }, [source, playKey, durationMs, startDelay]);

  const done = visibleCount >= source.length;
  return (
    <Tag className={`typewriter-text ${done ? 'typewriter-text--done' : ''} ${className}`} aria-label={source}>
      <span aria-hidden="true">{source.slice(0, visibleCount)}</span>
      {!done && <span className="typewriter-cursor" aria-hidden="true" />}
    </Tag>
  );
}

function AnimatedHeading({ text, playKey, className = 'text-2xl font-semibold mb-1', onDone }) {
  const value = String(text ?? '');
  return (
    <TypewriterText
      as="h1"
      text={value}
      playKey={`${playKey}:title:${value}`}
      durationMs={clampNumber(value.length * 18, 180, 620)}
      className={className}
      onDone={onDone}
    />
  );
}

function SmoothHeightReveal({
  active = true,
  loading = false,
  playKey = '',
  collapsedHeight = 76,
  skeletonLines = 3,
  className = '',
  children,
  onDone,
}) {
  const contentRef = React.useRef(null);
  const doneRef = React.useRef(onDone);
  const [height, setHeight] = useState(collapsedHeight);
  const [ready, setReady] = useState(false);
  const [durationMs, setDurationMs] = useState(420);
  const shouldReveal = active && !loading;

  useEffect(() => {
    doneRef.current = onDone;
  }, [onDone]);

  React.useLayoutEffect(() => {
    let raf = 0;
    let timer = 0;
    let doneTimer = 0;
    let observer = null;
    const reduced = typeof window !== 'undefined' && window.matchMedia?.('(prefers-reduced-motion: reduce)')?.matches;

    setReady(false);
    setHeight(collapsedHeight);

    if (!shouldReveal) {
      return () => {};
    }

    const measure = () => {
      const node = contentRef.current;
      const nextHeight = Math.max(collapsedHeight, Math.ceil(node?.scrollHeight || collapsedHeight));
      const nextDuration = reduced ? 0 : clampNumber(280 + nextHeight * 0.28, 360, 860);
      setDurationMs(nextDuration);
      setHeight(nextHeight);
      return nextDuration;
    };

    timer = window.setTimeout(() => {
      raf = window.requestAnimationFrame(() => {
        const nextDuration = measure();
        setReady(true);
        doneTimer = window.setTimeout(() => doneRef.current?.(), nextDuration + 90);

        if (typeof ResizeObserver !== 'undefined' && contentRef.current) {
          observer = new ResizeObserver(() => measure());
          observer.observe(contentRef.current);
        }
      });
    }, reduced ? 0 : 35);

    return () => {
      window.clearTimeout(timer);
      window.clearTimeout(doneTimer);
      window.cancelAnimationFrame(raf);
      if (observer) observer.disconnect();
    };
  }, [shouldReveal, collapsedHeight, playKey]);

  return (
    <div
      className={`smooth-height-reveal ${shouldReveal ? 'smooth-height-reveal--content' : 'smooth-height-reveal--placeholder'} ${ready ? 'smooth-height-reveal--ready' : ''} ${className}`}
      style={{ height: `${height}px`, '--solve-reveal-duration': `${durationMs}ms` }}
    >
      <div ref={contentRef} className="smooth-height-reveal-inner">
        {shouldReveal ? children : <SolveSkeletonLines lines={skeletonLines} />}
      </div>
    </div>
  );
}

function AnimatedStatementViewer({ value, playKey, active = true, loading = false, collapsedHeight = 84, skeletonLines = 3, onDone }) {
  return (
    <SmoothHeightReveal
      active={active}
      loading={loading}
      playKey={`statement:${playKey}:${String(value || '').length}`}
      collapsedHeight={collapsedHeight}
      skeletonLines={skeletonLines}
      className="animated-statement-reveal"
      onDone={onDone}
    >
      <div className="animated-statement-rich">
        <StatementViewer value={value} />
      </div>
    </SmoothHeightReveal>
  );
}

function SolveSkeletonLines({ lines = 4, className = '' }) {
  return (
    <div className={`solve-skeleton-lines ${className}`} aria-hidden="true">
      {Array.from({ length: lines }).map((_, index) => (
        <div
          key={index}
          className="solve-skeleton-line tf-skeleton"
          style={{ width: `${Math.max(34, 92 - index * 11)}%` }}
        />
      ))}
    </div>
  );
}

function SolvePart({ loading = false, delay = 0, minHeight, className = '', children }) {
  return (
    <div
      className={`solve-part ${loading ? 'solve-part--loading' : 'solve-part--ready'} ${className}`}
      style={{ '--solve-part-delay': `${delay}ms`, minHeight }}
    >
      {children}
    </div>
  );
}


const AssignmentSolveHeader = React.memo(function AssignmentSolveHeader({
  courseId,
  assignmentId,
  isAdmin = false,
  showQuota = false,
}) {
  const navigate = useNavigate();
  const goBack = React.useCallback(() => {
    navigate(`/course/${courseId}`);
  }, [courseId, navigate]);

  return (
    <div className="flex items-center justify-between mb-6">
      <div className="flex items-center gap-2">
        <Button
          variant="ghost"
          className="inline-flex items-center gap-1"
          onClick={goBack}
        >
          <ArrowLeft size={16} /> к заданиям курса
        </Button>
      </div>
      <div className="flex items-center gap-2 flex-wrap">
        {showQuota ? <QuotaPill bucket="tasks" /> : null}
        {isAdmin ? (
          <Link to={`/admin/assignments/${assignmentId}/insights`} className="btn-outline">
            <BarChart3 size={16} className="mr-2" /> Аналитика задания
          </Link>
        ) : null}
        <IfEditor>
          <Link to={`/assignment/${assignmentId}/edit`} className="btn-outline">
            Редактировать
          </Link>
        </IfEditor>
      </div>
    </div>
  );
});

const NextAssignmentControl = React.memo(function NextAssignmentControl({ options = [], loading = false, disabled = false, onSelect }) {
  const [open, setOpen] = useState(false);
  const rootRef = React.useRef(null);
  const rows = Array.isArray(options) ? options : [];
  const only = rows.length === 1 ? rows[0] : null;
  const isDisabled = Boolean(disabled || loading || rows.length === 0 || (only && only.disabled));

  useEffect(() => {
    if (!open) return undefined;
    const onPointerDown = (event) => {
      if (!rootRef.current?.contains(event.target)) setOpen(false);
    };
    const onKeyDown = (event) => {
      if (event.key === 'Escape') setOpen(false);
    };
    document.addEventListener('pointerdown', onPointerDown, true);
    window.addEventListener('keydown', onKeyDown, true);
    return () => {
      document.removeEventListener('pointerdown', onPointerDown, true);
      window.removeEventListener('keydown', onKeyDown, true);
    };
  }, [open]);

  useEffect(() => {
    if (rows.length <= 1) setOpen(false);
  }, [rows.length]);

  const title = loading
    ? 'Загружаю продолжение'
    : only?.subtitle || only?.title || (rows.length ? 'Выбрать следующую ветку' : 'Продолжения нет');
  const buttonLabel = loading
    ? 'Загружаю продолжение…'
    : only?.disabled
      ? 'Продолжение закрыто'
      : rows.length === 0
        ? 'Продолжения нет'
        : 'Следующее задание';

  const activate = () => {
    if (isDisabled) return;
    if (rows.length === 1) {
      onSelect?.(only);
      return;
    }
    setOpen((value) => !value);
  };

  return (
    <div className="solve-next-control" ref={rootRef} title={title}>
      {open && rows.length > 1 ? (
        <div className="solve-next-menu" role="menu" aria-label="Следующие задания">
          <div className="solve-next-menu-title"><GitBranch size={14} /> Выберите ветку</div>
          <div className="solve-next-menu-list">
            {rows.map((option) => {
              const content = (
                <>
                  <span className={`solve-next-option-icon${option.disabled ? ' is-locked' : ''}`}>
                    {option.disabled ? <LockKeyhole size={14} /> : <GitBranch size={14} />}
                  </span>
                  <span className="solve-next-option-copy">
                    <strong>{option.title || 'Следующее задание'}</strong>
                    {option.subtitle ? <small>{option.subtitle}</small> : null}
                  </span>
                </>
              );

              if (!option.disabled && option.id) {
                return (
                  <Link
                    key={option.key || option.id || option.title}
                    to={`/assignment/${option.id}`}
                    role="menuitem"
                    className="solve-next-option"
                    onClick={() => setOpen(false)}
                  >
                    {content}
                  </Link>
                );
              }

              return (
                <button
                  key={option.key || option.id || option.title}
                  type="button"
                  role="menuitem"
                  className="solve-next-option"
                  disabled={option.disabled}
                  onClick={() => {
                    if (option.disabled) return;
                    setOpen(false);
                    onSelect?.(option);
                  }}
                >
                  {content}
                </button>
              );
            })}
          </div>
        </div>
      ) : null}
      {only?.id && !isDisabled ? (
        <Link
          className="btn-outline solve-action-button solve-next-button"
          to={`/assignment/${only.id}`}
          data-taskforge-automation-id="next-assignment"
          data-taskforge-agent-role="navigation-action"
          data-taskforge-agent-action="next-assignment"
          title={title}
          aria-label={`${buttonLabel}${title && title !== buttonLabel ? `. ${title}` : ''}`}
        >
          <span>{buttonLabel}</span>
        </Link>
      ) : (
        <Button
          className="solve-action-button solve-next-button"
          variant="outline"
          onClick={activate}
          data-taskforge-automation-id="next-assignment"
          data-taskforge-agent-role="navigation-action"
          data-taskforge-agent-action="next-assignment"
          disabled={isDisabled}
          title={title}
          aria-label={`${buttonLabel}${title && title !== buttonLabel ? `. ${title}` : ''}`}
          aria-expanded={rows.length > 1 ? open : undefined}
          aria-haspopup={rows.length > 1 ? 'menu' : undefined}
        >
          {only?.disabled ? <LockKeyhole size={15} /> : null}
          <span>{buttonLabel}</span>
          {rows.length > 1 ? <ChevronDown size={15} className={open ? 'is-open' : ''} /> : null}
        </Button>
      )}
    </div>
  );
});

const NextAssignmentDock = React.memo(function NextAssignmentDock({ nextOptions = [], nextLoading = false, nextDisabled = false, onNext }) {
  return (
    <>
      <div className="solve-action-dock-clearance" aria-hidden="true" />
      <div className="solve-action-dock solve-action-dock--navigation-only">
        <div className="solve-action-dock-panel">
          <NextAssignmentControl options={nextOptions} loading={nextLoading} disabled={nextDisabled} onSelect={onNext} />
        </div>
      </div>
    </>
  );
});

const SolveActionDock = React.memo(function SolveActionDock({
  nextOptions = [],
  nextLoading = false,
  nextDisabled = false,
  onNext,
  statusText = '',
  primaryLabel = 'Отправить решение',
  primaryIcon: PrimaryIcon = null,
  primaryDisabled = false,
  onPrimary,
  secondaryActions = [],
}) {
  const visibleStatus = String(statusText || '').trim();
  return (
    <>
      <div className="solve-action-dock-clearance" aria-hidden="true" />
      <div className="solve-action-dock">
        {visibleStatus && (
          <div className="solve-action-floating-status" aria-live="polite">
            {visibleStatus}
          </div>
        )}
        <div className="solve-action-dock-panel">
          <NextAssignmentControl
            options={nextOptions}
            loading={nextLoading}
            disabled={nextDisabled}
            onSelect={onNext}
          />
          {secondaryActions.map((action, index) => (
            <Button
              key={action.key || index}
              className="solve-action-button"
              variant={action.variant || 'outline'}
              onClick={action.onClick}
              disabled={action.disabled}
              title={action.title || action.label}
            >
              {action.icon || null}
              <span>{action.label}</span>
            </Button>
          ))}
          <Button
            className="solve-action-button solve-action-button--primary"
            onClick={onPrimary}
            data-taskforge-automation-id="submit-code-solution"
            data-taskforge-agent-role="solution-submit"
            data-taskforge-agent-action="submit-code-solution"
            disabled={primaryDisabled}
          >
            {PrimaryIcon ? <PrimaryIcon size={16} className="mr-1" /> : null}
            <span>{primaryLabel}</span>
          </Button>
        </div>
      </div>
    </>
  );
});

const SolveDraftActionDock = React.memo(function SolveDraftActionDock({
  assignmentId,
  disablePrimaryWhenEmpty = true,
  disableSecondaryWhenEmpty = false,
  primaryDisabled = false,
  secondaryActions = [],
  ...props
}) {
  const { code } = useSolveDraft(assignmentId);
  const codeIsEmpty = !String(code || '').trim();
  const resolvedSecondaryActions = React.useMemo(() => (
    secondaryActions.map((action) => ({
      ...action,
      disabled: Boolean(action.disabled || (disableSecondaryWhenEmpty && codeIsEmpty)),
    }))
  ), [codeIsEmpty, disableSecondaryWhenEmpty, secondaryActions]);

  return (
    <SolveActionDock
      {...props}
      primaryDisabled={Boolean(primaryDisabled || (disablePrimaryWhenEmpty && codeIsEmpty))}
      secondaryActions={resolvedSecondaryActions}
    />
  );
});

function AssignmentFirstLoadSkeleton() {
  return (
    <>
      <div className="solve-page-shell solve-page-shell--loading">
        <div className="flex items-center justify-between mb-6">
          <div className="solve-skeleton-pill w-36" />
          <div className="flex items-center gap-2">
            <div className="solve-skeleton-pill tf-skeleton w-32" />
            <div className="solve-skeleton-pill tf-skeleton w-28" />
          </div>
        </div>
        <div className="grid lg:grid-cols-3 gap-6">
          <Card className="lg:col-span-2 min-h-[420px] tf-skeleton-card">
            <SolveSkeletonLines lines={8} />
          </Card>
          <Card className="min-h-[420px] tf-skeleton-card">
            <SolveSkeletonLines lines={7} />
          </Card>
        </div>
      </div>
      <SolveActionDockSkeleton />
    </>
  );
}

function SolveActionDockSkeleton() {
  return (
    <SolveActionDock
      nextDisabled
      primaryDisabled
      statusText="Загружаю задание"
      primaryLabel="Отправить решение"
    />
  );
}


export {
  displayText,
  InputTextPreview,
  clampNumber,
  TypewriterText,
  AnimatedHeading,
  SmoothHeightReveal,
  AnimatedStatementViewer,
  SolveSkeletonLines,
  SolvePart,
  AssignmentSolveHeader,
  SolveActionDock,
  NextAssignmentDock,
  SolveDraftActionDock,
  AssignmentFirstLoadSkeleton,
  SolveActionDockSkeleton,
};
