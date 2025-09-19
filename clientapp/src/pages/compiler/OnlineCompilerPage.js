import React, { useState, useEffect } from 'react';
import { useNavigate } from 'react-router-dom';
import { compileRun } from '../../api/compiler';

/**
 * Страница для онлайн‑компиляции и запуска кода.
 * Проверяет наличие токена и ведёт журнал отладки.
 */
function OnlineCompilerPage() {
    const navigate = useNavigate();
    const [language, setLanguage] = useState('csharp');
    const [code, setCode] = useState('');
    const [input, setInput] = useState('');
    const [output, setOutput] = useState('');
    const [error, setError] = useState('');
    const [debugLog, setDebugLog] = useState([]);

    // добавляет сообщение в консоль и в локальный лог
    const logDebug = (msg) => {
        console.log(msg);
        setDebugLog((prev) => [...prev, msg]);
    };

    // при первом рендере проверяем токен и переходим на /login, если его нет
    useEffect(() => {
        logDebug('OnlineCompilerPage mounted.');
        const token = localStorage.getItem('token');
        if (!token) {
            logDebug('No token found, redirecting to /login');
            navigate('/login');
        } else {
            logDebug('Token found');
        }
        // eslint-disable-next-line react-hooks/exhaustive-deps
    }, [navigate]);

    // обработчик запуска кода
    const handleRun = async () => {
        setError('');
        setOutput('');
        logDebug(`Running code for language ${language}`);
        try {
            const res = await compileRun({ language, code, input });
            logDebug('Received response from compileRun');
            setOutput(res.output || '');
            if (res.error) {
                logDebug(`Error from compileRun: ${res.error}`);
                setError(res.error);
            }
        } catch (err) {
            logDebug(`Exception during compileRun: ${err.message}`);
            setError(err.message);
        }
    };

    return (
        <div style={{ padding: '1rem' }}>
            <h2>Онлайн компилятор</h2>
            <div>
                <label>
                    Язык:&nbsp;
                    <select value={language} onChange={(e) => setLanguage(e.target.value)}>
                        <option value="csharp">C#</option>
                        <option value="cpp">C++</option>
                    </select>
                </label>
            </div>
            <textarea
                value={code}
                onChange={(e) => setCode(e.target.value)}
                rows={12}
                cols={80}
                placeholder="Введите код здесь..."
            />
            <div>
                <label>
                    Входные данные:<br />
                    <textarea
                        value={input}
                        onChange={(e) => setInput(e.target.value)}
                        rows={3}
                        cols={80}
                    />
                </label>
            </div>
            <button onClick={handleRun}>Запустить</button>
            {output && (
                <div>
                    <h4>Вывод:</h4>
                    <pre>{output}</pre>
                </div>
            )}
            {error && (
                <div style={{ color: 'red' }}>
                    <h4>Ошибка:</h4>
                    <pre>{error}</pre>
                </div>
            )}
            <div>
                <h4>Debug Log:</h4>
                <ul>
                    {debugLog.map((msg, idx) => (
                        <li key={idx}>{msg}</li>
                    ))}
                </ul>
            </div>
        </div>
    );
}

export default OnlineCompilerPage;
