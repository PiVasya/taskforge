import React, { createContext, useContext, useEffect, useMemo, useState } from "react";
import { useAuth } from "../auth/AuthContext";

// Декодер JWT без зависимостей
function parseJwt(token) {
  try {
    const [, payload] = token.split(".");
    const json = atob(payload.replace(/-/g, "+").replace(/_/g, "/"));
    return JSON.parse(decodeURIComponent(escape(json)));
  } catch {
    return null;
  }
}

const Ctx = createContext(null);
export const useEditorMode = () => useContext(Ctx);

const STORAGE_KEY = "editorMode.v1";

export default function EditorModeProvider({ children }) {
  const { access } = useAuth();
  const [isEditorMode, setIsEditorMode] = useState(false);

  // Роль из токена (поддерживаем несколько вариантов claim)
  const role = useMemo(() => {
    if (!access) return null;
    const p = parseJwt(access);
    return (
      p?.role ??
      p?.Role ??
      p?.roles ??
      p?.["http://schemas.microsoft.com/ws/2008/06/identity/claims/role"] ??
      null
    );
  }, [access]);

  const canEdit = String(role || "").toLowerCase() === "admin";

  // Восстановление переключателя (только если есть права)
  useEffect(() => {
    if (!canEdit) {
      setIsEditorMode(false);
      return;
    }
    try {
      setIsEditorMode(localStorage.getItem(STORAGE_KEY) === "1");
    } catch {}
  }, [canEdit]);

  // Сохранение флага режима
  useEffect(() => {
    if (canEdit) {
      try {
        localStorage.setItem(STORAGE_KEY, isEditorMode ? "1" : "0");
      } catch {}
    }
  }, [isEditorMode, canEdit]);

  const toggle = () => {
    if (!canEdit) return;
    setIsEditorMode(v => !v);
  };

  const value = useMemo(
    () => ({ canEdit, isEditorMode, toggle }),
    [canEdit, isEditorMode]
  );

  return <Ctx.Provider value={value}>{children}</Ctx.Provider>;
}

// Вспомогательный хук (если где-то нужно быстро проверить роль)
export function useRoleFlags() {
  const { access } = useAuth();
  let isAdmin = false;
  if (access) {
    try {
      const payload = parseJwt(access);
      const raw =
        payload?.role ??
        payload?.Role ??
        payload?.roles ??
        payload?.["http://schemas.microsoft.com/ws/2008/06/identity/claims/role"];
      if (Array.isArray(raw)) isAdmin = raw.includes("Admin");
      else if (typeof raw === "string") isAdmin = raw === "Admin";
    } catch {}
  }
  return { isAdmin };
}
