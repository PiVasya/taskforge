import React, { useEffect, useState } from "react";
import { useParams, Link } from "react-router-dom";
import { getAssignment, submitSolution } from "../../api/assignments";
import { runTests, compileRun } from "../../api/compiler";

function mask(text, hidden) {
    if (!hidden) return text;
    return "••• скрытый тест •••";
}

function AssignmentPage() {
    const { assignmentId } = useParams();

    const [info, setInfo] = useState(null); // AssignmentDetailsDto
    const [loading, setLoading] = useState(false);
    const [err, setErr] = useState("");

    // решение
    const [language, setLanguage] = useState("csharp");
    const [code, setCode] = useState("");
    const [runInput, setRunInput] = useState("");
    const [runOutput, setRunOutput] = useState("");
    const [runError, setRunError] = useState("");

    // локальный «пробный» прогон по ПУБЛИЧНЫМ тестам (без отправки)
    const [publicCheck, setPublicCheck] = useState(null); // { cases: [{input, expected, actual, passed}], passed, failed }

    // итог после отправки — полный (с учётом скрытых)
    const [submitResult, setSubmitResult] = useState(null); // SubmitSolutionResultDto

    useEffect(() => {
        load();
        // eslint-disable-next-line
    }, [assignmentId]);

    async function load() {
        try {
            setLoading(true);
            setErr("");
            const data = await getAssignment(assignmentId);
            setInfo(data);
        } catch (e) {
            setErr(e.message || "Ошибка загрузки");
        } finally {
            setLoading(false);
        }
    }

    // быстрый запуск с произвольным вводом
    async function handleQuickRun() {
        setRunError(""); setRunOutput("");
        try {
            const r = await compileRun({ language, code, input: runInput });
            if (r.error) setRunError(r.error);
            setRunOutput(r.output || "");
        } catch (e) {
            setRunError(e.message);
        }
    }

    // локальная проверка по публичным тестам
    async function handlePublicCheck() {
        if (!info) return;
        try {
            const publicCount = info.publicTestCount || 0;
            if (!publicCount) {
                setPublicCheck({ cases: [], passed: 0, failed: 0 });
                return;
            }

            // Нам нужны фактические входы/ожидания публичных тестов.
            // Простая опция: добавить эндпоинт `GET /api/assignments/{id}` который возвращает примеры публичных тестов.
            // Чтобы не ломать бэкенд, просто вызовем submit на публичных нельзя — он включает hidden.
            // В минимальном варианте — делаем «черный ящик» и просим бэк отдать публичные кейсы.
            // В этом коде предположим, что есть /api/assignments/{id}/public-cases (если добавишь),
            // а пока — пропустим локальную проверку и оставим только submit.

            alert("Локальная проверка публичных тестов требует эндпоинта с публичными кейсами. Пока используем только «Отправить решение»."); // можно убрать
        } catch (e) {
            console.error(e);
        }
    }

    async function handleSubmit() {
        try {
            setErr("");
            setSubmitResult(null);
            const res = await submitSolution(assignmentId, { language, code });
            setSubmitResult(res);
            // можно перезагрузить детали задания, чтобы обновился флаг solved
            await load();
        } catch (e) {
            setErr(e.message || "Не удалось отправить решение");
        }
    }

    return (
        <div style={{ maxWidth: 1100, margin: "0 auto", padding: "1rem" }}>
            <div style={{ marginBottom: 12 }}>
                <Link to="/courses">← К курсам</Link>
            </div>

            {loading && <div>Загрузка…</div>}
            {err && <div style={{ color: "crimson" }}>{err}</div>}
            {!loading && info && (
                <>
                    <h2 style={{ margin: 0 }}>{info.title}</h2>
                    <div style={{ color: "#777", marginBottom: 12 }}>
                        сложность: {info.difficulty} · {info.tags || "—"} · создано: {new Date(info.createdAt).toLocaleString()}
                    </div>
                    <div style={{ whiteSpace: "pre-wrap", border: "1px solid #eee", padding: 12, borderRadius: 8, background: "#fafafa" }}>
                        {info.description}
                    </div>

                    <div style={{ marginTop: 16, fontSize: 14, color: "#555" }}>
                        Публичных тестов: {info.publicTestCount} · Скрытых: {info.hiddenTestCount} ·
                        {info.solvedByCurrentUser ? <span style={{ color: "green" }}> ✔ вы уже решили</span> : " ещё не решено"}
                    </div>

                    <hr style={{ margin: "16px 0" }} />

                    {/* редактор кода */}
                    <div style={{ display: "grid", gridTemplateColumns: "1fr 360px", gap: 16 }}>
                        <div>
                            <label>
                                Язык:&nbsp;
                                <select value={language} onChange={e => setLanguage(e.target.value)}>
                                    <option value="csharp">C#</option>
                                    <option value="cpp">C++</option>
                                    {/* <option value="python">Python</option> если добавишь */}
                                </select>
                            </label>

                            <textarea
                                value={code}
                                onChange={e => setCode(e.target.value)}
                                rows={18}
                                placeholder="// Введите код здесь"
                                style={{ width: "100%", marginTop: 8, fontFamily: "monospace", resize: "vertical" }}
                            />

                            <div style={{ display: "flex", gap: 8 }}>
                                <button onClick={handleQuickRun}>Быстрый запуск</button>
                                {/* <button onClick={handlePublicCheck} disabled={!info.publicTestCount}>Проверить по публичным</button> */}
                                <button onClick={handleSubmit} style={{ marginLeft: "auto", background: "#16a34a", color: "white" }}>
                                    Отправить решение
                                </button>
                            </div>
                        </div>

                        <div>
                            <div style={{ border: "1px solid #ddd", borderRadius: 8, padding: 12 }}>
                                <h4 style={{ margin: 0 }}>Быстрый запуск</h4>
                                <textarea
                                    value={runInput}
                                    onChange={e => setRunInput(e.target.value)}
                                    rows={6}
                                    placeholder="stdin"
                                    style={{ width: "100%", fontFamily: "monospace", marginTop: 8 }}
                                />
                                <div style={{ marginTop: 8 }}>
                                    <div style={{ fontWeight: 600 }}>Вывод:</div>
                                    <pre style={{ background: "#fafafa", padding: 8, borderRadius: 6, minHeight: 80 }}>{runOutput}</pre>
                                </div>
                                {runError && (
                                    <div style={{ color: "crimson" }}>
                                        <div style={{ fontWeight: 600 }}>Ошибка:</div>
                                        <pre style={{ whiteSpace: "pre-wrap" }}>{runError}</pre>
                                    </div>
                                )}
                            </div>

                            {/* публичный чек — пока отключён, см. комментарий */}
                            {publicCheck && (
                                <div style={{ border: "1px solid #ddd", borderRadius: 8, padding: 12, marginTop: 12 }}>
                                    <h4 style={{ marginTop: 0 }}>Проверка по публичным</h4>
                                    <div>Пройдено: {publicCheck.passed} · Ошибок: {publicCheck.failed}</div>
                                    <table border="1" cellPadding="6" style={{ width: "100%", marginTop: 8 }}>
                                        <thead>
                                            <tr>
                                                <th>#</th>
                                                <th>Вход</th>
                                                <th>Ожидание</th>
                                                <th>Фактический</th>
                                                <th>OK?</th>
                                            </tr>
                                        </thead>
                                        <tbody>
                                            {publicCheck.cases.map((c, i) => (
                                                <tr key={i}>
                                                    <td>{i + 1}</td>
                                                    <td><pre>{c.input}</pre></td>
                                                    <td><pre>{c.expected}</pre></td>
                                                    <td><pre>{c.actual}</pre></td>
                                                    <td style={{ color: c.passed ? "green" : "crimson" }}>{c.passed ? "✔" : "✘"}</td>
                                                </tr>
                                            ))}
                                        </tbody>
                                    </table>
                                </div>
                            )}

                            {/* итог после submit — с учётом скрытых */}
                            {submitResult && (
                                <div style={{ border: "2px solid #16a34a", borderRadius: 8, padding: 12, marginTop: 12 }}>
                                    <h4 style={{ marginTop: 0 }}>Итоговая проверка</h4>
                                    <div>
                                        {submitResult.passedAll
                                            ? <span style={{ color: "green", fontWeight: 700 }}>Все тесты пройдены 🎉</span>
                                            : <span style={{ color: "crimson", fontWeight: 700 }}>Есть ошибки</span>}
                                    </div>
                                    <div>Пройдено: {submitResult.passed} · Ошибок: {submitResult.failed}</div>

                                    <table border="1" cellPadding="6" style={{ width: "100%", marginTop: 8 }}>
                                        <thead>
                                            <tr>
                                                <th>#</th>
                                                <th>Вход</th>
                                                <th>Ожидание</th>
                                                <th>Фактический</th>
                                                <th>OK?</th>
                                            </tr>
                                        </thead>
                                        <tbody>
                                            {submitResult.cases.map((c, i) => (
                                                <tr key={i}>
                                                    <td>{i + 1}</td>
                                                    <td><pre>{mask(c.input, c.hidden)}</pre></td>
                                                    <td><pre>{mask(c.expected, c.hidden)}</pre></td>
                                                    <td><pre>{c.actual}</pre></td>
                                                    <td style={{ color: c.passed ? "green" : "crimson" }}>{c.passed ? "✔" : "✘"}</td>
                                                </tr>
                                            ))}
                                        </tbody>
                                    </table>
                                </div>
                            )}
                        </div>
                    </div>
                </>
            )}
        </div>
    );
}

export default AssignmentPage;
