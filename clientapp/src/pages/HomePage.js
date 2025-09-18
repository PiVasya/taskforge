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
            <button onClick={handleLogout}>Выйти</button>
        </div>
    );
}

export default HomePage;
