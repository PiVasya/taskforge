import React from 'react';
import ReactDOM from 'react-dom/client';
import { BrowserRouter } from 'react-router-dom';
import AppProviders from './app/AppProviders';
import App from './App';
import { applyStoredUiAppearance } from './utils/uiAppearance';
import './index.css';
import './neobrutal.css';

applyStoredUiAppearance();

ReactDOM.createRoot(document.getElementById('root')).render(
  <BrowserRouter>
    <AppProviders>
      <App />
    </AppProviders>
  </BrowserRouter>,
);
