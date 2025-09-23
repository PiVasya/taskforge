import React from "react";
import { Navigate, Outlet, useLocation } from "react-router-dom";
import { useAuth } from "./AuthContext";

export default function ProtectedRoute() {
    const { access, ready } = useAuth();
    const loc = useLocation();

    if (!ready) return <div className="container-app py-10 text-slate-500">Загрузка…</div>;
    if (!access) return <Navigate to="/login" replace state={{ from: loc }} />;
    return <Outlet />;
}
