import { Routes, Route } from 'react-router-dom';
import CourseAssignmentsPage from './pages/CourseAssignmentsPage';
import AssignmentEditPage from './pages/AssignmentEditPage';


export default function App() {
    return (
        <Routes>
            <Route path="/course/:courseId" element={<CourseAssignmentsPage />} />
            <Route path="/assignment/:assignmentId/edit" element={<AssignmentEditPage />} />
            {/* остальные ваши маршруты */}
        </Routes>
    );
}