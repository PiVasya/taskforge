// clientapp/src/api/auth.js

const API_URL = process.env.REACT_APP_API_URL; // берём адрес из .env

/**
 * Регистрация пользователя.
 * @param {Object} data объект с полями: email, firstName, lastName, password, phoneNumber?, dateOfBirth?
 * @returns {Promise<Object>} ответ сервера { message: string }
 */
export async function registerUser(data) {
    const response = await fetch(`${API_URL}/api/auth/register`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
            email: data.email,
            firstName: data.firstName,
            lastName: data.lastName,
            password: data.password,
            phoneNumber: data.phoneNumber || null,
            dateOfBirth: data.dateOfBirth || null
        })
    });

    const result = await response.json();
    if (!response.ok) {
        throw new Error(result.message || 'Ошибка регистрации');
    }
    return result;
}

/**
 * Авторизация пользователя.
 * @param {Object} data объект с полями: email, password
 * @returns {Promise<string>} токен из ответа сервера
 */
export async function loginUser(data) {
    const response = await fetch(`${API_URL}/api/auth/login`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
            email: data.email,
            password: data.password
        })
    });

    const result = await response.json();
    if (!response.ok) {
        throw new Error(result.message || 'Ошибка авторизации');
    }
    return result.token;
}
