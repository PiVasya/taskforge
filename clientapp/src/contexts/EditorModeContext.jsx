import React, { createContext, useContext, useEffect, useMemo, useState } from "react";

/**
 * canEdit — право аккаунта быть редактором.
 * Пока бэкенд не отдаёт статус — считаем true (но режим по умолчанию выключен).
 */
const DEFAULT_CAN_EDIT = true;

const Ctx = createContext(null);
export const useEditorMode = () => useContext(Ctx);

const STORAGE_KEY = "editorMode.v1";

export default function EditorModeProvider({ children, canEdit = DEFAULT_CAN_EDIT }) {
    const [isEditorMode, setIsEditorMode] = useState(false);

    // грузим сохранённый режим только если у юзера вообще есть право редактировать
    useEffect(() => {
        if (!canEdit) { setIsEditorMode(false); return; }
        const saved = localStorage.getItem(STORAGE_KEY);
        setIsEditorMode(saved === "1");
    }, [canEdit]);

    // сохраняем
    useEffect(() => {
        if (canEdit) localStorage.setItem(STORAGE_KEY, isEditorMode ? "1" : "0");
    }, [isEditorMode, canEdit]);

    const value = useMemo(() => ({
        canEdit,
        isEditorMode,
        toggle: () => canEdit && setIsEditorMode(v => !v),
        set: (v) => canEdit && setIsEditorMode(!!v),
    }), [canEdit, isEditorMode]);

    return <Ctx.Provider value={value}>{children}</Ctx.Provider>;
}
