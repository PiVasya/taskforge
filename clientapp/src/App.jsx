import { Routes, Route, Navigate } from "react-router-dom";
import ProtectedRoute from "./auth/ProtectedRoute";
import EditorRoute from "./auth/EditorRoute";

import LoginPage from "./pages/LoginPage";
import CoursesPage from "./pages/CoursesPage";
import CourseAssignmentsPage from "./pages/CourseAssignmentsPage";
import CourseEditPage from "./pages/CourseEditPage";
import AssignmentEditPage from "./pages/AssignmentEditPage";
import AssignmentSolvePage from "./pages/AssignmentSolvePage"; // <— НОВОЕ

function Home() { return <Navigate to="/courses" replace />; }
function NotFound() { return <div className="container-app py-10">Страница не найдена</div>; }

export default function App() {
    return (
        <Routes>
            <Route path="/login" element={<LoginPage />} />

            <Route element={<ProtectedRoute />}>
                <Route path="/" element={<Home />} />
                <Route path="/courses" element={<CoursesPage />} />
                <Route path="/course/:courseId" element={<CourseAssignmentsPage />} />

                {/* viewer-страница решения */}
                <Route path="/assignment/:assignmentId" element={<AssignmentSolvePage />} />

                {/* редакторские страницы — только в режиме редактора */}
                <Route element={<EditorRoute fallbackTo="course" />}>
                    <Route path="/courses/:courseId/edit" element={<CourseEditPage />} />
                    <Route path="/assignment/:assignmentId/edit" element={<AssignmentEditPage />} />
                </Route>
            </Route>

            <Route path="*" element={<NotFound />} />
        </Routes>
    );
}
