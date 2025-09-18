import React from 'react';
import { useNavigate } from 'react-router-dom';

/**
 * A simple home page displayed after a successful login. It shows a
 * welcome message and includes a logout button that clears the
 * authentication token and redirects back to the login page.
 */
function Home() {
  const navigate = useNavigate();

  const logout = () => {
    localStorage.removeItem('token');
    navigate('/login');
  };

  return (
    <div className="container">
      <div className="form-card">
        <h2>Главная страница</h2>
        <p>Поздравляю! Вы успешно вошли в систему.</p>
        <button onClick={logout}>Выйти</button>
      </div>
    </div>
  );
}

export default Home;