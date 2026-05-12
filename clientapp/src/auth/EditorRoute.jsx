import React from "react";
import { Navigate, Outlet, useLocation, useParams } from "react-router-dom";
import { useEditorMode } from "../contexts/EditorModeContext";


export default function EditorRoute({ fallbackTo }) {
    const { isEditorMode, canEdit } = useEditorMode();
    const loc = useLocation();
    const { courseId, assignmentId } = useParams();

    if (!canEdit) return <Navigate to="/courses" replace />;

    if (!isEditorMode) {
        if (fallbackTo === "course" && courseId) return <Navigate to={`/course/${courseId}`} replace />;
        if (fallbackTo === "assignment" && assignmentId) return <Navigate to={`/course/${courseId}`} replace />;
        return <Navigate to="/courses" replace state={{ from: loc }} />;
    }
    return <Outlet />;
}
