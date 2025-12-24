// clientapp/src/App.jsx
// Основной компонент приложения с маршрутизацией. Этот файл
// демонстрирует, как подключить страницы поддержки. В реальном
// проекте он должен быть объединён с существующей конфигурацией роутов.

import React from 'react';
import { BrowserRouter as Router, Routes, Route } from 'react-router-dom';
// Импорт страниц поддержки
import SupportTicketsPage from './pages/SupportTicketsPage';
import SupportCreatePage from './pages/SupportCreatePage';
import SupportChatPage from './pages/SupportChatPage';
import AdminSupportPage from './pages/AdminSupportPage';

// TODO: подключить существующие страницы и контексты (AuthContext, etc.)

function App() {
  return (
    <Router>
      <Routes>
        {/* Маршруты для страницы поддержки пользователя */}
        <Route path="/support" element={<SupportTicketsPage />} />
        <Route path="/support/new" element={<SupportCreatePage />} />
        <Route path="/support/:ticketId" element={<SupportChatPage />} />
        {/* Админская страница (необходимо обернуть ProtectedRoute/AdminRoute) */}
        <Route path="/admin/support" element={<AdminSupportPage />} />
        {/* ... другие маршруты ... */}
      </Routes>
    </Router>
  );
}

export default App;