import React, { createContext, useCallback, useContext, useEffect, useMemo, useState } from 'react';
import { useAuth } from '../auth/AuthContext';

function parseJwt(token) {
  try {
    const [, payload] = String(token || '').split('.');
    if (!payload) return null;
    const json = atob(payload.replace(/-/g, '+').replace(/_/g, '/'));
    return JSON.parse(decodeURIComponent(escape(json)));
  } catch { return null; }
}
function normalizeRoles(raw) {
  if (!raw) return [];
  if (Array.isArray(raw)) return raw.flatMap(normalizeRoles);
  if (typeof raw === 'string') return raw.split(/[,;]+/g).map((x) => x.trim()).filter(Boolean);
  return [];
}
function getRoles(access) {
  const p = parseJwt(access);
  if (!p) return [];
  return [p.role, p.Role, p.roles, p.Roles, p.primary_role, p['http://schemas.microsoft.com/ws/2008/06/identity/claims/role']]
    .flatMap(normalizeRoles)
    .filter((v, i, a) => v && a.findIndex((x) => x.toLowerCase() === v.toLowerCase()) === i);
}
const Ctx = createContext(null);
export const useEditorMode = () => useContext(Ctx) || { canEdit: false, isEditorMode: false, setEditorMode: () => {}, toggle: () => {}, roles: [], hasRole: () => false };
const KEY = 'ctEditorMode.v1';
export default function EditorModeProvider({ children }) {
  const { access } = useAuth();
  const [isEditorMode, setIsEditorMode] = useState(false);
  const roles = useMemo(() => getRoles(access), [access]);
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
