// clientapp/src/pages/OnlineCompilerPage.js
import React, { useState } from 'react';

const API_URL = process.env.REACT_APP_API_URL;

function OnlineCompilerPage() {
    const [language, setLanguage] = useState('csharp');
    const [code, setCode] = useState('');
    const [input, setInput] = useState('');
    const [output, setOutput] = useState('');
    const [error, setError] = useState('');

    const handleRun = async () => {
        setOutput('');
        setError('');
        try {
            const response = await fetch(`${API_URL}/api/compiler/compile-run`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ language, code, input })
            });
            const result = await response.json();
            if (response.ok) {
                setOutput(result.output || '');
                setError(result.error || '');
            } else {
                setError(result.error || 'Ошибка выполнения');
            }
        } catch (err) {
            setError(err.message);
        }
    };

    return (
        <div style={{ padding: '1rem' }}>
            <h2>Онлайн компилятор</h2>
            <div>
                <label>Язык:&nbsp;
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
                <label>Входные данные:<br />
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
        </div>
    );
}

export default OnlineCompilerPage;
