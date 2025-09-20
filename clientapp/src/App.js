import React from 'react';
import { Routes, Route, Navigate } from 'react-router-dom';
import HomePage from './pages/HomePage';
import LoginPage from './pages/auth/LoginPage';
import RegisterPage from './pages/auth/RegisterPage';
import OnlineCompilerPage from './pages/compiler/OnlineCompilerPage';
import TestRunnerPage from './pages/tester/TestRunnerPage';
import CoursesPage from './pages/courses/CoursesPage';
import CoursePage from "./pages/courses/CoursePage";

function App() {
    return (
        <Routes>
            <Route path="/" element={<Navigate to="/login" />} />
            <Route path="/register" element={<RegisterPage />} />
            <Route path="/login" element={<LoginPage />} />
            <Route path="/home" element={<HomePage />} />
            <Route path="/compiler" element={<OnlineCompilerPage />} />
            <Route path="/tester" element={<TestRunnerPage />} />
            <Route path="/courses" element={<CoursesPage />} />
            <Route path="/course/:courseId" element={<CoursePage />} />
        </Routes>
    );
}

export default App;
