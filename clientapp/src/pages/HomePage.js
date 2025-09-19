// clientapp/src/pages/HomePage.js

import React from 'react';
import { useNavigate } from 'react-router-dom';

function HomePage() {
    const navigate = useNavigate();

    const handleLogout = () => {
        localStorage.removeItem('token');
        navigate('/login');
    };

    return (
        <div style={{ maxWidth: 600, margin: '0 auto' }}>
            <h2>Главная страница</h2>
            <p>Поздравляем! Вы успешно вошли в систему.</p>
            <div style={{ marginBottom: '1rem' }}>
                {/* Кнопка для перехода на страницу онлайн‑компилятора */}
                <button onClick={() => navigate('/compiler')} style={{ marginRight: '0.5rem' }}>
                    Онлайн компилятор
                </button>
                {/* Кнопка для перехода на страницу тестера */}
                <button onClick={() => navigate('/tester')}>
                    Тестер решений
                </button>
            </div>
            <button onClick={handleLogout}>Выйти</button>
        </div>
    );
}

export default HomePage;
