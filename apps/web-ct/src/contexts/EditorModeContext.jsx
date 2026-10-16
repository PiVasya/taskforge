import { rolesFromProfile } from '../auth/profileRoles';
import React, { createContext, useCallback, useContext, useEffect, useMemo, useState } from 'react';
import { useAuth } from '../auth/AuthContext';

const Ctx = createContext(null);
Ctx.displayName = 'EditorModeContext';
export const useEditorMode = () => useContext(Ctx) || { canEdit: false, isEditorMode: false, setEditorMode: () => {}, toggle: () => {}, roles: [], hasRole: () => false };
const KEY = 'ctEditorMode.v1';
export default function EditorModeProvider({ children }) {
  const { user } = useAuth();
  const [isEditorMode, setIsEditorMode] = useState(false);
  const roles = useMemo(() => rolesFromProfile(user), [user]);
  const hasRole = useCallback(
    (role) => roles.some((value) => value.toLowerCase() === String(role).toLowerCase()),
    [roles],
  );
  const canEdit = hasRole('Admin') || hasRole('LearningEditor');
  useEffect(() => {
    if (!canEdit) { setIsEditorMode(false); return; }
    try { setIsEditorMode(localStorage.getItem(KEY) === '1'); } catch { setIsEditorMode(false); }
  }, [canEdit]);
  useEffect(() => {
    if (canEdit) { try { localStorage.setItem(KEY, isEditorMode ? '1' : '0'); } catch {} }
  }, [canEdit, isEditorMode]);
  const setEditorMode = useCallback((value) => {
    if (!canEdit) return;
    setIsEditorMode(Boolean(value));
  }, [canEdit]);
  const toggle = useCallback(() => {
    if (canEdit) setIsEditorMode((value) => !value);
  }, [canEdit]);
  const value = useMemo(
    () => ({ canEdit, isEditorMode, setEditorMode, toggle, roles, hasRole }),
    [canEdit, hasRole, isEditorMode, roles, setEditorMode, toggle],
  );
  return <Ctx.Provider value={value}>{children}</Ctx.Provider>;
}
