import React, { createContext, useContext, useEffect, useMemo, useState } from "react";
import { useAuth } from "../auth/AuthContext";

// JWT payload decoder (no deps)
function parseJwt(token) {
  try {
    const [, payload] = token.split(".");
    const json = atob(payload.replace(/-/g, "+").replace(/_/g, "/"));
    return JSON.parse(decodeURIComponent(escape(json)));
  } catch {
    return null;
  }
}

function normalizeRoles(raw) {
  if (!raw) return [];
  if (Array.isArray(raw)) return raw.map(String).filter(Boolean);
  if (typeof raw === "string") {
    // sometimes roles come as "Admin,Editor" or "Admin;Editor"
    if (raw.includes(",") || raw.includes(";")) {
      return raw
        .split(/[,;]+/g)
        .map((x) => x.trim())
        .filter(Boolean);
    }
    return [raw];
  }
  return [];
}

function getRolesFromToken(access) {
  if (!access) return [];
  const p = parseJwt(access);
  const raw =
    p?.role ??
    p?.Role ??
    p?.roles ??
    p?.Roles ??
    p?.["http://schemas.microsoft.com/ws/2008/06/identity/claims/role"] ??
    null;
  return normalizeRoles(raw);
}

const Ctx = createContext(null);
export const useEditorMode = () => useContext(Ctx);

const STORAGE_KEY = "editorMode.v1";

export default function EditorModeProvider({ children }) {
  const { access } = useAuth();
  const [isEditorMode, setIsEditorMode] = useState(false);

  const roles = useMemo(() => getRolesFromToken(access), [access]);
  const isAdmin = roles.includes("Admin");
  const isEditor = roles.includes("Editor");

  // editor mode is available for Admin and Editor
  const canEdit = isAdmin || isEditor;

  // restore switch only if canEdit
  useEffect(() => {
    if (!canEdit) {
      setIsEditorMode(false);
      return;
    }
    try {
      setIsEditorMode(localStorage.getItem(STORAGE_KEY) === "1");
    } catch {}
  }, [canEdit]);

  // persist
  useEffect(() => {
    if (canEdit) {
      try {
        localStorage.setItem(STORAGE_KEY, isEditorMode ? "1" : "0");
      } catch {}
    }
  }, [isEditorMode, canEdit]);

  const toggle = () => {
    if (!canEdit) return;
    setIsEditorMode((v) => !v);
  };

  const value = useMemo(
    () => ({ canEdit, isEditorMode, toggle, isAdmin, isEditor, roles }),
    [canEdit, isEditorMode, isAdmin, isEditor, roles]
  );

  return <Ctx.Provider value={value}>{children}</Ctx.Provider>;
}

// quick role flags (for components that do not want editor-mode state)
export function useRoleFlags() {
  const { access } = useAuth();
  const roles = useMemo(() => getRolesFromToken(access), [access]);
  return {
    roles,
    isAdmin: roles.includes("Admin"),
    isEditor: roles.includes("Editor"),
    canEdit: roles.includes("Admin") || roles.includes("Editor"),
  };
}
