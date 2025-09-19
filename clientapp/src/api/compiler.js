// src/api/compiler.js

// базовый адрес API считывается из .env
const API_URL = process.env.REACT_APP_API_URL || '';

/**
 * Вспомогательная функция: возвращает заголовок Authorization,
 * если в localStorage сохранён токен.
 */
function getAuthHeaders() {
    const token = localStorage.getItem('token');
    return token ? { Authorization: `Bearer ${token}` } : {};
}

/**
 * Компилирует и запускает код.
 * @param {Object} params { language, code, input }
 * @returns {Promise<Object>} результат { output?, error? }
 */
export async function compileRun({ language, code, input }) {
    const response = await fetch(`${API_URL}/api/compiler/compile-run`, {
        method: 'POST',
        headers: {
            'Content-Type': 'application/json',
            ...getAuthHeaders()
        },
        body: JSON.stringify({ language, code, input })
    });
    return await response.json();
}

/**
 * Запускает набор тестов для указанного кода.
 * @param {Object} params { language, code, testCases }
 * @returns {Promise<Object>} { results: [ { input, expectedOutput, actualOutput, passed }, … ] }
 */
export async function runTests({ language, code, testCases }) {
    const response = await fetch(`${API_URL}/api/compiler/run-tests`, {
        method: 'POST',
        headers: {
            'Content-Type': 'application/json',
            ...getAuthHeaders()
        },
        body: JSON.stringify({ language, code, testCases })
    });
    return await response.json();
}
