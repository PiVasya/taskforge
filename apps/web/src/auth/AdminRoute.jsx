import React from "react";
import { Navigate, Outlet, useLocation } from "react-router-dom";
import { useRoleFlags } from "../contexts/EditorModeContext";

export default function AdminRoute() {
  const { isAdmin } = useRoleFlags();
  const loc = useLocation();

  if (!isAdmin) return <Navigate to="/courses" replace state={{ from: loc }} />;
  return <Outlet />;
}
