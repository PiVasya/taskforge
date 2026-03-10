import React from 'react';
import { Navigate, Outlet, useLocation } from 'react-router-dom';
import { useRoleFlags } from '../contexts/EditorModeContext';

export default function FeatureRoute({ requiredRole, fallbackTo = '/courses' }) {
  const { isAdmin, roles } = useRoleFlags();
  const loc = useLocation();
  const ok = isAdmin || roles.includes(requiredRole);

  if (!ok) return <Navigate to={fallbackTo} replace state={{ from: loc }} />;
  return <Outlet />;
}
