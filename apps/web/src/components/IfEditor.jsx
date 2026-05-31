import React from "react";
import { useEditorMode } from "../contexts/EditorModeContext";

export default function IfEditor({ children, otherwise = null }) {
    const { canEdit, isEditorMode } = useEditorMode();
    return (canEdit && isEditorMode) ? children : otherwise;
}
