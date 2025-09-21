import React, { useEffect, useState, useMemo } from "react";
import { useParams, Link, useNavigate } from "react-router-dom";
import { getAssignmentsByCourse, createAssignment } from "../../api/assignments";

function CoursePage() {
    const { courseId } = useParams();
    const navigate = useNavigate();

    const [assignments, setAssignments] = useState([]);
    const [loading, setLoading] = useState(false);
    const [err, setErr] = useState("");

    // форма: поля задания
    const [title, setTitle] = useState("");
    const [description, setDescription] = useState("");
    const [difficulty, setDifficulty] = useState(1);
    const [tags, setTags] = useState("");

    // тесты
    const [testCases, setTestCases] = useState([{ input: "", expectedOutput: "", isHidden: false }]);

    // фильтр/поиск по списку (приятно иметь)
    const [q, setQ] = useState("");

    useEffect(() => {
        load();
        // eslint-disable-next-line
    }, [courseId]);

    async function load() {
        try {
            setLoading(true);
            setErr("");
            const data = await getAssignmentsByCourse(courseId);
            setAssignments(data);
        } catch (e) {
            setErr(e.message || "Ошибка загрузки");
        } finally {
            setLoading(false);
        }
    }

    function addTestCase() {
        setTestCases([...testCases, { input: "", expectedOutput: "", isHidden: false }]);
    }

    function updateTestCase(i, field, value) {
        const copy = [...testCases];
        copy[i][field] = field === "isHidden" ? !!value : value;
        setTestCases(copy);
    }

    function removeTestCase(i) {
        const copy = [...testCases];
        copy.splice(i, 1);
        setTestCases(copy.length ? copy : [{ input: "", expectedOutput: "", isHidden: false }]);
    }

    async function handleCreate() {
        if (!title.trim()) {
            setErr("Укажите название задания");
            return;
        }
        if (!description.trim()) {
            setErr("Укажите описание задания");
            return;
        }
        if (!testCases.length || testCases.some(tc => !tc.input && !tc.expectedOutput)) {
            setErr("Добавьте хотя бы один корректный тест");
            return;
        }

        try {
            setErr("");
            await createAssignment(courseId, {
                title: title.trim(),
                description: description.trim(),
                difficulty,
                tags: tags.trim(),
                testCases
            });

            // очистим форму и обновим список
            setTitle(""); setDescription(""); setDifficulty(1); setTags("");
            setTestCases([{ input: "", expectedOutput: "", isHidden: false }]);
            await load();
        } catch (e) {
            setErr(e.message || "Не удалось создать задание");
        }
    }

    const filtered = useMemo(() => {
        if (!q.trim()) return assignments;
        const needle = q.toLowerCase();
        return assignments.filter(a =>
            (a.title || "").toLowerCase().includes(needle) ||
            (a.description || "").toLowerCase().includes(needle) ||
            (a.tags || "").toLowerCase().includes(needle)
        );
    }, [assignments, q]);

    return (
        <div style={{ maxWidth: 1100, margin: "0 auto", padding: "1rem" }}>
            <h2>Задания курса</h2>

            <div style={{ display: "grid", gridTemplateColumns: "1fr 420px", gap: 16 }}>
                {/* левая колонка — список */}
                <div>
                    <div style={{ marginBottom: 8 }}>
                        <input
                            placeholder="Поиск по заданиям…"
                            value={q}
                            onChange={e => setQ(e.target.value)}
                            style={{ width: "100%" }}
                        />
                    </div>

                    {loading && <div>Загрузка…</div>}
                    {err && <div style={{ color: "crimson" }}>{err}</div>}
                    {!loading && filtered.length === 0 && <div>Пока заданий нет</div>}

                    <ul style={{ paddingLeft: 16 }}>
                        {filtered.map(a => (
                            <li key={a.id} style={{ marginBottom: 10 }}>
                                <div style={{ display: "flex", alignItems: "baseline", gap: 8 }}>
                                    <Link to={`/assignment/${a.id}`} style={{ fontWeight: 600 }}>
                                        {a.title}
                                    </Link>

                                    {/* ← вот тут ссылка на редактор */}
                                    <Link to={`/assignments/${a.id}/edit`} style={{ fontSize: 12 }}>
                                        (редактировать)
                                    </Link>

                                    <span style={{ fontSize: 12, color: "#777" }}>
                                        сложность: {a.difficulty} · {a.tags || "—"}
                                    </span>
                                    {a.solvedByCurrentUser && (
                                        <span style={{ marginLeft: 8, color: "green", fontSize: 12 }}>
                                            ✔ выполнено
                                        </span>
                                    )}
                                </div>
                                <div style={{ color: "#555" }}>
                                    {(a.description || "").slice(0, 140)}
                                    {a.description && a.description.length > 140 ? "…" : ""}
                                </div>
                            </li>
                        ))}
                    </ul>

                </div>

                {/* правая колонка — конструктор */}
                <div style={{ border: "1px solid #ddd", padding: 12, borderRadius: 8 }}>
                    <h3>Создать задание</h3>
                    <label style={{ display: "block", marginBottom: 6 }}>
                        Название *
                        <input
                            value={title}
                            onChange={e => setTitle(e.target.value)}
                            placeholder="Например: Сумма чисел из ввода"
                            style={{ width: "100%" }}
                        />
                    </label>

                    <label style={{ display: "block", marginBottom: 6 }}>
                        Описание (markdown/html)
                        <textarea
                            value={description}
                            onChange={e => setDescription(e.target.value)}
                            rows={6}
                            placeholder="Опишите постановку задачи, примеры, ограничения…"
                            style={{ width: "100%", resize: "vertical" }}
                        />
                    </label>

                    <div style={{ display: "flex", gap: 8 }}>
                        <label>
                            Сложность:&nbsp;
                            <select value={difficulty} onChange={e => setDifficulty(Number(e.target.value))}>
                                <option value={1}>Легко</option>
                                <option value={2}>Средне</option>
                                <option value={3}>Сложно</option>
                            </select>
                        </label>
                        <label style={{ flex: 1 }}>
                            Теги:
                            <input
                                value={tags}
                                onChange={e => setTags(e.target.value)}
                                placeholder="строки,массивы,цикл"
                                style={{ width: "100%" }}
                            />
                        </label>
                    </div>

                    <div style={{ marginTop: 12 }}>
                        <div style={{ display: "flex", alignItems: "center", justifyContent: "space-between" }}>
                            <h4 style={{ margin: 0 }}>Тест-кейсы</h4>
                            <button onClick={addTestCase}>+ Тест</button>
                        </div>

                        {testCases.map((t, i) => (
                            <div key={i} style={{ border: "1px dashed #ccc", padding: 8, borderRadius: 6, marginTop: 8 }}>
                                <div style={{ display: "flex", gap: 8 }}>
                                    <input
                                        value={t.input}
                                        onChange={e => updateTestCase(i, "input", e.target.value)}
                                        placeholder="Входные данные"
                                        style={{ flex: 1 }}
                                    />
                                    <input
                                        value={t.expectedOutput}
                                        onChange={e => updateTestCase(i, "expectedOutput", e.target.value)}
                                        placeholder="Ожидаемый вывод"
                                        style={{ flex: 1 }}
                                    />
                                </div>
                                <div style={{ display: "flex", justifyContent: "space-between", marginTop: 6 }}>
                                    <label style={{ fontSize: 13 }}>
                                        <input
                                            type="checkbox"
                                            checked={t.isHidden}
                                            onChange={e => updateTestCase(i, "isHidden", e.target.checked)}
                                            style={{ marginRight: 6 }}
                                        />
                                        Скрытый тест
                                    </label>
                                    <button onClick={() => removeTestCase(i)} style={{ color: "#a00" }}>
                                        удалить
                                    </button>
                                </div>
                            </div>
                        ))}
                    </div>

                    {err && <div style={{ color: "crimson", marginTop: 8 }}>{err}</div>}

                    <div style={{ marginTop: 12 }}>
                        <button onClick={handleCreate}>Создать задание</button>
                    </div>
                </div>
            </div>
        </div>
    );
}

export default CoursePage;
