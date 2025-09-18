import React, { useState } from 'react';
import { useNavigate } from 'react-router-dom';

/**
 * A minimal login page component. It collects an email and password and
 * submits them to a backend authentication endpoint. On success it
 * stores the returned token and redirects the user to the home page.
 *
 * Note: adjust the endpoint URL to match your backend API. This example
 * assumes the backend provides a JSON response containing a JWT token
 * under the `token` property. Errors are displayed above the form.
 */
function Login() {
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [error, setError] = useState(null);
  const navigate = useNavigate();

  const handleSubmit = async (e) => {
    e.preventDefault();
    setError(null);
    try {
      const response = await fetch('/api/auth/login', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ email, password })
      });
      if (!response.ok) {
        throw new Error('Неверный email или пароль');
      }
      const data = await response.json();
      // Save token to localStorage or another storage mechanism
      if (data && data.token) {
        localStorage.setItem('token', data.token);
      }
      navigate('/home');
    } catch (err) {
      setError(err.message);
    }
  };

  return (
    <div className="container">
      <div className="form-card">
        <h2>Вход</h2>
        {error && <p style={{ color: 'red' }}>{error}</p>}
        <form onSubmit={handleSubmit}>
          <label htmlFor="email">Email</label>
          <input
            id="email"
            type="email"
            value={email}
            onChange={(e) => setEmail(e.target.value)}
            required
          />
          <label htmlFor="password">Пароль</label>
          <input
            id="password"
            type="password"
            value={password}
            onChange={(e) => setPassword(e.target.value)}
            required
          />
          <button type="submit">Войти</button>
        </form>
      </div>
    </div>
  );
}

export default Login;