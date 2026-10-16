import { rolesFromProfile } from '../auth/profileRoles';
import React, { createContext, useCallback, useContext, useEffect, useMemo, useState } from "react";
import { useAuth } from "../auth/AuthContext";

function hasRoleName(roles, role) {
  const wanted = String(role || '').trim().toLowerCase();
  if (!wanted) return false;
  return roles.some((r) => String(r || '').trim().toLowerCase() === wanted);
}

const Ctx = createContext(null);
Ctx.displayName = 'EditorModeContext';
export const useEditorMode = () => useContext(Ctx);

const STORAGE_KEY = "editorMode.v1";

export default function EditorModeProvider({ children }) {
  const { user } = useAuth();
  const [isEditorMode, setIsEditorMode] = useState(false);

  const roles = useMemo(() => rolesFromProfile(user), [user]);
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
  const { user } = useAuth();
  const roles = useMemo(() => rolesFromProfile(user), [user]);
  return {
    roles,
    isAdmin: hasRoleName(roles, "Admin"),
    isEditor: hasRoleName(roles, "Editor") || hasRoleName(roles, "LearningEditor"),
    canEdit: hasRoleName(roles, "Admin") || hasRoleName(roles, "Editor") || hasRoleName(roles, "LearningEditor"),
    hasRole: (role) => hasRoleName(roles, role),
  };
}
