// clientapp/src/api/compiler.js

// базовый адрес API (пустая строка означает относительный путь)
const API_URL = process.env.REACT_APP_API_URL;

/**
 * Возвращает заголовок Authorization, если в localStorage есть токен.
 */
function getAuthHeaders() {
    const token = localStorage.getItem("token");
    return token ? { Authorization: `Bearer ${token}` } : {};
}

/**
 * Общий метод для POST‑запросов к компилятору и тестеру.
 * Он обрабатывает ответы с кодами, отличными от 2xx, и пытается разобрать тело.
 */
async function postJson(url, payload) {
    const res = await fetch(url, {
        method: "POST",
        headers: {
            "Content-Type": "application/json",
            ...getAuthHeaders()
        },
        body: JSON.stringify(payload)
    });

    // читаем тело как текст, потому что при 401/500 оно может быть пустым или не JSON
    const text = await res.text();
    let data;
    try {
        data = text ? JSON.parse(text) : null;
    } catch {
        data = null;
    }

    // если статус не OK, генерируем осмысленное сообщение
    if (!res.ok) {
        const message =
            data?.error || text || `Ошибка сервера: ${res.status} ${res.statusText}`;
        throw new Error(message);
    }
    return data;
}

/**
 * Компилирует и запускает код.
 * @param {{language:string, code:string, input?:string}} params
 * @returns {Promise<{output?:string, error?:string}>}
 */
export async function compileRun({ language, code, input }) {
    return await postJson(
            `${API_URL}/api/compiler/compile-run`,
            { language, code, input }
        );
}

/**
 * Запускает набор тестов для указанного кода.
 * @param {{language:string, code:string, testCases:Array}} params
 * @returns {Promise<{results: Array}>}
 */
export async function runTests({ language, code, testCases }) {
    return await postJson(
        `${API_URL}/api/compiler/run-tests`,
        { language, code, testCases }
    );
}
