// Функции для работы с API компилятора и тестера

/**
 * Компилирует и запускает код.
 * @param {Object} params { language, code, input }
 * @returns {Promise<Object>} результат { output?, error? }
 */

const API_URL = process.env.REACT_APP_API_URL;

export async function compileRun({ language, code, input }) {
    const response = await fetch(`${API_URL}/api/compiler/compile-run`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
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
    const response = await fetch(`${API_URL}/apicompiler/run-tests`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ language, code, testCases })
    });
    return await response.json();
}
