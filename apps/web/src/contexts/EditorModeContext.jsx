import React, { createContext, useCallback, useContext, useEffect, useMemo, useState } from "react";
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

function hasRoleName(roles, role) {
  const wanted = String(role || '').trim().toLowerCase();
  if (!wanted) return false;
  return roles.some((r) => String(r || '').trim().toLowerCase() === wanted);
}

const Ctx = createContext(null);
export const useEditorMode = () => useContext(Ctx);

const STORAGE_KEY = "editorMode.v1";

export default function EditorModeProvider({ children }) {
  const { access } = useAuth();
  const [isEditorMode, setIsEditorMode] = useState(false);

  const roles = useMemo(() => getRolesFromToken(access), [access]);
  const isAdmin = hasRoleName(roles, "Admin");
  const isEditor = hasRoleName(roles, "Editor") || hasRoleName(roles, "LearningEditor");
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

  const toggle = useCallback(() => {
    if (!canEdit) return;
    setIsEditorMode((value) => !value);
  }, [canEdit]);

  const hasRole = useCallback((role) => hasRoleName(roles, role), [roles]);

  const value = useMemo(
    () => ({
      canEdit,
      isEditorMode,
      toggle,
      isAdmin,
      isEditor,
      roles,
      hasRole,
    }),
    [canEdit, hasRole, isAdmin, isEditor, isEditorMode, roles, toggle]
  );

  return <Ctx.Provider value={value}>{children}</Ctx.Provider>;
}

export function useRoleFlags() {
  const { access } = useAuth();
  const roles = useMemo(() => getRolesFromToken(access), [access]);
  return {
    roles,
    isAdmin: hasRoleName(roles, "Admin"),
    isEditor: hasRoleName(roles, "Editor") || hasRoleName(roles, "LearningEditor"),
    canEdit: hasRoleName(roles, "Admin") || hasRoleName(roles, "Editor") || hasRoleName(roles, "LearningEditor"),
    hasRole: (role) => hasRoleName(roles, role),
  };
}
