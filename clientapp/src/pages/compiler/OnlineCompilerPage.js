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
    const logDebug = (msg, data) => {
        const timestamp = new Date().toISOString();
        const entry = data ? `${timestamp} - ${msg}: ${JSON.stringify(data)}` : `${timestamp} - ${msg}`;
        console.log(entry);
        setDebugLog((prev) => [...prev, entry]);
    };

    // при первом рендере проверяем токен и переходим на /login, если его нет
    useEffect(() => {
        logDebug('OnlineCompilerPage mounted');
        const token = localStorage.getItem('token');
        if (!token) {
            logDebug('No token found, redirecting to /login');
            navigate('/login');
        } else {
            logDebug('Token found', { tokenSnippet: token.substring(0, 10) + '...' });
        }
        // eslint-disable-next-line react-hooks/exhaustive-deps
    }, [navigate]);

    // обработчик запуска кода
    const handleRun = async () => {
        setError('');
        setOutput('');
        logDebug('handleRun invoked', { language, codeLength: code.length, inputLength: input.length });
        try {
            const payload = { language, code, input };
            logDebug('Sending compileRun request', payload);
            console.log("Token from localStorage:", localStorage.getItem("token"));
            const res = await compileRun(payload);
            logDebug('Received response from compileRun', res);
            setOutput(res.output || '');
            if (res.error) {
                logDebug('Error returned from compileRun', { error: res.error });
                setError(res.error);
            }
        } catch (err) {
            logDebug('Exception during compileRun', { message: err.message });
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
                    {debugLog.map((entry, idx) => (
                        <li key={idx}>{entry}</li>
                    ))}
                </ul>
            </div>
        </div>
    );
}

export default OnlineCompilerPage;
