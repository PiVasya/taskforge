// clientapp/src/pages/RegisterPage.js

import React, { useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { registerUser } from '../api/auth';

function RegisterPage() {
    const navigate = useNavigate();
    const [form, setForm] = useState({
        email: '',
        firstName: '',
        lastName: '',
        phoneNumber: '',
        dateOfBirth: '',
        password: '',
        confirmPassword: ''
    });
    const [error, setError] = useState(null);
    const [success, setSuccess] = useState(null);

    const handleChange = (e) => {
        const { name, value } = e.target;
        setForm((prev) => ({ ...prev, [name]: value }));
    };

    const handleSubmit = async (e) => {
        e.preventDefault();
        setError(null);
        setSuccess(null);
        if (form.password.length < 6) {
            setError('Пароль должен быть не короче 6 символов');
            return;
        }
        if (form.password !== form.confirmPassword) {
            setError('Пароли не совпадают');
            return;
        }
        try {
            await registerUser({
                email: form.email,
                firstName: form.firstName,
                lastName: form.lastName,
                password: form.password,
                phoneNumber: form.phoneNumber || null,
                dateOfBirth: form.dateOfBirth || null
            });
            setSuccess('Регистрация успешна!');
            // перенаправляем на страницу входа
            setTimeout(() => navigate('/login'), 1500);
        } catch (err) {
            setError(err.message);
        }
    };

    return (
        <div style={{ maxWidth: 400, margin: '0 auto' }}>
            <h2>Регистрация</h2>
            {error && <p style={{ color: 'red' }}>{error}</p>}
            {success && <p style={{ color: 'green' }}>{success}</p>}
            <form onSubmit={handleSubmit}>
                <div>
                    <label>Email<br />
                        <input
                            type="email"
                            name="email"
                            value={form.email}
                            onChange={handleChange}
                            required
                        />
                    </label>
                </div>
                <div>
                    <label>Имя<br />
                        <input
                            type="text"
                            name="firstName"
                            value={form.firstName}
                            onChange={handleChange}
                            required
                        />
                    </label>
                </div>
                <div>
                    <label>Фамилия<br />
                        <input
                            type="text"
                            name="lastName"
                            value={form.lastName}
                            onChange={handleChange}
                            required
                        />
                    </label>
                </div>
                <div>
                    <label>Телефон<br />
                        <input
                            type="tel"
                            name="phoneNumber"
                            value={form.phoneNumber}
                            onChange={handleChange}
                        />
                    </label>
                </div>
                <div>
                    <label>Дата рождения<br />
                        <input
                            type="date"
                            name="dateOfBirth"
                            value={form.dateOfBirth}
                            onChange={handleChange}
                        />
                    </label>
                </div>
                <div>
                    <label>Пароль<br />
                        <input
                            type="password"
                            name="password"
                            value={form.password}
                            onChange={handleChange}
                            required
                        />
                    </label>
                </div>
                <div>
                    <label>Подтвердите пароль<br />
                        <input
                            type="password"
                            name="confirmPassword"
                            value={form.confirmPassword}
                            onChange={handleChange}
                            required
                        />
                    </label>
                </div>
                <button type="submit">Создать аккаунт</button>
            </form>
            <p>
                Уже зарегистрированы? <a href="/login">Войти</a>
            </p>
        </div>
    );
}

export default RegisterPage;
