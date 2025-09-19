// clientapp/src/pages/TestRunnerPage.js
import React, { useState } from 'react';

const API_URL = process.env.REACT_APP_API_URL;

function TestRunnerPage() {
    const [language, setLanguage] = useState('csharp');
    const [code, setCode] = useState('');
    const [tests, setTests] = useState([{ input: '', expectedOutput: '' }]);
    const [results, setResults] = useState(null);
    const [error, setError] = useState('');

    const handleAddTest = () => {
        setTests([...tests, { input: '', expectedOutput: '' }]);
    };

    const handleTestChange = (index, field, value) => {
        const newTests = [...tests];
        newTests[index][field] = value;
        setTests(newTests);
    };

    const handleRunTests = async () => {
        try {
            const res = await runTests({ language, code, testCases: tests });
            setResults(res.results);
        } catch (err) {
            setError(err.message);
        }
    };

    return (
        <div style={{ padding: '1rem' }}>
            <h2>Тестер программ</h2>
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
                placeholder="Введите код..."
            />
            <h4>Тест-кейсы</h4>
            {tests.map((t, idx) => (
                <div key={idx} style={{ marginBottom: '0.5rem' }}>
                    <div>
                        <label>Вход {idx + 1}:&nbsp;
                            <input
                                type="text"
                                value={t.input}
                                onChange={(e) => handleTestChange(idx, 'input', e.target.value)}
                            />
                        </label>
                    </div>
                    <div>
                        <label>Ожидаемый выход {idx + 1}:&nbsp;
                            <input
                                type="text"
                                value={t.expectedOutput}
                                onChange={(e) => handleTestChange(idx, 'expectedOutput', e.target.value)}
                            />
                        </label>
                    </div>
                </div>
            ))}
            <button onClick={handleAddTest}>Добавить тест</button>
            <button onClick={handleRunTests}>Запустить</button>
            {error && <p style={{ color: 'red' }}>{error}</p>}
            {results && (
                <table border="1" cellPadding="4">
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
                        {results.map((r, i) => (
                            <tr key={i}>
                                <td>{i + 1}</td>
                                <td><pre>{r.input}</pre></td>
                                <td><pre>{r.expectedOutput}</pre></td>
                                <td><pre>{r.actualOutput}</pre></td>
                                <td style={{ color: r.passed ? 'green' : 'red' }}>
                                    {r.passed ? '✔' : '✘'}
                                </td>
                            </tr>
                        ))}
                    </tbody>
                </table>
            )}
        </div>
    );
}

export default TestRunnerPage;
