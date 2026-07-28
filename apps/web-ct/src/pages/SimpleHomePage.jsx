import React, { useCallback, useEffect, useMemo, useState } from "react";
import { Link, useNavigate } from "react-router-dom";
import {
  AlertTriangle,
  BookOpen,
  ChevronRight,
  Download,
  Loader2,
  PencilLine,
  Plus,
  RefreshCcw,
} from "lucide-react";
import { exportAllCtPdf } from "../utils/learningExport";
import {
  CT_PARTS,
  getCreatedSectionsByPart,
  getNextSectionCode,
  getSectionPath,
  normalizeSectionCode,
} from "../data/ctSections";
import { getLearningCourseTree } from "../api/learning";
import { getApiErrorMessage } from "../api/http";
import { useEditorMode } from "../contexts/EditorModeContext";
import {
  createCtSectionCourse,
  flattenCourses,
  findCourseBySection,
} from "../utils/ctCourseAdmin";

function AddSectionBox({
  part,
  suggestedCode,
  busy,
  error,
  onCancel,
  onCreate,
}) {
  const [value, setValue] = useState(suggestedCode);
  const normalized = normalizeSectionCode(value);
  const invalid =
    value.trim() && (!normalized || !normalized.startsWith(part.code));

  useEffect(() => {
    setValue(suggestedCode);
  }, [suggestedCode]);

  return (
    <div className="mb-5 rounded-[1.75rem] border-2 border-brand-300 bg-brand-50 p-4 shadow-soft dark:border-brand-800 dark:bg-brand-950/30 md:p-5">
      <div className="flex flex-col gap-4 lg:flex-row lg:items-end lg:justify-between">
        <div className="min-w-0 flex-1">
          <div className="text-sm font-black uppercase tracking-wide text-brand-700 dark:text-brand-300">
            Добавить номер в {part.title}
          </div>
          <label className="mt-3 block">
            <span className="text-sm font-semibold">Номер</span>
            <input
              value={value}
              onChange={(e) => setValue(e.target.value.toUpperCase())}
              placeholder={suggestedCode}
              className="mt-1 w-full rounded-3xl border border-brand-200 bg-white px-5 py-4 text-3xl font-black tracking-tight outline-none focus:border-brand-500 dark:border-brand-900 dark:bg-neutral-950"
            />
          </label>
          <p className="mt-2 text-sm leading-6 text-brand-900/75 dark:text-brand-100/75">
            Создаётся полноценный раздел. Только после этого он появится в списке
            номеров у учеников.
          </p>
          {invalid ? (
            <div className="mt-3 rounded-2xl border border-red-200 bg-red-50 p-3 text-sm font-semibold text-red-800 dark:border-red-900 dark:bg-red-950/30 dark:text-red-100">
              Для {part.title} нужен формат {part.code}1, {part.code}31, {" "}
              {part.code}100 и т.п.
            </div>
          ) : null}
          {error ? (
            <div className="mt-3 rounded-2xl border border-red-200 bg-red-50 p-3 text-sm text-red-800 dark:border-red-900 dark:bg-red-950/30 dark:text-red-100">
              {error}
            </div>
          ) : null}
        </div>
        <div className="flex flex-wrap gap-2 lg:justify-end">
          <button
            type="button"
            onClick={onCancel}
            className="btn-outline bg-white dark:bg-neutral-950"
          >
            Отмена
          </button>
          <button
            type="button"
            onClick={() => onCreate(normalized || value)}
            disabled={busy || invalid || !value.trim()}
            className="btn-primary inline-flex items-center gap-2 px-6 py-4 text-base disabled:opacity-60"
          >
            {busy ? (
              <Loader2 size={20} className="animate-spin" />
            ) : (
              <Plus size={20} />
            )}
            Создать {normalized || value}
          </button>
        </div>
      </div>
    </div>
  );
}

export default function SimpleHomePage() {
  const { canEdit, isEditorMode } = useEditorMode();
  const navigate = useNavigate();
  const [tree, setTree] = useState([]);
  const [busy, setBusy] = useState("tree");
  const [error, setError] = useState("");
  const [activeAddPart, setActiveAddPart] = useState("");
  const [exporting, setExporting] = useState(false);
  const [exportError, setExportError] = useState("");

  const includeDraft = canEdit && isEditorMode;
  const allCourses = useMemo(() => flattenCourses(tree), [tree]);

  const loadCourseTree = useCallback(async () => {
    setBusy("tree");
    setError("");
    try {
      const data = await getLearningCourseTree({ includeDraft });
      setTree(data || []);
    } catch (e) {
      setError(getApiErrorMessage(e, "Не удалось загрузить созданные номера."));
    } finally {
      setBusy("");
    }
  }, [includeDraft]);

  useEffect(() => {
    loadCourseTree();
    if (!canEdit || !isEditorMode) setActiveAddPart("");
  }, [canEdit, isEditorMode, loadCourseTree]);

  async function handleExportAll() {
    setExporting(true);
    setExportError("");
    try {
      await exportAllCtPdf(allCourses, { includeDraft });
    } catch (e) {
      setExportError(e?.message || "Не удалось подготовить PDF по частям A и B.");
    } finally {
      setExporting(false);
    }
  }

  async function createSection(partCode, rawCode) {
    const code = normalizeSectionCode(rawCode);
    if (!code || !code.startsWith(partCode)) {
      setError(
        `Номер должен быть в формате ${partCode}1, ${partCode}31, ${partCode}100 и т.п.`,
      );
      return;
    }

    setBusy(`create:${partCode}`);
    setError("");
    try {
      const existing = findCourseBySection(allCourses, code);
      if (!existing) await createCtSectionCourse(code, allCourses);
      await loadCourseTree();
      setActiveAddPart("");
      navigate(getSectionPath(code));
    } catch (e) {
      setError(getApiErrorMessage(e, `Не удалось создать раздел ${code}.`));
    } finally {
      setBusy("");
    }
  }

  return (
    <>
      <div className="min-h-[calc(100vh-57px)] bg-neutral-50 dark:bg-neutral-950">
        <div className="mx-auto max-w-6xl px-4 py-8 md:py-12">
          <section className="mb-8 rounded-[2rem] border border-neutral-200/80 bg-white p-6 text-center shadow-soft dark:border-neutral-800 dark:bg-neutral-900 md:p-10">
            <div className="mx-auto mb-4 grid h-14 w-14 place-items-center rounded-3xl bg-brand-50 text-brand-700 dark:bg-brand-900/20 dark:text-brand-100">
              <BookOpen size={28} />
            </div>
            <div className="text-sm font-bold uppercase tracking-[0.25em] text-brand-700 dark:text-brand-300">
              ЦТ / ЦЭ
            </div>
            <h1 className="mt-3 text-4xl font-black tracking-tight md:text-6xl">
              Выбери номер задания
            </h1>
            <p className="mx-auto mt-4 max-w-2xl text-lg text-neutral-600 dark:text-neutral-300">
              В списке только реально созданные номера: номер → HTML-конспект →
              случайные задания по этому же номеру.
            </p>
            <div className="mt-6 flex flex-wrap justify-center gap-3">
              <button
                type="button"
                onClick={handleExportAll}
                disabled={busy === "tree" || exporting || allCourses.length === 0}
                className="btn-primary inline-flex items-center gap-2 disabled:opacity-60"
              >
                {exporting ? <Loader2 size={18} className="animate-spin" /> : <Download size={18} />}
                Экспорт A+B в PDF
              </button>
            </div>
          </section>

          {exportError ? (
            <div className="mb-6 rounded-[1.5rem] border border-red-200 bg-red-50 p-4 text-sm text-red-800 shadow-soft dark:border-red-900 dark:bg-red-950/30 dark:text-red-100">
              <AlertTriangle size={17} className="mr-2 inline" />
              {exportError}
            </div>
          ) : null}

          {canEdit && isEditorMode ? (
            <div className="mb-6 rounded-[1.5rem] border border-brand-200 bg-brand-50 p-4 text-sm font-semibold text-brand-900 shadow-soft dark:border-brand-900 dark:bg-brand-950/30 dark:text-brand-100">
              <PencilLine size={18} className="mr-2 inline" />
              Режим редактора включён. Номера появляются в списке только после
              создания раздела через плюс в части A или B.
              <button
                type="button"
                onClick={loadCourseTree}
                disabled={busy === "tree"}
                className="ml-3 inline-flex items-center gap-1 rounded-full bg-white px-3 py-1 text-xs font-bold disabled:opacity-60 dark:bg-neutral-950"
              >
                {busy === "tree" ? (
                  <Loader2 size={13} className="animate-spin" />
                ) : (
                  <RefreshCcw size={13} />
                )}
                обновить
              </button>
            </div>
          ) : null}

          {error ? (
            <div className="mb-6 rounded-[1.5rem] border border-red-200 bg-red-50 p-4 text-sm text-red-800 shadow-soft dark:border-red-900 dark:bg-red-950/30 dark:text-red-100">
              <AlertTriangle size={17} className="mr-2 inline" />
              {error}
            </div>
          ) : null}

          {busy === "tree" ? (
            <div className="rounded-[2rem] border border-neutral-200 bg-white p-10 text-center shadow-soft dark:border-neutral-800 dark:bg-neutral-900">
              <Loader2 className="mx-auto animate-spin text-brand-600" />
              <div className="mt-3 text-neutral-600 dark:text-neutral-300">
                Загружаю созданные номера...
              </div>
            </div>
          ) : (
            <div className="space-y-6">
              {CT_PARTS.map((part) => {
                const sections = getCreatedSectionsByPart(
                  allCourses,
                  part.code,
                );
                const suggestedCode = getNextSectionCode(part.code, sections);
                const isAdding = activeAddPart === part.code;
                return (
                  <section
                    key={part.code}
                    className="rounded-[2rem] border border-neutral-200/80 bg-white p-5 shadow-soft dark:border-neutral-800 dark:bg-neutral-900 md:p-6"
                  >
                    <div className="mb-4 flex flex-wrap items-center justify-between gap-3">
                      <div>
                        <h2 className="text-3xl font-black tracking-tight">
                          {part.title}
                        </h2>
                        <p className="mt-1 max-w-3xl text-sm leading-6 text-neutral-600 dark:text-neutral-300">
                          {part.description}
                        </p>
                      </div>
                      <div className="flex flex-wrap items-center gap-2">
                        <div className="rounded-full bg-neutral-100 px-3 py-2 text-sm font-semibold text-neutral-500 dark:bg-neutral-950 dark:text-neutral-400">
                          создано: {sections.length}
                        </div>
                        {canEdit && isEditorMode ? (
                          <button
                            type="button"
                            onClick={() =>
                              setActiveAddPart(isAdding ? "" : part.code)
                            }
                            className="inline-flex items-center gap-2 rounded-3xl bg-brand-600 px-5 py-3 text-base font-black text-white shadow-soft transition hover:bg-brand-700"
                          >
                            <Plus size={20} /> Добавить {part.code}
                          </button>
                        ) : null}
                      </div>
                    </div>

                    {isAdding ? (
                      <AddSectionBox
                        part={part}
                        suggestedCode={suggestedCode}
                        busy={busy === `create:${part.code}`}
                        error={error}
                        onCancel={() => setActiveAddPart("")}
                        onCreate={(code) => createSection(part.code, code)}
                      />
                    ) : null}

                    {sections.length === 0 ? (
                      <div className="rounded-[1.5rem] border border-dashed border-neutral-200 bg-neutral-50 p-5 text-sm text-neutral-500 dark:border-neutral-800 dark:bg-neutral-950 dark:text-neutral-400">
                        В {part.title.toLowerCase()} пока нет созданных номеров.
                        {canEdit && isEditorMode
                          ? ` Нажми «Добавить ${part.code}», чтобы создать первый.`
                          : ""}
                      </div>
                    ) : (
                      <div className="grid grid-cols-3 gap-3 sm:grid-cols-5 md:grid-cols-6 lg:grid-cols-10">
                        {sections.map((section) => (
                          <Link
                            key={section.code}
                            to={getSectionPath(section.code)}
                            className="group rounded-3xl border border-neutral-200 bg-neutral-50 p-4 text-center transition hover:-translate-y-0.5 hover:border-brand-300 hover:bg-white hover:shadow-md dark:border-neutral-800 dark:bg-neutral-950 dark:hover:border-brand-700 dark:hover:bg-neutral-900"
                          >
                            <div className="text-2xl font-black tracking-tight text-brand-700 dark:text-brand-200">
                              {section.code}
                            </div>
                            <div className="mt-2 inline-flex items-center gap-1 text-xs font-semibold text-neutral-500 group-hover:text-brand-700 dark:text-neutral-400 dark:group-hover:text-brand-200">
                              {canEdit && isEditorMode
                                ? "Открыть и править"
                                : "Открыть"}{" "}
                              <ChevronRight size={13} />
                            </div>
                            {canEdit && isEditorMode &&
                            !section.isPublished ? (
                              <div className="mt-2 rounded-full bg-amber-50 px-2 py-1 text-[10px] font-black uppercase tracking-wide text-amber-700 dark:bg-amber-950/40 dark:text-amber-200">
                                черновик
                              </div>
                            ) : null}
                          </Link>
                        ))}
                      </div>
                    )}
                  </section>
                );
              })}
            </div>
          )}
        </div>
      </div>
    </>
  );
}
