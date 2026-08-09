import React from 'react';
import ReactDOM from 'react-dom/client';
import { BrowserRouter } from 'react-router-dom';
import AppProviders from './app/AppProviders';
import App from './App';
import './index.css';
import './styles/theme.css';
import './components/bgfx/bgfx.css';
import './styles/controls.css';
import './components/bgfx/bgfx-legacy.css';
import './components/shell/shell.css';
import './features/landing/landing.css';
import './features/assignment-solve/assignment-solve.css';
import './features/news/news.css';
import './components/shell/mobile-shell.css';
import './styles/neobrutal.css';

ReactDOM.createRoot(document.getElementById('root')).render(
  <BrowserRouter>
    <AppProviders>
      <App />
    </AppProviders>
  </BrowserRouter>,
);
