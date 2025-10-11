import React, { createContext, useContext, useEffect, useMemo, useState } from "react";
import { useAuth } from "../auth/AuthContext";

// простейший декодер JWT без зависимостей
function parseJwt(token) {
  try {
    const [, payload] = token.split(".");
    const json = atob(payload.replace(/-/g, "+").replace(/_/g, "/"));
    return JSON.parse(decodeURIComponent(escape(json)));
  } catch { return null; }
}

const Ctx = createContext(null);
export const useEditorMode = () => useContext(Ctx);

const STORAGE_KEY = "editorMode.v1";

export default function EditorModeProvider({ children }) {
  const { access } = useAuth();                 // токен из AuthContext
  const [isEditorMode, setIsEditorMode] = useState(false);

  // роль из токена
  const role = React.useMemo(() => {
    if (!access) return null;
    const p = parseJwt(access);
    // стандартный и нестандартный варианты claim
    return p?.role || p?.roles || p?.["http://schemas.microsoft.com/ws/2008/06/identity/claims/role"] || null;
  }, [access]);

  const canEdit = String(role || "").toLowerCase() === "admin";

  // восстановление переключателя (только если аккаунт может редактировать)
  useEffect(() => {
    if (!canEdit) { setIsEditorMode(false); return; }
    try { setIsEditorMode(localStorage.getItem(STORAGE_KEY) === "1"); } catch {}
  }, [canEdit]);

  // сохраняем
  useEffect(() => {
    if (canEdit) try { localStorage.setItem(STORAGE_KEY, isEditorMode ? "1" : "0"); } catch {}
  }, [isEditorMode, canEdit]);

  const value = useMemo(() => ({
    canEdit,
    isEditorMode,
    toggle: () => canEdit && setIsEditorMode(v => !v),
    set: (v) => canEdit && setIsEditorMode(!!v),
  }), [canEdit, isEditorMode]);

  return <Ctx.Provider value={value}>{children}</Ctx.Provider>;
}
export function useRoleFlags() {
  const { access } = useAuth();
  let isAdmin = false;
  if (access) {
    try {
      const payload = JSON.parse(atob(access.split(".")[1].replace(/-/g, "+").replace(/_/g, "/")));
      const role = (payload?.role || payload?.Role || payload?.roles)?.toString() || "";
      isAdmin = role === "Admin";
    } catch {}
  }
  return { isAdmin };
}
