import React, { createContext, useContext, useEffect, useMemo, useState } from "react";
import { useAuth } from "../auth/AuthContext";

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
  if (Array.isArray(raw)) return raw.flatMap(normalizeRoles);
  if (typeof raw === "string") {
    if (raw.includes(",") || raw.includes(";")) {
      return raw
        .split(/[,;]+/g)
        .map((x) => x.trim())
        .filter(Boolean);
    }
    return [raw.trim()].filter(Boolean);
  }
  return [];
}

function getRolesFromToken(access) {
  if (!access) return [];
  const p = parseJwt(access);
  if (!p) return [];

  const buckets = [
    p?.role,
    p?.Role,
    p?.roles,
    p?.Roles,
    p?.primary_role,
    p?.["http://schemas.microsoft.com/ws/2008/06/identity/claims/role"],
  ];

  return buckets
    .flatMap(normalizeRoles)
    .filter(Boolean)
    .filter((v, i, arr) => arr.findIndex((x) => x.toLowerCase() === v.toLowerCase()) === i);
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
  const canEdit = isAdmin || isEditor;

  useEffect(() => {
    if (!canEdit) {
      setIsEditorMode(false);
      return;
    }
    try {
      setIsEditorMode(localStorage.getItem(STORAGE_KEY) === "1");
    } catch {}
  }, [canEdit]);

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
    () => ({
      canEdit,
      isEditorMode,
      toggle,
      isAdmin,
      isEditor,
      roles,
      hasRole: (role) => roles.includes(role),
    }),
    [canEdit, isEditorMode, isAdmin, isEditor, roles]
  );

  return <Ctx.Provider value={value}>{children}</Ctx.Provider>;
}

export function useRoleFlags() {
  const { access } = useAuth();
  const roles = useMemo(() => getRolesFromToken(access), [access]);
  return {
    roles,
    isAdmin: roles.includes("Admin"),
    isEditor: roles.includes("Editor"),
    canEdit: roles.includes("Admin") || roles.includes("Editor"),
    hasRole: (role) => roles.includes(role),
  };
}
