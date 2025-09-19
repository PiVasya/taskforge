// Функции для работы с API компилятора и тестера

/**
 * Компилирует и запускает код.
 * @param {Object} params { language, code, input }
 * @returns {Promise<Object>} результат { output?, error? }
 */
export async function compileRun({ language, code, input }) {
    const response = await fetch('/api/compiler/compile-run', {
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
    const response = await fetch('/api/compiler/run-tests', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ language, code, testCases })
    });
    return await response.json();
}
